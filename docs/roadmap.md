# Roadmap

Current state as of `f45e496` (2026-08-05): the SDK covers the full TCGdex REST
surface, builds clean on `net8.0` and `net10.0`, and is verified under Native
AOT. **113 unit tests, 22 integration tests, zero warnings.**

Not published, deliberately.

---

## Before 1.0

### 1. Coverage to ~100% — *the current gate*

83.2% of hand-written code, 93 uncovered lines across 10 files. Full gap
analysis and the order to tackle it in: [`coverage.md`](coverage.md).

The headline items are the error and cancellation paths on both transports, and
the `NotSupportedException` messages in `ExpressionTranslator` — the paths users
hit when something goes wrong are currently the least tested.

### 2. More integration tests

22 is thin for an SDK whose whole job is matching a third-party API. Worth
adding:

- One card per category across several eras, not just the current fixtures.
- Every `Catalog` endpoint (only three are covered live today).
- Both damage forms, resistances, abilities and boosters against live data.
- Error paths: a real 404, and a genuinely unsupported language.
- Every language code at least smoke-tested — 18 exist, 2 are exercised.
- Pagination boundaries: last page, page beyond the end, `itemsPerPage=1`.

These are cheap to write and they are the tests that catch the API changing
underneath the SDK, which is the failure mode fixtures cannot detect.

### 3. Decide the serialization story

Both JSON converters have uncovered `Write` paths because nothing in the SDK
serializes a Card. Either cover them with round-trip tests or delete them.
Untested code no caller reaches is worse than absent code.

### 4. Enforce coverage in CI

Once the gap is closed, add a threshold to the unit-test step so it cannot
silently regress. Details in [`coverage.md`](coverage.md).

### 5. Publish

Full first-timer walkthrough in [`publishing.md`](publishing.md). Ship `0.x`
until the API shape has settled; `1.0.0` is a promise not to break it.

### 6. Submit to tcgdex.dev/sdks

No C#/.NET SDK is listed today — Java, JavaScript, Kotlin, PHP, TypeScript and
Python only. That gap is the reason this project exists. Submit via a pull
request to [tcgdex/documentation](https://github.com/tcgdex/documentation) once
published.

---

## Possible later

Not committed to, and none of it should precede the work above.

- **Response caching.** The API sends `Cache-Control: no-store` with a weak
  `ETag`, so any caching is a client-side policy decision rather than something
  to follow from headers. Would meaningfully cut round trips for catalog data,
  which changes rarely.
- **Resilience.** `Microsoft.Extensions.Http.Resilience` would add retry and
  circuit-breaking without hand-rolled `Polly` wiring. Deliberately left out for
  now: `AddTcgDex` returns `IHttpClientBuilder`, so callers can already attach
  their own policies, and baking in a retry policy nobody asked for is a way to
  hammer a free public API.
- **Auto-pagination.** An `IAsyncEnumerable<CardBrief>` that pages until
  exhausted. Straightforward, but note the API exposes no total count, so it can
  only detect the end by receiving a short page.
- **`SearchDetailedAsync` for sets and series.** Only cards have it today.
  Worth it only if the same N+1 shape shows up in practice.
- **Image helpers.** A typed `GetImageUrl(quality, format)` rather than string
  concatenation.

## Explicitly not doing

- **`IQueryable<Card>`.** The API has ten operators; an `IQueryable` would throw
  for most of LINQ. See [`architecture.md`](architecture.md).
- **GraphQL as the primary transport.** It cannot serve 17 of the 18 languages,
  any range filter, or any pricing.
- **Write operations.** The API is read-only — `GET` only, no auth.
