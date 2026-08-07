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
from NuGet automatically. `net8.0` is the oldest .NET still in support and
`net10.0` is the current LTS, so those two targets cover every supported runtime:
.NET 6 and 7 are past end of life, and 9 and 11 consume the `net8.0` asset by
roll-forward. (`net8.0` itself falls out of support on 2026-11-10, at which
point the floor becomes `net10.0`.)

`System.Text.Json` is **not** referenced as a package on either target. Both ship
it in the shared framework, source generator included, and adding the reference
back raises `NU1510` on `net8.0` — NuGet's way of saying the framework already
provides it. Leaving it out is also what keeps consumers on the serviced,
security-patched copy: they upgrade it by patching .NET rather than by waiting
for this package to bump a version.

---

## Multi-targeting the library while testing one framework proves half of it

The test projects were `net10.0` only while the SDK shipped `net8.0` and
`net10.0`, so the `net8.0` assembly was build-verified and never executed. Making
the unit tests multi-target found two compile errors immediately:

- A test double used `System.Threading.Lock`, which is .NET 9+.
- Two assertions called Shouldly's `ShouldContainKey`, which binds to
  `IDictionary<,>`; the properties are `IReadOnlyDictionary<,>`, and only the
  `net10.0` Shouldly build had an overload that matched. Asserting on `.Keys`
  works on both and prints the actual key set on failure.

Neither was in the SDK, but neither could have been *found* without running the
older target — and the difference between targets is more than compiler symbols:
`System.Text.Json` resolves from a different assembly version, so serializer
behaviour is genuinely not the same code. If a library multi-targets, its tests
should too.

---

## CodeQL: `paths-ignore` does not work for compiled languages

The first scan produced **212 alerts, 211 of them in `*.g.cs` files** emitted by
System.Text.Json's generator — `cs/useless-cast-to-self`, none with a security
severity. Zero findings in hand-written code.

The obvious fix does not work. `paths-ignore: "**/*.g.cs"` loads correctly — the
init log says *Using configuration file input from workflow* — and changes
nothing, because **for compiled languages CodeQL cannot exclude files the
compiler pulls into the build**, and the generator emits into `obj/` during
`dotnet build`. Confirming the config had loaded before blaming the pattern is
what turned this from guesswork into a two-line fix.

`query-filters` is the mechanism that works:

```yaml
query-filters:
  - exclude:
      id: cs/useless-cast-to-self
```

Excluding a whole rule is only acceptable because nothing is lost: redundant
casts in code we own are caught by IDE0004 under `TreatWarningsAsErrors`, so the
check still exists where its results are actionable.

The one genuine finding was **declined, not fixed**. `cs/linq/missed-where`
wanted `CatalogEndpoints.Any(e => …)` in place of a `foreach`; the LINQ form
reads better and allocates a delegate and a closure per call, on the caching
path, in an SDK that keeps allocations off the hot path deliberately. It is
dismissed as *won't fix* with that reason and documented at the method, rather
than the rule being excluded repo-wide — so a real instance elsewhere still
surfaces.

The point of all this is the Security tab: alerts nobody can act on train
everyone to ignore the one place a real finding would appear.

---

## A passing CI run can still be reporting a problem

Adding `net472` to the test project broke CI on every push and I did not notice,
because Actions was degraded at the time and I read the missing result as the
outage. GitHub's ubuntu runners have no mono, and **vstest does not skip a
framework it cannot host** — it aborts the whole run with *Could not find 'mono'
host*, taking the net8.0 and net10.0 results down with it. The test project now
targets `net472` only on Windows, with a dedicated `windows-latest` job so the
framework stays covered in CI rather than only locally.

Then every job passed while CI quietly did the wrong thing. The fix above had
been anchored on the ubuntu job's `pack` step, which moved that job's two upload
steps into the new Windows job. The package and test results stopped being
uploaded, and the only evidence was an annotation:

```
No files were found with the provided path: ./artifacts/*.nupkg.
No artifacts will be uploaded.
```

Nothing failed, because `upload-artifact` treats "nothing matched" as a warning
rather than an error — and the annotation was attached to a job that had no
business uploading a package in the first place.

**A passing status only means no step returned a non-zero exit code.** It does
not mean the run was clean. Here it meant artifacts had silently stopped being
produced; elsewhere in the same session it meant a deprecation notice (CodeQL
Action v3, removed December 2026) that would otherwise have sat in the log for
four months. Both are the same failure to read past the tick, which is why
annotations are treated as build output in this repo and held at zero:

```bash
gh api repos/{owner}/{repo}/check-runs/{job-id}/annotations --jq 'length'
```

---

## An SDK has two untrusted inputs, and hardening one is not hardening both

Worth separating, because the defences are unrelated:

**The server's responses.** A body is buffered before deserialization, so
without a ceiling the peak memory of a request is whatever the server sends —
and automatic decompression sharpens that, since a few kilobytes of hostile gzip
expand to gigabytes. `TcgDexOptions.MaxResponseBytes` (32 MiB default, against a
2.4 MB largest real response) bounds it. Three details decide whether such a
limit works:

