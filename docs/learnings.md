# Learnings

Non-obvious things discovered while building this SDK, with the evidence that
established them. Each entry exists because getting it wrong cost time or would
have shipped a bug.

---

## System.Text.Json's source generator discards property initializers

**Severity: high — this ships NullReferenceExceptions to consumers.**

A collection property written the obvious way:

```csharp
public IReadOnlyList<Attack> Attacks { get; init; } = [];
```

deserializes to **`null`**, not to an empty list, when the JSON omits the
property. The `= []` is silently discarded.

Verified with an isolated probe against .NET 10.0.302 — all three cases produce
`null`:

| Case | Result |
|---|---|
| No `required` members, property absent (`{}`) | `null` |
| With `required` members, property absent | `null` |
| Explicit `"items": null` in the JSON | `null` |

This is not specific to `required`, and not specific to omitted properties.

**Why it matters here:** most `Card` fields are category-specific. A Trainer card
has no `attacks` key at all, so `foreach (var a in card.Attacks)` on any Trainer
would have thrown. The contract test
`Deserialize_TrainerCard_MapsTrainerTypeAndEffect` caught it.

**The fix** is a backing field with a coalescing `init` accessor, applied to
every collection on every model:

```csharp
private readonly IReadOnlyList<Attack> _attacks = [];

public IReadOnlyList<Attack> Attacks
{
    get => _attacks;
    init => _attacks = value ?? [];
}
```

This covers all three failure modes at once — absent property, explicit JSON
null, and a null passed by a caller constructing the record by hand. Both are
pinned by tests (`Deserialize_AbsentCollections_AreEmptyNotNull` and
`Construct_WithExplicitNullCollection_CoercesToEmpty`).

**Rule:** any new collection property on a model must use this pattern. A plain
initializer will compile, pass review, and fail at runtime.

---

## `IDE0005` needs `GenerateDocumentationFile` to run during build

Setting `dotnet_diagnostic.IDE0005.severity = warning` is not enough. Without
`<GenerateDocumentationFile>true</GenerateDocumentationFile>` the build fails
with a meta-error rather than reporting unused usings:

> `CSC : error EnableGenerateDocumentationFile: Set MSBuild property 'GenerateDocumentationFile' to 'true' in project file to enable IDE0005`

The property is therefore set in `Directory.Build.props` for every project, and
test projects suppress the resulting `CS1591` (missing XML comments) via
`NoWarn`.

**Consequence for the global-usings convention:** because `IDE0005` is an error,
a namespace declared in `GlobalUsings.cs` before any code uses it fails the
build. Global usings must be added *alongside* the code that needs them, not up
front. The `TcgDex.CSharpSdk.IntegrationTests` global-usings file is
deliberately empty for this reason until its first test exists.

---

## EditorConfig directory globs do not reliably match the test projects

This looked correct and did nothing:

```editorconfig
[**/{*.Tests,*.IntegrationTests}/**/*.cs]
dotnet_diagnostic.CA1707.severity = none
```

`CA1707` kept firing. Analyzer exclusions that must apply to a whole project now
live in that project's `.csproj` as `<NoWarn>`, which is unambiguous:

```xml
<NoWarn>$(NoWarn);CA1707;CS1591;CA1062</NoWarn>
```

Worth remembering generally: a non-matching editorconfig section fails silently
and looks configured.

---

## `CA1707` conflicts with the required test-naming convention

The engineering standards mandate `{Method}_{Scenario}_{Expected}` test names.
`CA1707` ("remove the underscores from member name") flags exactly that. The
rule is suppressed in the test projects only — the convention wins, because the
underscores carry meaning there.

---

## `CA1716`: keeping the type name `Set`

`Set` collides with a VB.NET keyword, which `CA1716` flags. Suppressed for
`Models/*.cs` only, deliberately: the model types mirror TCGdex's own vocabulary
(`Card`, `Set`, `Serie`) so the SDK surface maps one-to-one onto the API
reference. Renaming to `CardSet` would break that correspondence for anyone
reading both. `CA1716` still applies everywhere else in the SDK.

---

## Multi-targeting `net8.0;net10.0` works with only the .NET 10 SDK installed

