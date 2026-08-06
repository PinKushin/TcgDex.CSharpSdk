# Roadmap

State at `c9a4e67`: the SDK covers the full TCGdex REST surface, builds clean on
`net8.0` and `net10.0`, and is verified under Native AOT.

| | |
|---|---|
| Tests | **339 unit + 129 integration** |
| Line coverage | **99.76%**, gated at 99.5 |
| Branch coverage | **96.06%**, gated at 95 |
| Warnings | zero — compiler, analyzers, DocFX, CI annotations |
| Docs | published to GitHub Pages on every push to `main` |
| Package | builds, ~182 KB, both target frameworks |

Not published to NuGet, deliberately.

---

## Before 1.0

### 1. Publish to NuGet — *the only remaining gate*

Everything else on this list is done. Full first-timer walkthrough in
[`publishing.md`](publishing.md), including the two irreversible facts worth
reading before the first push: a published version can never be deleted, and the
package ID is claimed permanently.

Ship `0.x` while the API shape can still move. `1.0.0` is a promise not to break
it.

### 2. Submit to tcgdex.dev/sdks

TCGdex lists official SDKs for Java, JavaScript, Kotlin, PHP, TypeScript and
Python. There is no C#/.NET one, which is the gap this fills. Submit via a pull
request to [tcgdex/documentation](https://github.com/tcgdex/documentation) once
published.

---

## Done

Recorded because the reasoning behind each is worth keeping, and because a
roadmap that only lists future work hides what the project already decided.

- **Full REST surface** — cards, sets, series, random, and all 13 enumeration
  endpoints.
- **Typed query builder** over every operator the API actually has, translating
  expression trees without ever calling `Expression.Compile()`.
- **Opt-in GraphQL search** for full card detail in one request instead of one
  call per card.
- **Response caching** with ETag revalidation, so a stale entry costs a `304`
  and zero bytes rather than a re-download. See [`caching.md`](caching.md).
- **Auto-pagination** via `StreamAsync`, which handles the short-page end signal
  the missing total count forces on every consumer.
- **Typed image URLs**, including the asymmetry that card artwork takes a quality
  segment and set assets do not.
- **`HttpClient` ownership** made explicit, with `TcgDexClient.Create()` for
  callers outside a container.
- **Logging and tracing** through `ILogger` and `ActivitySource`, with no
  dependency on any telemetry vendor. See [`observability.md`](observability.md).
- **Coverage gated in CI** on both line and branch, verified to fail as well as
  pass.
- **Documentation site** generated from XML docs, so the reference cannot drift
  from the code.

---

## Possible later

Not committed to. Each would need a reason beyond "it would be neat".

- **`SearchDetailedAsync` for sets and series.** Only cards have it. Worth it
  only if the same N+1 shape shows up in practice for the others.
- **A distributed cache implementation.** `ITcgDexResponseCache` is already
  pluggable and documented; shipping a Redis one would mean taking that
  dependency for everyone.
- **Response compression tuning.** `Create()` enables automatic decompression;
  whether it is worth exposing knobs depends on evidence it matters.
- **Metrics.** `System.Diagnostics.Metrics` counters for cache hit rate and
  request duration would complement the existing tracing. The tracing already
  carries duration, so this only pays off for aggregate dashboards.

## Explicitly not doing

- **`IQueryable<Card>`.** The API has ten operators; an `IQueryable` would have
  to throw for most of LINQ — a partial implementation failing at runtime rather
  than at the call site. See [`architecture.md`](architecture.md).
- **GraphQL as the primary transport.** It cannot serve 17 of the 18 languages,
  any range filter, or any pricing data.
- **Built-in retry or circuit breaking.** `AddTcgDex` returns
  `IHttpClientBuilder`, so `.AddStandardResilienceHandler()` already works.
  Shipping a retry policy nobody asked for is how an SDK ends up hammering a
  free public API.
- **A telemetry vendor dependency.** The SDK writes to `ILogger` and
  `ActivitySource`; choosing the backend belongs to the application. A library
  that reported to its author's account from a consumer's process would be
  exfiltrating their data.
- **Write operations.** The API is read-only — `GET` only, no authentication.