- **It lives in the transport, not on `HttpClient.MaxResponseContentBufferSize`.**
  Callers may supply their own `HttpClient`, and that is precisely the case an
  SDK cannot configure.
- **`Content-Length` is a claim, not proof.** It is worth acting on when it
  already admits the body is too large, but the bytes are counted while reading
  because a hostile sender simply lies or omits it.
- **The check happens before each write to the buffer**, so an oversized body is
  abandoned at the limit rather than fully buffered and then rejected — which
  would concede exactly the memory the limit exists to protect.

**The caller's arguments.** Consumers are programmers, who probe harder than end
users. Card ids go into the URL path, so an id is untrusted input: they are
escaped with `Uri.EscapeDataString`, which turns `/` into `%2F` that `Uri` never
decodes, so no id can move a request off the configured path.

Two things deliberately *not* done, because both would be inventing protection:

- **No cap on id length.** .NET Core removed the `Uri` length limit, so a
  1 MB id builds a 1 MB URL and unicode expands ~6× through escaping. It is
  wasteful, but the server answers 414 and that is the right authority. A cap
  here could reject a legitimate id the API later introduces.
- **No test for deeply nested JSON.** `Utf8JsonReader` tracks depth with a bit
  stack rather than recursion and these models are shallow, so there is no stack
  to exhaust; `MaxDepth` 64 is a backstop, not the thing preventing a crash. A
  test would also have passed regardless, since `[[[[…]]]]` fails to deserialize
  into a `Card` at any depth.

---

## Check the wrapped service before deciding an input is invalid

A filter value of a single `*` threw `ArgumentOutOfRangeException` out of
`QueryFilter.EscapeValue`: the wildcard detection counted the one character as
both a leading *and* a trailing wildcard, stripped it twice, and computed a
negative substring length.

The fix was one character — `value.Length > 1` on the trailing test, so a lone
asterisk counts once. The part worth keeping is how the *correct behaviour* was
decided, because "reject it with a better exception" was equally plausible:

- **The API was asked.** `GET /v2/en/cards?name=*` returns **200 with every
  card**. It is a legitimate match-anything query, not a malformed one. So was
  `name=` with no value; only `name=eq:` (equality against the empty string)
  returns `[]`.
- **The official SDK was read.** The JavaScript SDK's `Query.contains` pushes
  the value through untouched — it does no wildcard parsing at all, so `*`
  reaches the wire exactly as written. Throwing would have made this SDK reject
  a query every other client can express.

Neither answer was available from inside the repository. A wrapper does not get
to invent validation the wrapped service does not have; that turns a working
query into a crash and makes the SDK the least capable client of the API.

Also worth noting what did *not* fail: `StartsWith("")` and `EndsWith("")` were
my first guess at the trigger, and both are already rejected upstream by a
deliberate `NotSupportedException` in `ExpressionTranslator`. The reachable path
was a caller passing a literal `"*"` — a user typing the wildcard they know the
API understands. Guessing the trigger and confirming it are different acts; the
test only became meaningful once it failed with `length ('-1')`.

The unit test proves the query string is `name=*`. A live integration test
proves the API accepts it — the same split that caught the set-logo URL bug,
where a string assertion happily confirmed a URL that 404s.

---

## Supporting netstandard2.0 cost portability work, not API compromises

Adding `netstandard2.0` reaches Unity and .NET Framework 4.6.1+. The public
surface, async, and nullability are unchanged — what it cost was 31 call sites
using BCL APIs that target does not have. The shape of the work, for anyone
adding a target later:

- **Language features lower onto BCL types.** `init` needs `IsExternalInit`,
  `required` needs `RequiredMemberAttribute` and `CompilerFeatureRequired`. The
  compiler only checks those types *exist* — declaring them internally is the
  supported approach, and 318 of the initial 520 errors were that one gap.
- **`LangVersion` must be set explicitly.** `netstandard2.0` otherwise defaults
  to C# 7.3 and nothing modern compiles. Already set repo-wide here.
- **Prefer a real backport to an `#if`.** `TimeProvider` exists as
  `Microsoft.Bcl.TimeProvider`, so the cache's clock abstraction stayed one code
  path. The same for `System.Text.Json`, `IAsyncEnumerable`, and
  `ActivitySource` — all packages, no forked code.
- **Guard helpers beat conditional compilation.** `ArgumentNullException.ThrowIfNull`
  is .NET 6+, and statics cannot be added to a type from outside, so 33 call
  sites moved to `Guard`. Teach CA1062 about it via
  `dotnet_code_quality.CA1062.null_check_validation_methods`, or every guarded
  public method is reported as unvalidated.
- **The netstandard2.0 BCL has no nullability annotations.** `string.IsNullOrWhiteSpace`
  lacks `[NotNullWhen(false)]`, so code the modern targets accept produces
  CS8602 there. Fixed with explicit `is null ||` tests, not `!`.
- **Only two `#if` blocks survived**, and both are genuine platform differences
  rather than missing sugar: `SocketsHttpHandler` versus
  `ServicePoint.ConnectionLeaseTimeout`, and span-based `int.Parse`.