No separate targeting pack install was needed — the reference assemblies restore
from NuGet automatically. `net8.0` is the current LTS and `net10.0` is the
newest, so the package covers both without the consumer needing a specific SDK.

`System.Text.Json` is referenced as a package only on `net8.0`; on `net10.0` it
comes from the shared framework.

---

## Native AOT publish needs `vswhere.exe` on PATH

Publishing the smoke test failed at the final link step with:

> `error MSB3073: The command ""'vswhere.exe' is not recognized as an internal or external command,;operable program or batch file.;F:\VisualStudio2026\...\link.exe" ...` exited with code 123`

The MSVC linker was present; the ILCompiler targets shell out to `vswhere.exe`
unqualified to locate it, and the failure message got concatenated into the
link command. `vswhere.exe` ships at a fixed location that is **not** on PATH by
default:

```
C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe
```

Prepend that directory to PATH and the publish succeeds, or run from a Visual
Studio Developer prompt where it is already set. Worth knowing that this happens
regardless of which drive Visual Studio is installed on — here it is on `F:`,
while `vswhere` remains on `C:`.

Verified result: a 3.02 MB self-contained native binary with no managed DLLs
beside it, and all six smoke checks passing.

---

## The query builder walks expression trees, never compiles them

`Expression.Compile()` emits IL at runtime, which Native AOT cannot do. Since
the SDK is built to stay AOT-compatible, `ExpressionTranslator` inspects the
tree structurally and reads captured variables straight off their compiler-
generated closure with `FieldInfo.GetValue`. That is a metadata read rather than
code generation, so it survives AOT — and it is cheaper than compiling, because
no codegen happens at all.

Verified rather than assumed: no `.Compile(`, `Reflection.Emit`,
`Activator.CreateInstance` or `MakeGenericType` anywhere in the SDK, and the
trim/AOT analyzers report no `IL2xxx`/`IL3xxx` warnings.

### Why not `IQueryable<Card>`

The API supports exactly ten operators. An `IQueryable` implementation would
have to throw for most of LINQ — `Select` projections, `Join`, `GroupBy`,
`Any`, arbitrary `Where` bodies — which is a partial interface implementation
that fails at runtime rather than at the call site. A dedicated builder makes
the supported surface explicit and keeps every rejection a precise, actionable
message naming the offending expression.

One consequence worth knowing: `||` is only representable **within a single
field** (`name=eq:Furret|Pikachu`). An OR across two fields has no encoding, so
it throws rather than silently dropping half the predicate and returning
plausible-looking wrong data.

### Generated query strings are checked against the live API

The exact string the builder produces was replayed against the real service —
the combined filter/sort/pagination query, a value containing an escaped `&`,
the `fu*` wildcard, and `not:` — all return correct results. Unit assertions
alone would only prove the builder is self-consistent.

---

## Transport tests are mutation-checked, not just green

A test that passes on first write proves nothing until you have seen it fail.
The URL assertions were verified by reintroducing the exact defect the previous
SDK shipped — wrapping the query string in an invented `?q=` parameter — and
confirming that `GetAsync_PreservesQueryStringVerbatim` failed, and that it was
the only failure.

That defect passed the old suite because its mock handler ignored the `request`
argument entirely, so no test ever observed a URL.
`Http/RecordingHandler.cs` exists to make that class of blind spot impossible:
it records every request, exposes the URI for assertion, and fails loudly with
the offending method and URL when the code makes an unexpected extra call.

Worth repeating for any new area: if a test has never been red, confirm it can
be.

---

## Test fixtures are recorded live responses, not hand-written JSON

Every file in `TcgDex.CSharpSdk.Tests/Fixtures/` was captured from the live API
and is loaded through the SDK's own `TcgDexJsonContext`, so the tests exercise
the exact serializer configuration that ships.

Hand-written fixtures would have hidden every bug in this document: the
polymorphic `damage` field, the object-array `boosters`, the missing `image` on
`exu-!`, and the initializer problem above were all found because the recorded
payloads are irregular in ways invented test data never is.

See `docs/api-info.md` §9 for what each fixture card is for.
