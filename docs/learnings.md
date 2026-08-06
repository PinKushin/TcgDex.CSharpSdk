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

## Test fixtures are recorded live responses, not hand-written JSON

Every file in `TcgDex.CSharpSdk.Tests/Fixtures/` was captured from the live API
and is loaded through the SDK's own `TcgDexJsonContext`, so the tests exercise
the exact serializer configuration that ships.

Hand-written fixtures would have hidden every bug in this document: the
polymorphic `damage` field, the object-array `boosters`, the missing `image` on
`exu-!`, and the initializer problem above were all found because the recorded
payloads are irregular in ways invented test data never is.

See `docs/api-info.md` §9 for what each fixture card is for.