The `#if` count is the metric worth watching. Every one is a place where the
targets can drift apart and only one of them is tested.

---

## `netstandard2.0` is compiled, never executed — so run the tests on net472

A `netstandard2.0` assembly cannot run on its own; it is a contract. Adding the
target left the shipped DLL build-verified and never executed — the same trap as
testing only one target framework, one level deeper.

`net472` in the test project closes it: .NET Framework resolves the
`netstandard2.0` asset, so the suite runs against exactly the DLL a Unity or
WinForms consumer receives. It immediately found four things the compiler had
not: `HttpStatusCode.TooManyRequests`, `DateTimeOffset.UnixEpoch`,
`Task.IsCompletedSuccessfully`, and `ValueTask.FromResult` are all absent from
.NET Framework.

It also answers "how do we test the compiler shims?" — you don't, directly.
`IsExternalInit` and `RequiredMemberAttribute` have no behaviour and no caller;
a test asserting one exists would restate the compiler's own requirement. What
needs proving is that `init` and `required` *work* there, and 340 tests
deserializing into models built entirely from them prove exactly that.

Caveat worth recording: coverlet does not collect coverage from the `net472`
run, so netstandard2.0-only code is invisible to the coverage gate. That is why
`HttpContentExtensions` is wrapped in `#if NETSTANDARD2_0` — on modern targets
the instance methods win overload resolution and it would be shipped code that
nothing can reach, showing up as an uncoverable hole.

---

## Security analyzers are opt-in, and worth turning on

`AnalysisLevel=latest-recommended` leaves most of the CA3xxx injection and
CA5xxx cryptography rules **off**. `<AnalysisModeSecurity>All</AnalysisModeSecurity>`
enables the category, and with `TreatWarningsAsErrors` a finding fails the build.

`<NuGetAudit>` with `NuGetAuditMode=all` and `NuGetAuditLevel=low` fails
*restore* on a dependency with a known advisory, transitive ones included — the
check that matters most here, since the netstandard2.0 target pulls a 8.0.x
graph the modern targets get in-box and service automatically.

Both were verified in the failing direction, per the rule above. Restoring
`System.Text.Json` 8.0.0 produced `NU1903` for two advisories — the same ones
that motivated pinning 8.0.6. A deliberate `SHA1.Create()` produced `CA5350`.

The limit is worth recording alongside: a deliberate `new Random().Next()` in
the same probe was **not** flagged. These rules match known-dangerous API
patterns, not weak logic. They are a floor, not an audit.

---

## A library's dependency versions are a floor for every consumer

Central Package Management made it easy to pin one version of
`Microsoft.Extensions.*` for all targets, and the `net8.0` build was asking for
`10.0.10`. That works — those packages support `net8.0` — but it forces a .NET 8
app to drag its whole `Microsoft.Extensions` graph to 10.0.x, and collides with
an ASP.NET Core 8 app that pins `8.0.x`.

A library should request the **lowest** version that satisfies it, per target,
because NuGet resolves upwards on its own: a consumer already on 10.0.x still
gets 10.0.x. `Directory.Packages.props` now conditions its `PackageVersion`
items on `$(TargetFramework)`.

Caught by reading the generated `.nuspec` out of the packed `.nupkg` rather than
by any build failure — dependency versions are baked into a published version
permanently, so the `.nuspec` is worth reading once before the first push.

---

## Four of the 18 advertised languages have no card data

The API enumerates 18 language codes in its own error payload, and all 18 route
successfully. But `pt-pt`, `nl`, `pl` and `ru` return **HTTP 200 with empty
arrays** — no cards, empty catalogs. `nl`, `pl` and `ru` do carry a few sets
(3, 2 and 9) with no cards in them; `pt-pt` is empty entirely.

Accepted is not the same as populated. A client must treat an empty result as
valid rather than as a failure, which is what the SDK does.

## Card ids are not universal across languages

Each language is backed by its own card pool. `swsh3-136` is a Western card and
returns **404** in `ja`, `ko`, `th`, `id`, `zh-cn` and `pt-br` — those databases
contain different sets entirely.

Consequence for tests and for callers: to work in an arbitrary language, take
ids from that language's own list endpoint rather than assuming a shared id
resolves. The integration suite proves each language is live by asking it for
its *own* first card, not for a fixed id.

Where the pool is shared, names are genuinely localised: `swsh3-136` is *Furret*
in `en`, *Fouinar* in `fr`, *Wiesenior* in `de`.

## `JsonConverter.HandleNull` is false, so null branches are dead code

Both custom converters had a `JsonTokenType.Null` case and a null check in
`Write`. Tests covering null passed — while those exact lines stayed uncovered.

The reason: `JsonConverter<T>.HandleNull` defaults to `false`, so
System.Text.Json handles a null value itself and never invokes the converter.
The branches could not run. They were deleted rather than tested.

Worth remembering as a general point: a line that stays dark while a test
covering that scenario passes is evidence the line cannot run, not evidence the
test is missing.

