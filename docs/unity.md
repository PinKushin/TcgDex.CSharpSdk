# Using the SDK in Unity

The SDK ships a `netstandard2.0` assembly, which is what Unity consumes. Nothing
here is Unity-specific code — it is the same package, with the packaging and
stripping caveats Unity adds.

## Status: built for it, not yet run in it

Be clear about what has and has not been demonstrated.

**What is verified.** The three things that break a .NET library under Unity's
IL2CPP backend are runtime code generation, reflection-based serialization, and
trimming. The published `netstandard2.0` assembly was inspected at the metadata
level and references **none** of `System.Reflection.Emit`, `DynamicMethod`,
`ILGenerator`, `TypeBuilder`, `CallSite` (the `dynamic` infrastructure),
`MakeGenericType`, `MakeGenericMethod`, `Activator`, or `Expression.Compile`.
JSON is source-generated rather than reflected. The query builder walks
expression trees structurally instead of compiling them.

The reflective path that *does* exist — reading a captured local out of its
compiler-generated closure — is exercised by the Native AOT smoke test, which
publishes with full trimming and no JIT fallback. That is a stricter regime than
Unity's managed stripping.

**What is not verified.** Nobody has run this inside a Unity project. IL2CPP and
Native AOT are the same *class* of constraint, not the same implementation, so
the evidence above is strong rather than conclusive. If something does break
under Unity, the closure-reading path in `ExpressionTranslator` is where to look
first, and [`link.xml`](#il2cpp-and-managed-stripping) is the remedy.

## Requirements

| | |
|---|---|
| API Compatibility Level | **.NET Standard 2.1** (Player Settings → Other Settings). `.NET Framework` also works. |
| Scripting backend | Mono or IL2CPP — both fine. |
| Platforms | Everything except **WebGL**. See [WebGL](#webgl) below. |

`netstandard2.1` is a superset of `netstandard2.0`, so the shipped assembly is
compatible with either profile.

## Installing

### The easy way

[NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) resolves the
dependency graph for you:

```
TcgDex.CSharpSdk
```

That is the whole step. Skip to [duplicate assembly errors](#duplicate-assembly-errors).

### By hand

Unity does not read `PackageReference`, so without NuGetForUnity you place DLLs
in `Assets/Plugins/` yourself. The `netstandard2.0` closure is **21 assemblies**:

| Assembly | Version |
|---|---|
| `TcgDex.CSharpSdk.dll` | 0.1.1 |
| `Microsoft.Extensions.Http.dll` | 8.0.1 |
| `Microsoft.Extensions.Logging.dll` | 8.0.1 |
| `Microsoft.Extensions.Logging.Abstractions.dll` | 8.0.3 |
| `Microsoft.Extensions.DependencyInjection.dll` | 8.0.1 |
| `Microsoft.Extensions.DependencyInjection.Abstractions.dll` | 8.0.2 |
| `Microsoft.Extensions.Options.dll` | 8.0.2 |
| `Microsoft.Extensions.Primitives.dll` | 8.0.0 |
| `Microsoft.Extensions.Configuration.Abstractions.dll` | 8.0.0 |
| `Microsoft.Bcl.AsyncInterfaces.dll` | 8.0.0 |
| `Microsoft.Bcl.TimeProvider.dll` | 8.0.1 |
| `System.Text.Json.dll` | 8.0.6 |
| `System.Text.Encodings.Web.dll` | 8.0.0 |
| `System.Net.Http.Json.dll` | 8.0.1 |
| `System.Diagnostics.DiagnosticSource.dll` | 8.0.1 |
| `System.ComponentModel.Annotations.dll` | 5.0.0 |
| `System.Buffers.dll` | 4.5.1 |
| `System.Memory.dll` | 4.5.5 |
| `System.Numerics.Vectors.dll` | 4.4.0 |
| `System.Runtime.CompilerServices.Unsafe.dll` | 6.0.0 |
| `System.Threading.Tasks.Extensions.dll` | 4.5.4 |

To produce that set without hunting through nuget.org, restore it once from a
throwaway project and copy what lands in the package cache:

```bash
dotnet new classlib -f netstandard2.0 -o unity-deps
cd unity-deps
dotnet add package TcgDex.CSharpSdk
dotnet build
```

The `lib/netstandard2.0/` folder of each restored package under
`~/.nuget/packages/` holds the DLL to copy.

## Duplicate assembly errors

The most likely thing to go wrong, and it is a packaging problem rather than a
compatibility one.

Seven of those assemblies are **polyfills** — they backport APIs that
`netstandard2.1` already has in the box:

```
System.Buffers.dll
System.Memory.dll
System.Numerics.Vectors.dll
System.Runtime.CompilerServices.Unsafe.dll
System.Threading.Tasks.Extensions.dll
System.ComponentModel.Annotations.dll
Microsoft.Bcl.AsyncInterfaces.dll
```

On a `.NET Standard 2.1` profile Unity supplies these itself, so shipping them
too can produce *"The type `X` exists in both …"* or a duplicate-assembly load
error. If that happens, **delete them from `Assets/Plugins/` one at a time**
until the error clears — the rest of the set is still required.

Which of the seven actually collide depends on the Unity version, so this is
written as a troubleshooting step rather than a fixed list to remove up front.

## IL2CPP and managed stripping

The SDK reads a captured variable out of its closure reflectively, so that
`.Where(c => c.Hp > minimumHp)` works without compiling the expression tree —
`Expression.Compile()` emits IL at runtime and IL2CPP cannot support it.

Managed stripping can remove members that only reflection reaches. If you build
with **Managed Stripping Level: High** and see queries with captured variables
producing empty or wrong filters, add `Assets/link.xml`:

```xml
<linker>
  <assembly fullname="TcgDex.CSharpSdk" preserve="all" />
</linker>
```

That is a blunt instrument — it preserves the whole assembly. A narrower rule is
possible, but the assembly is small enough that it is rarely worth the tuning.

Note the failure mode: a stripped closure field surfaces as a *wrong query*,
not an exception. Assert on `ToQueryString()` in a test rather than trusting a
successful build.

## Async on Unity's main thread

Every await inside the SDK uses `ConfigureAwait(false)`, which is correct for a
library. A common misreading is that this strands *your* continuation off the
main thread. It does not: `ConfigureAwait` only governs where the awaiting
method resumes, so the SDK's setting applies to the SDK's own internals. Your
`await` captures Unity's synchronization context, and your code resumes on the
main thread.

```csharp
private async void Start()
{
    Card? card = await _tcgdex.Cards.GetAsync("swsh3-136", destroyCancellationToken);

    // Main thread. This await captured Unity's synchronization context, which
    // is what decides where *this* method resumes.
    _label.text = card?.Name;
}
```

Where it does bite is your own helper layers. If you write
`await SomethingAsync().ConfigureAwait(false)` in a helper and then touch a
`UnityEngine` API, that throws — and the SDK is not involved in the mistake.

`destroyCancellationToken` (Unity 2022.2+) is the right token to pass: it cancels
when the object is destroyed, so a scene change does not leave requests running
against a dead object.

## Lifetime

Create **one** client for the application, not one per request — a disposed
`HttpClient` leaves connections in `TIME_WAIT` and exhausts sockets. Unity has no
DI container by default, so `TcgDexClient.Create()` is the entry point:

```csharp
public sealed class TcgDexService : MonoBehaviour
{
    public static TcgDexService Instance { get; private set; } = null!;

    internal ITcgDexClient Client { get; private set; } = null!;

    private TcgDexClient _owned = null!;

    private void Awake()
    {
        Instance = this;
        DontDestroyOnLoad(gameObject);

        _owned = TcgDexClient.Create(configureCache: _ => { });
        Client = _owned;
    }

    private void OnDestroy() => _owned.Dispose();
}
```

`configureCache: _ => { }` enables response caching with defaults, which matters
more in a game than in a service — repeat lookups of the same card cost nothing
and the `ETag` revalidation keeps them correct.

## WebGL

**`System.Net.Http` does not work on WebGL.** The browser sandbox has no sockets,
and Unity's WebGL runtime is single-threaded. This is a platform restriction, not
an SDK limitation — no `HttpClient`-based library works there.

The SDK is usable anyway, because the transport is injectable. It takes an
`HttpClient`, and an `HttpClient` takes an `HttpMessageHandler`, so a handler
backed by `UnityWebRequest` makes the whole SDK work on WebGL unchanged:

```csharp
internal sealed class UnityWebRequestHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using UnityWebRequest unityRequest = UnityWebRequest.Get(request.RequestUri);

        // Await the UnityWebRequestAsyncOperation, then translate the result
        // into an HttpResponseMessage — status code, body, and the ETag header
        // if you want the SDK's caching layer to keep working.
        // ...
    }
}

ITcgDexClient client = new TcgDexClient(new HttpClient(new UnityWebRequestHandler()));
```

Nothing else changes: the SDK spawns no threads, calls no `Task.Run`, and never
blocks on a task, so the single-threaded WebGL model is otherwise fine.

A complete handler is more code than fits here, and none of it is SDK-specific —
it is the standard `UnityWebRequest`-to-`HttpClient` adapter, and existing
implementations can be dropped in.

## Reporting a problem

Since this page describes a configuration nobody has run end to end, a bug
report from an actual Unity project is genuinely useful. Include the Unity
version, scripting backend, API compatibility level, managed stripping level and
target platform — those five determine almost everything about what can go
wrong here.
