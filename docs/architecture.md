# Architecture

How the SDK is put together, and why each piece is shaped the way it is.

The API reference it is built against is [`api-info.md`](api-info.md); the
non-obvious behaviour discovered along the way is in
[`learnings.md`](learnings.md).

---

## Layers

```
ITcgDexClient                     public entry point
  ├── Cards / Sets / Series       resource clients, one per endpoint group
  ├── Random / Catalog
  │
  ├── TcgDexTransport             REST: URL building, error contract      (internal)
  └── GraphQlTransport            GraphQL: opt-in, one path only          (internal)
        │
        └── TcgDexJsonContext     source-generated serialization
            GraphQlJsonContext
                │
                └── Models        records with required members
```

Both transports are `internal`, exposed to the test project via
`InternalsVisibleTo`. Tests drive them directly because asserting a URL through
a resource client would test two things at once.

## Projects

| Project | Purpose |
|---|---|
| `TcgDex.CSharpSdk` | The SDK. Multi-targets `net8.0` and `net10.0`. |
| `TcgDex.CSharpSdk.Tests` | Unit tests. Offline, against recorded fixtures. |
| `TcgDex.CSharpSdk.IntegrationTests` | Live API. `[Category("Integration")]`, weekly in CI. |
| `TcgDex.CSharpSdk.AotSmokeTest` | Publishes with Native AOT and runs, proving compatibility. |

Shared build settings live in `Directory.Build.props`; package versions in
`Directory.Packages.props` (central package management).

---

## Decisions worth understanding

### One error contract

A missing resource returns `null`; everything else throws
`TcgDexApiException`. `JsonException`, `HttpRequestException` and client-side
timeouts are all folded into that one type, so callers catch one thing rather
than four.

This exists because the previous SDK split it arbitrarily — single-item getters
swallowed failures and returned null while list methods propagated raw
`HttpRequestException`, so identical failures surfaced differently depending on
which method you happened to call.

The subtlety: the API returns **404 for an unsupported language too**, so the
status code alone cannot distinguish that from a missing card. The transport
discriminates on the problem document's `type`, and a language error throws
rather than masquerading as an empty result.

### Models are records with `required` members

Nothing is constructible in a null state, and null is opt-in — a property is
nullable only where the API genuinely omits the field.

**Every collection needs a null-coalescing backing field**, not an initializer.
`System.Text.Json`'s source generator discards property initializers, so `= []`
silently deserializes to `null`. This is the single most important rule when
adding a model; see [`learnings.md`](learnings.md).

### Two serializer contexts

`TcgDexJsonContext` is public and covers the API models. `GraphQlJsonContext` is
**internal** and covers the GraphQL envelopes, because the generator emits a
public property per registered type — registering wire types in the public
context would make the wire format public API.

### The query builder is not `IQueryable`

The API supports exactly ten operators. An `IQueryable<Card>` would have to
throw for most of LINQ, which is a partial interface implementation failing at
runtime rather than at the call site. `CardQuery` makes the supported surface
explicit and rejects anything else with a message naming the expression.

**It never calls `Expression.Compile()`** — that emits IL at runtime and is not
AOT-safe. Trees are walked structurally, and captured variables are read from
their closure reflectively. This constraint is load-bearing: the AOT smoke test
fails if it is ever violated.

### GraphQL is one method, not a transport

`Cards.SearchDetailedAsync` exists for a single reason: REST's list endpoint
returns briefs, so full detail for a 12-card result costs 13 round trips versus
1 over GraphQL.

It is not used anywhere else, because GraphQL is worse in every other respect —
no language support, equality-only filters, no pricing. Note also that nested
fetch does *not* work: `set{cards{hp}}` returns nulls, because that resolver is
shallow. Only the flat `cards(filters:)` query returns detail.

### DI goes through `IHttpClientFactory`

`AddTcgDex` registers a typed client, so handler lifetime and connection pooling
are managed properly. Options are validated at registration, so a typo'd
language fails at startup rather than as a confusing 404 later.

`TcgDexClient` has exactly **one** constructor — a second overload also taking
`HttpClient` makes the typed-client activator ambiguous, which the DI test
caught.

---

## Adding to the SDK

**A new endpoint:** add the method to the resource interface in
`Resources/ITcgDexResources.cs`, implement it in `Resources/Resources.cs`, and
add a test asserting the exact request URI. The URI assertion is the point — it
is what the previous SDK lacked.

**A new model:** add the record, register it in `TcgDexJsonContext`, use a
null-coalescing backing field for every collection, and add a contract test
against a **recorded live response** rather than hand-written JSON. Fixtures live
in `TcgDex.CSharpSdk.Tests/Fixtures`.

**A new filter operator:** it must exist in the API. Add it to `QueryOperator`,
render it in `QueryFilter.Render`, translate it in `ExpressionTranslator`, and
assert the exact query string. Then check it against the live API — unit tests
only prove the builder is self-consistent.

## Testing conventions

- Unit tests are offline and deserialize recorded responses through the SDK's own
  serializer context, so they exercise the shipping configuration.
- `RecordingHandler` records every request and its body; assert on URIs.
- Integration tests carry `[Category("Integration")]` and hit the live API.
- Names follow `{Method}_{Scenario}_{Expected}`. `CA1707` is suppressed in test
  projects because the underscores are the convention.
- Shouldly for assertions; no mocking framework.
- **If a test has never been red, confirm it can be.** The URL assertions were
  mutation-checked by reintroducing the old `?q=` bug.