## Set logos and card artwork are addressed differently

Both are base URLs without an extension, and the fields sit side by side on the
model — but the URL forms differ:

```
card artwork  {base}/{quality}.{format}   .../136/high.png    200
set logo      {base}.{format}             .../logo.png        200
set logo      {base}/{quality}.{format}   .../logo/high.png   404
```

Card artwork takes a quality segment; set logos and symbols take none. Applying
the card pattern to a logo returns 404.

This was assumed away when the image helpers were first written, and the
integration test that fetched the generated URL is what caught it. A unit test
asserting the string would have happily confirmed the wrong answer — the URL
only reveals itself as wrong when something requests it.

`GetImageUrl` therefore takes a quality and `GetLogoUrl` / `GetSymbolUrl` do not,
so the distinction is enforced by the signature rather than by remembering.

---

## A live test must not assert on data the service is free to change

Two integration tests asserted that a second read returns `304`, using
`swsh3-136` — a card embedding market pricing that TCGdex updates server-side.
When an update landed between the two reads, the ETag changed and `200` became
the *correct* answer, failing the test for something that was never a defect.
It reproduced roughly one run in three, and only in the full suite, which is the
worst possible signature: rare enough to look like noise, frequent enough to
erode trust.

The fix is not a retry. It is to assert against a resource with no volatile data
— the rarity list — and to cover the volatile case by asserting only what is
actually guaranteed: the result is correct, and a zero freshness window never
serves without asking.

**Rule:** a live assertion must be about behaviour the SDK controls, or about a
resource the service will not change underneath it. Anything else is a
time bomb.

---

## Eliminating explanations beats optimising guesses

Six attempts to speed up deserialization, five of which did nothing. Recorded
together because the pattern is more useful than any single result:

| Attempt | Effect |
|---|---|
| Deserialize from UTF-8 bytes, not a decoded string | 43.3 → 34.6 KB |
| **Rent the read chunk from `ArrayPool`** | **34.6 → 18.6 KB** |
| Pre-size the response buffer from `Content-Length` | none |
| Pre-size the pricing dictionary | *worse* |
| Remove `PropertyNameCaseInsensitive` | none |
| `ValueTextEquals` instead of `GetString` for metadata keys | 2% allocations, no time |

The one that mattered was not a clever technique. It was noticing that a
**16 KB per-request scratch buffer was larger than the 2.9 KB payload it was
reading**. Twice, pre-sizing a buffer — the most obviously correct optimisation
available — measured as nothing or as a regression.

Then Newtonsoft.Json settled the rest as a *control* rather than a candidate. It
is a wholly independent implementation, not a wrapper over System.Text.Json, so
running the identical model through it eliminated two explanations at once: the
model shape is not pathological (Newtonsoft is slower still, and needs no
converters), and the serializer choice is not wrong (source generation
comfortably beats it). What was left — source generation losing to
System.Text.Json's own reflection path — is narrow, understood, and kept on
purpose, because reflection breaks Native AOT.

The lesson is about order. Every one of the five failures was a plausible guess
acted on before the cost had been located; the one success and both eliminations
came from measuring where the time actually was first. Deserialization turned
out to be **86% of the request path**, which nothing before the benchmark had
established.

---
## A benchmark only tells you about the size you ran it at

Everything above was measured on one card of 2,938 bytes. Two of its conclusions
did not survive being re-run on the 2.3 MB unpaginated card list and a cache at
its bound — and the failures point in opposite directions, which is the useful
part.

**A cost dismissed as fixed turned out to scale.** Pre-sizing the response
buffer from `Content-Length` measured as nothing at 2.9 KB and went into the
"ruled out" list. At 2.3 MB it saves **2.24 MB per request**, because that is
where the doubling growth of a `MemoryStream` starts to matter. The code had
been left in place anyway; the conclusion about it was simply wrong.

**A cost dismissed as small turned out to be the whole thing.** Source
generation losing to reflection by ~5 µs on a card looked like a fixed per-call
overhead that a real payload would drown. It is proportional: 0.81× time at
2.9 KB, 0.85× at 2.3 MB. The AOT guarantee costs 15–20% of deserialization at
every size, not a flat 5 µs.

The cache had the sharper version of the same problem. `CachingBenchmarks`
reported the caching layer's overhead as ~0.8 µs — measured, honestly, on a
cache that had never reached its bound. Eviction only runs when a cache is full,
and once it is full it runs on *every* store. Measured in that state, a store
cost 14 µs at the default bound and 49 µs at 4096. The published 0.8 µs was true
of a state that a long-running application leaves within minutes and never
returns to.

**Pick the size the code will actually see, and the state it will actually be
in.** A benchmark that only exercises the easy case is not neutral; it publishes
a number that is wrong in the direction you would have chosen.

---
## `ConcurrentDictionary.Count` is not a field read

It acquires every lock in the dictionary and sums the per-lock counters, and the
lock array grows with the table. Isolated:

| Entries | `Count` |
|---:|---:|
| 64 | 299 ns |
| 512 | 4,701 ns |
| 4096 | 18,925 ns |

`MemoryTcgDexResponseCache.SetAsync` called it on every store to check its
bound. At 4096 entries that check cost **17× the entire store operation
containing it** — the cheapest-looking line in the class was its most expensive.

The reason this went unnoticed is worth more than the fix. The suspect was the
eviction scan, which genuinely was O(n) and genuinely did run on every store
once the cache filled. Batching it to `MaxEntries/8` — an amortisation that
should have bought about 6× — bought 1.3×, and *that* was the signal: when a
fix aimed at the presumed bottleneck barely moves the number, the bottleneck is
somewhere else. Decomposing the operation into its parts found it in one run.

The replacement is a counter maintained incrementally, which needs `TryAdd`
rather than the indexer so that replacing an entry is not counted as growth, and
which is re-derived inside eviction so drift from concurrent add/remove races
cannot accumulate. The public `Count` still asks the dictionary, because callers
are entitled to an exact answer; only the internal bound check uses the cheap
one.

---
## Mutation testing rejects designs, not just tests

The first eviction implementation scanned the live dictionary into `ArrayPool`
buffers. It was faster and allocated less. Stryker found three mutants in it
that no test could kill:

- the empty-cache early return,
- the guard against the dictionary growing mid-scan,
- the buffers' return path, whose deletion changes nothing observable.

All three exist *because* the scan reads a structure that other threads are
modifying. They are not missing tests — the first two need a race to reach, and
the third is a pure leak with no visible effect.

`ConcurrentDictionary.ToArray()` takes the locks once and returns a consistent
snapshot, so none of the three are needed. It allocates more (385 B per store
against 256 B) and it is only affordable because eviction now runs once per
batch rather than once per store. The file went from 86.54% to **100%**.

The general shape: **an uncoverable branch is usually a design telling you it
chose the harder correctness problem.** The right response is sometimes a
cleverer test, and sometimes deleting the branch by not needing it.

One of the surviving mutants was not equivalent at all. Deleting the count reset
in `Clear()` left every existing test passing, and it is a real bug: the cache
would still believe it was full, so the next store would trip the bound and
evict the entry just written. A clear-then-store sequence — the obvious thing a
consumer does — would silently lose data.

---
## Benchmarking against a competitor, and losing

The other public C# TCGdex SDK accepts an injected `HttpClient`, which is the
only reason an honest comparison is possible — without it the sole option is
measuring over the live API, which reports TCGdex's servers and the local
connection rather than either library.

Same stub transport, same recorded payload, caching off on both. The result:

| | This SDK | `TCGdex` | Ratio |
|---|---:|---:|---:|
| Fetch + deserialize a card | 29.1 µs / 43.3 KB | 16.8 µs / 12.2 KB | **0.58× / 0.28×** |
| Build a filtered query | 3,100 ns / 4,744 B | 135 ns / 416 B | **0.04× / 0.09×** |

*(Those are the numbers as first measured. Both rows moved later — the fetch row
twice, once from fixing this SDK and once from fixing the harness. Current
figures are in [`comparison.md`](comparison.md); the history is kept here
because the corrections are the point of the section.)*

**A first attempt at explaining it away was wrong, and is worth recording as
such.** The claim was that their `CardModel` exposes 37 properties against this
SDK's 22, so they must deserialize more. Both numbers were miscounted — their
file declares five classes and the 37 summed all of them, while the 22 missed
eight backing-field properties spanning two lines. Counted properly it is about
30 each. **Reaching for the flattering explanation first, and getting it wrong,
is exactly the failure mode a published benchmark is supposed to prevent.**

The real difference is a design choice: their model stores the polymorphic
`damage` and `level` fields as raw `JsonElement`, while this SDK converts them
to usable types. That work has not been avoided on their side, only moved into
every consumer — which is a fair thing to say, and still does not account for
the whole gap.

Two different lessons in those two rows.

The query row is **a real cost that does not matter**. This SDK translates an
expression tree the compiler checks; theirs takes strings it cannot. 3 µs
against 135 ns is the price of `c.Hp > 100` failing to compile when misspelled
rather than failing at runtime — and it is charged once per request against a
20–50 ms round trip, so it is 0.01% of the work. Worth stating plainly in both
directions: real difference, irrelevant consequence.

The fetch row is **a real cost that does matter**, and the honest response is to
fix it rather than explain it. The suspect was code added in the same session:
`BoundedContent` enforced the response-size limit by buffering into a
`MemoryStream`, calling `ToArray()`, converting to a `string`, and only then
deserializing — at least two full copies before parsing started. A safety
feature paid for in allocations, which nobody noticed until something measured
it.

That was fixed, and the row read **25.3 µs / 18.6 KB against 15.3 µs / 12.2 KB**
— a smaller loss on both axes.

**Then the harness turned out to be lying, in this SDK's disfavour.** Its stated
rule was "caching off on both". The other SDK caches *by default* — a fresh
client already carries a `MemoryTCGDexCache` and `CacheTTL = 3600` — and the
benchmark asked for the same card id every iteration, so from the second call
onward it answered from memory while this SDK ran its whole transport. Counting
requests at the handler settled it in one run: **three calls, one request.**

With `CacheTTL = 0` actually set, the row is **24.79 µs / 18.38 KB against
18.57 µs / 25.12 KB**. Time is still a loss. **The allocation column reverses**:
this SDK allocates 27% less, where the page had been reporting 50% more.

Three lessons, and the third is the one worth keeping:

- **A stated fairness rule that nobody executed is worth less than no rule**,
  because it reads to a reader as evidence that a check happened.
- **Check the other library's defaults, not its API surface.** Nothing in the
  constructor signature suggests a cache; it is assigned in the field
  initialiser.
- **A benchmark can be wrong in your favour, and that is harder to catch.**
  Every other correction in this document was found because a number looked too
  good. This one sat unexamined for weeks because the result was unflattering,
  and an unflattering number feels like proof of honesty rather than something
  to verify. It is not.

**A second correction belongs here, because it was published before it was
checked.** The paragraph above originally described this as "43 KB to
deserialize a ~10 KB payload". The card fixture is **2,938 bytes**. The
allocation was 14.7× the payload, not 4×, and the wrong figure made the
situation look considerably better than it was. That is the second time in this
document a flattering number went out unverified; the first was the model
property count two paragraphs up.

The general point: a benchmark that only ever flatters the thing that
commissioned it is marketing. This one was written expecting a mixed result,
produced a clean loss on both axes, and is published with the harness so anyone
can rerun it. That is what makes the numbers elsewhere in these docs worth
anything — and the two corrections above are the cost of keeping it that way.

---
## 99.77% line coverage, 77.91% mutation score

The two numbers measure different things, and only one of them is about whether
the tests work.

| | Question |
|---|---|
| Line coverage | Did this line run? |
| Branch coverage | Were both outcomes of this condition exercised? |
| **Mutation score** | **If this code were wrong, would any test fail?** |

Running Stryker over a suite sitting at 99.77% line coverage returned **77.91%**
— 144 mutants the suite would not have caught. After a sweep through the worst
files it reached **90.03%**, and *line coverage did not move at all*. Every one
of the newly-killed mutants was in code the suite already executed. Coverage
said the lines ran; mutation testing said whether running them proved anything.

It is around **88%** now, and the drop is the more useful half of the story:
that is the first full run since a round of performance work, which changed code
without changing its tests. A mutation score is not a certificate earned once —
it decays exactly when code moves and tests do not.

Two decimal places would be false precision, and finding that out was worth more
than the number. Two consecutive runs on identical code returned 89.21% and
88.05%: a **timeout counts as killed**, and six mutants flipped between
`Timeout` and `Survived` depending on machine load. A busier machine scores
higher.

The distribution was more useful than the total. Models and the query builder
scored 93–95%; the transport, the GraphQL filter and the caching handler — the
most complex and most consequential code — sat at 64–67%. **Verification was
strongest where the code was simplest**, which is the inverse of where it should
be, and completely invisible in a coverage report where all of them read as
fully covered.

---

## The worst-scoring file had tests. They asserted nothing.

`TcgDexClient` came in at 53%, and not one of its problems was a missing test:

- `Create_DisposesItsOwnHttpClient` called `Should.ThrowAsync(...)` and
  **discarded the returned `Task`**. The assertion never ran. That test passed
  no matter what the client did with its `HttpClient`.
- `Create_WithCaching_Works` asserted `client.Cards.ShouldNotBeNull()`, which
  holds whether or not the caller's `configureCache` delegate is ever invoked —
  so deleting that call entirely went unnoticed.
- `Create_AppliesTheConfiguredLanguage` asserted `client.ShouldNotBeNull()`
  while claiming in its name to prove something it structurally cannot observe,
  since `Create` builds its own `HttpClient`.
- The caller-supplied-`HttpClient` disposal test covered one of two constructor
  overloads.

All four ran the code. None verified it. **This is the class of test that
coverage rewards and mutation testing exposes**, and it is why the score moved
53% → 93% almost entirely by fixing existing tests rather than adding new ones.

The generalisation worth keeping: an assertion on a property that is non-null
regardless of the behaviour under test is not an assertion. Neither is an
un-awaited async one.

---

## Most surviving mutants are equivalent — triage before writing tests

Chasing 100% produces tests written for the metric. The recurring unkillable
shapes here, in rough order of frequency:

- **`.ConfigureAwait(false)` flipped to `true`.** No observable difference
  without a synchronization context. The single largest group — ten of the
  caching handler's sixteen remaining survivors.
- **Guards the public API validates first.** `TcgDexClient` checks its arguments
  before the transport or handler sees them, so the inner `Guard.NotNull` calls
  cannot be reached with null through any public path. Note the contrast with
  `MemoryTcgDexResponseCache`, where the same guards *are* reachable because the
  type is public and its interface is an extension point — there they were real
  gaps.
- **Ternary and catch collapses** where the mutated branch throws into a `catch`
  that produces the same result anyway. Forcing `Deserialize("   ")` raises
  `JsonException`, which the very next `catch` turns back into `null`.
- **Non-deterministic tie-breaks**, such as an LRU eviction comparison that
  depends on dictionary ordering.

Two files finished at 70–76% for these reasons and are at their realistic
ceilings. Recording *why* a survivor is equivalent is more useful than the
number, because the next person will otherwise try to kill it again.

---

## Boundary mutants find the off-by-one nobody writes a test for

Two of the most valuable kills were single-character mutations at a boundary:

- `buffered.Length + read > maxBytes` flipped to `>=`, which would reject a
  response of **exactly** `MaxResponseBytes`. A limit is a maximum, not a
  threshold to stay under, and this only ever misfires on the one payload size
  nobody produces by accident.
- `Errors is { Count: > 0 }` flipped to `>= 0`, which treats a present-but-empty
  GraphQL `errors` array as a failure. Only a present-but-empty collection
  distinguishes them — `null` and non-empty behave identically either way.

Both had full line and branch coverage. Neither had a test that landed on the
boundary.

---

## Traps encountered while measuring

- **A file scores differently alone than in a full run.** `TcgDexTransport`
  measured 85% by itself and 76% in the full sweep. Scoring one file runs only
  the mutants in it; a full run includes mutants elsewhere that the same tests
  cover, which shifts the denominator. Compare like with like.
- **Restoring a mutated file with `Copy-Item` keeps the backup's older
  timestamp**, so MSBuild sees the build as up to date and keeps the *mutated*
  DLL. A test then fails only on whichever target framework rebuilt for some
  other reason, which reads exactly like flakiness. Touch the file after
  restoring.
- **`StreamContent` over a `MemoryStream` computes a `Content-Length`**, because
  the stream is seekable. Testing the "unknown length" path needs an
  `HttpContent` whose `TryComputeLength` returns `false` — otherwise the early
  rejection fires and the streaming path is never exercised.
- **`ReasonPhrase = null` does not stick** on a known status code; .NET
  substitutes the standard phrase. Reaching a `?? "no detail supplied"` fallback
  needs a non-standard status — which is not contrived, since HTTP/2 removed
  reason phrases from the protocol entirely.

---
## Line, block and branch coverage answer different questions

| Metric | Question |
|---|---|
| **Line** | Did this line run? |
| **Block** (what Visual Studio reports) | Did this straight-line chunk run? |
| **Branch** (what coverlet reports) | Did we test **both outcomes** of this condition? |

The first two ask "did it execute". Branch asks something categorically
stronger, and the gap between them is where bugs hide:

```csharp
ExpressionType.GreaterThan => flipped ? QueryOperator.LessThan : QueryOperator.GreaterThan,
```

One test hitting `c.Hp > 100` makes this 100% line-covered and 100%
block-covered while `flipped` was only ever `false`. `100 < c.Hp` could have
returned the complement of what the caller asked for with the whole suite green.

Measured here: **99.76% line, 91.90% branch** at the point that was noticed.
Closing the real gaps took branch coverage to 96.06%. Both are now gated, because
gating on line coverage alone cannot see this class of defect.

Worth knowing when comparing against another project: Visual Studio's block
percentage is not comparable to coverlet's branch percentage, and block numbers
typically read higher.

---

## "Unreachable" is a claim that needs proving

Eight lines were written off as "unreachable by construction". Six of them were
not — they were unreachable only through `CardQuery`, whose model happens to
have no boolean property and no custom methods. That is an accident of the
current model, not a property of the generic translator, and those paths are
exactly what a future `SetQuery` would hit.

Driving the internal translator with a synthetic model covered all six.

The remaining two are genuinely unreachable, and that was established by
experiment rather than assertion: `Expression.MakeMemberAccess` rejects any
member that is neither a `FieldInfo` nor a `PropertyInfo` with an
`ArgumentException`, so no tree — compiled or hand-built — can carry one.

**Rule:** if a line cannot be tested, demonstrate why. "It cannot happen" is a
hypothesis until something proves it, and it is wrong more often than it feels.

---

## A gate that cannot fail is not a gate

Every check in this repository was verified in both directions before being
trusted:

- the coverage gate passes at the current value and fails when the threshold is
  raised past it, and fails loudly when no coverage report exists at all rather
  than passing silently on an empty run
- the fixture drift check was fed a simulated field removal and a simulated type
  change, and reported each precisely
- the URL assertions were mutation-checked by reintroducing a defect

This costs minutes and is the difference between a check and a decoration. A
coverage gate that silently passes on a missing report is the most common way
one quietly stops gating.

---

## Shouldly defaults that will bite

Two cost real debugging time here:

- **`ShouldContain` / `ShouldNotContain` on strings are case-insensitive by
  default.** `"itemsPerPage:25".ShouldNotContain("page:")` fails. Pass
  `Case.Sensitive`, or anchor the match — `"{page:"`.
- **`ShouldHaveSingleItem` takes no predicate.** `xs.ShouldHaveSingleItem(x => …)`
  does not compile; write `xs.Where(…).ShouldHaveSingleItem()`.

Also `ShouldBeAssignableTo<T>()` on a `Type` instance tests the `RuntimeType`
object, not the type it represents. For that, use
`typeof(IDisposable).IsAssignableFrom(typeof(Foo))`.

---

## Log with the source generator, not string interpolation

`[LoggerMessage]` emits a cached delegate and an `IsEnabled` check per message,
so a disabled level costs a branch — no formatting, no boxing, no allocation.

`logger.LogDebug($"…{uri}")` formats the string and boxes its arguments *before*
the level is checked, so it costs something on every call whether or not anyone
is listening. That is how a library ends up measurably slower with logging
"off". A test asserts nothing is formatted when the level is disabled.

The generated delegates are also AOT-safe, being built at compile time rather
than by reflection.

### Do not duplicate what `IHttpClientFactory` already logs

Registered through `AddTcgDex`, it already logs every request and its timing
under `System.Net.Http.HttpClient`. Logging raw requests again would double the
noise and disagree on detail. What an SDK should log is what it *decided* —
cache outcomes, error classification, dropped entries — not what the transport
already reports.

---

## GraphQL's win is the flat card search, not nested fetch

The obvious assumption — that GraphQL avoids N+1 by fetching a set together with
its cards — is **wrong here**. Probing `set(id:"swsh3"){ cards { hp types
attacks } }` returns all 201 cards with `hp`, `types` and `attacks` **null**,
even for cards that plainly have them. The nested resolver is shallow: it
populates only `id`, `name` and the other non-nullable fields.

The real win is the flat query. `cards(filters:{…})` *does* return full detail
per card:

| Goal | REST | GraphQL |
|---|---|---|
| 12 cards, full detail | 1 list call + 12 detail calls = **13** | **1** |

That is what `SearchDetailedAsync` uses. Everything else in GraphQL is a
downgrade — see [`docs/api-info.md`](api-info.md) §7 for the language, filter
and pricing limits.

### `Card.rarity` is non-nullable in the schema but null in the data

Selecting `rarity` inside `set { cards { … } }` fails with
`Cannot return null for non-nullable field Card.rarity`, and the server nulls
the entire card entry rather than just that field. On the flat
`cards(filters:)` query it is safe, which is why the SDK selects it there.

Because any entry can be nulled this way, the transport drops null entries
rather than handing back a list the caller must null-check element by element.

### GraphQL reports failure with HTTP 200

A failed query still returns 200 with an `errors` array. Status codes are not a
usable success signal on this endpoint; the SDK checks `errors` first.

### Assert on the decoded query, not the wire bytes

`System.Text.Json` escapes `"` as `"` by default, so asserting on the raw
request body tests the serializer's escaping rather than the query being built.
The tests parse the body and assert against the GraphQL document itself. The
`"` form is valid JSON and the live endpoint accepts it, which the
integration tests confirm.

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
The URL assertions were verified by deliberately breaking the transport —
wrapping the query string in a `?q=` parameter the API does not have — and
confirming that `GetAsync_PreservesQueryStringVerbatim` failed, and that it was
the only failure.

That class of bug is invisible to a mock handler that ignores its `request`
argument, because no test ever observes a URL.
`Http/RecordingHandler.cs` is built to make it impossible: it records every
request, exposes the URI for assertion, and fails loudly with the offending
method and URL when the code makes an unexpected extra call.

Worth repeating for any new area: if a test has never been red, confirm it can
be.

---

## Advice about someone else's service is the fastest-rotting kind of doc

`docs/publishing.md` was written from general knowledge of how nuget.org works
and was wrong in two places within a day of being read:

- It sent the reader to a **Manage Packages → ID Prefix Reservation** page. No
  such page exists — the user checked `nuget.org/account/Packages` and found
  nothing. Reservation is an email to `account@nuget.org` reviewed by a person.
  The step was plausible, which is exactly why it survived writing.
- It recommended a **365-day API key** as "the maximum". nuget.org caps new keys
  at 30 days from 2026-08-17 and expires every earlier key on 2026-11-01, having
  made Trusted Publishing — a GitHub OIDC exchange for a one-hour key — the
  recommended path.

Nothing in the repo could have caught either. The build does not compile prose,
and no test asserts against a vendor's UI. The distinction worth carrying: facts
about *this codebase* are verifiable here and stay true until someone changes
them, while facts about *another organisation's product* have an expiry date
nobody tells you about.

So when a doc describes a third party's UI or policy, cite the vendor page it
came from — a stale claim next to its source can be re-checked in one fetch,
while an uncited one has to be re-derived from scratch. And when a doc's whole
purpose is a procedure the user has not performed yet, re-verify it at the
moment they start, not at the moment it was written.

A related trap sits in the same file: the ID prefix criteria ask whether the
prefix identifies the *reservation owner*. `TcgDex.` identifies TCGdex, the
upstream API — so reserving it would likely be rejected, and would block the
actual maintainers from publishing under their own name. Wrapping someone's API
does not give you their namespace.

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
