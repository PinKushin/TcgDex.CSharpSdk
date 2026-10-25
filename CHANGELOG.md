# Changelog

All notable changes to `TcgDex.CSharpSdk` are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Entries are written as the work lands rather than assembled at release time, so
`Unreleased` is the working set and nothing has to be reconstructed from git log
afterwards.

Changes are recorded from a **consumer's** point of view. Refactors, style
sweeps, test additions and CI work do not appear here unless they change
something an application can observe — those live in the commit history.

## [Unreleased]

### Changed

- **Breaking, source level:** paging moved off the method signatures, ahead of 1.0. Public interfaces cannot gain
  parameters after 1.0 without a breaking release, so growth now goes onto the types they take.
  - `SearchDetailedAsync(filter, page, itemsPerPage, cancellationToken)` is `SearchDetailedAsync(filter, cancellationToken)`.
    Set `Page` and `ItemsPerPage` on the `CardFilter` instead: `new CardFilter { Name = "Furret", Page = 2, ItemsPerPage = 25 }`.
  - `StreamAsync(query, pageSize, cancellationToken)` is `StreamAsync(query, cancellationToken)`. The page size is the
    `itemsPerPage` of `query.Page(1, n)`, and is 100 when the query sets none. The page number is still ignored.
  - The wire requests are unchanged.

### Added

- `Catalog.GetValuesAsync(name)` and `Catalog.GetNumberValuesAsync(name)` read an enumeration endpoint by name, so one
  the SDK has no named method for yet is reachable without waiting for a release. The name is escaped as a single path
  segment.

## [0.8.1] - 2026-10-18

### Fixed

- A string indexer is now accepted as a filter value, like every other indexer. `Where(c => c.Name == text[0].ToString())`
  threw `NotSupportedException`, while `names[0]` on a list or array worked; the string indexer is named `Chars` rather
  than `Item`, and the translator only recognised `Item`. Nothing that worked before changes.

## [0.8.0] - 2026-10-18

The 1.0 naming pass: every public name now follows the .NET Framework Design Guidelines. Almost
every change is **breaking** at compile time and none changes what goes over the wire — JSON keys,
request paths and query strings are the same as in 0.7.0. Most upgrades are a find-and-replace from
the table below.

### Migration

| 0.7.0 | 0.8.0 |
|---|---|
| `Serie` | `Series` |
| `SerieBrief` | `SeriesBrief` |
| `ISerieResource` | `ISeriesResource` |
| `ISetResource` | `ICardSetResource` |
| `Set` (the model) | `CardSet` |
| `SetBrief` | `CardSetBrief` |
| `SetAbbreviation` | `CardSetAbbreviation` |
| `Set.Serie` | `CardSet.Series` |
| `Card.Hp` | `Card.HitPoints` |
| `CardFilter.Hp` | `CardFilter.HitPoints` |
| `Card.DexId` | `Card.DexIds` (`CardFilter.DexId` stays singular: it filters by one id) |
| `Card.Retreat` | `Card.RetreatCost` |
| `CardFilter.Retreat` | `CardFilter.RetreatCost` |
| `Card.EvolveFrom` | `Card.EvolvesFrom` |
| `CardFilter.EvolveFrom` | `CardFilter.EvolvesFrom` |
| `Card.VariantsDetailed` | `Card.DetailedVariants` |
| `Card.Legal` / `CardSet.Legal` | `Card.Legality` / `CardSet.Legality` |
| `DetailedVariant.Stamp` | `DetailedVariant.Stamps` |
| `CardCount.FirstEd` | `CardCount.FirstEdition` |
| `CardmarketPricing.IdProduct` | `CardmarketPricing.ProductId` |
| `CardmarketPricing.Avg` | `CardmarketPricing.Average` |
| `CardmarketPricing.Avg1` / `Avg7` / `Avg30` | `Average1Day` / `Average7Days` / `Average30Days` |
| `CardmarketPricing.AvgHolo` | `CardmarketPricing.AverageHolo` |
| `CardmarketPricing.Avg1Holo` / `Avg7Holo` / `Avg30Holo` | `Average1DayHolo` / `Average7DaysHolo` / `Average30DaysHolo` |
| `Pricing.Tcgplayer` | `Pricing.TcgPlayer` |
| `ThirdParty.Tcgplayer` | `ThirdParty.TcgPlayer` |
| `ThirdParty.Cardtrader` | `ThirdParty.CardTrader` |
| `TcgDexProblem.Lang` | `TcgDexProblem.Language` |
| `TcgDexOptions.GraphQlEndpoint` | `TcgDexOptions.GraphQLEndpoint` |
| `TcgDexErrorKind.GraphQlError` | `TcgDexErrorKind.GraphQLError` (same value, 7; its `ToString()` changes) |
| `Random.CardAsync` / `SetAsync` / `SerieAsync` | `Random.GetCardAsync` / `GetSetAsync` / `GetSeriesAsync` |
| `Catalog.CategoriesAsync` (and the other twelve) | `Catalog.GetCategoriesAsync` (each gains `Get`) |
| `Catalog.HitPointsAsync` | `Catalog.GetHitPointsAsync` |
| `TcgDexCachingHandler.FreshHits` / `Revalidations` / `Misses` | `TcgDexCachingHandler.Statistics.FreshHits` / `.Revalidations` / `.Misses` |
| `GetImageUrl` / `GetLogoUrl` / `GetSymbolUrl` returning `string?` | the same methods, returning `Uri?` |
| `ImageUrl.Build` / `BuildAsset` returning `string?` | the same methods, returning `Uri?` |
| `new TcgDexApiException(message, statusCode, problem, inner)` | `new TcgDexApiException(message, kind, requestUri, statusCode, problem, inner)` |
| `QueryOperator` | removed from the public API (internal) |

### Changed

- **Breaking:** the renames in the table above.
- **Breaking:** the image URL helpers return `Uri?`. A base value that does not form an absolute
  http(s) URL now gives `null` instead of a malformed string.
- **Breaking:** `CachedResponse` is a class with reference equality rather than a record. It no
  longer supports `with` or value `==`; create a new instance with an object initializer.
- **Breaking:** `QueryOperator` is internal. No public member used it.
- **Breaking:** the `TcgDexApiException` constructor taking only a status code is removed. It
  produced `Kind = Unspecified`; use the constructor that takes a `TcgDexErrorKind`.
- `AddTcgDexWithCaching` registers `TcgDexCacheOptions` through the options pattern, so
  `Configure<TcgDexCacheOptions>`, an `appsettings.json` binding and `IOptions<TcgDexCacheOptions>`
  now work alongside the `configureCache` delegate. Previously only the delegate was applied.
  The merged options are validated at start-up, like `TcgDexOptions`: a `MaxEntries` below 1 or a
  negative time-to-live fails with `OptionsValidationException`.
- An unreadable `updated` timestamp on a card or a pricing block now reads as `null`, as
  `ReleaseDate` already did, instead of failing the whole card.

## [0.7.0] - 2026-10-17

### Added

- `TcgDexApiException.Kind` (a new `TcgDexErrorKind`: `HttpStatus`, `Network`, `Timeout`,
  `Canceled`, `ResponseTooLarge`, `InvalidResponse`, `GraphQlError`) and
  `TcgDexApiException.RequestUri`, set on every exception the SDK throws, so a caller can branch on
  the failure without reading the message.
- `TcgDexCacheStatistics`, registered by `AddTcgDexWithCaching`: inject it to read the cache's
  fresh hits, revalidations and misses.
- The cache log events 1100–1104 (hit, revalidated, miss, coalesced, evicted) are now emitted. They
  were documented but never written.

### Changed

- **Breaking:** `Set.ReleaseDate` and `Serie.ReleaseDate` are now `DateTime?` instead of `string?`,
  on every target framework. The value is a date of kind `Unspecified` with no time component.
  An absent or malformed date reads as `null` rather than failing the set or series.
- **Breaking:** `TcgDexJsonContext` is no longer public. Deserialize models through the client, or
  with reflection-based `JsonSerializer` if you serialize them yourself (not Native AOT-safe). Your
  own source-generated `JsonSerializerContext` cannot be used: the models name internal converters.
- **Breaking:** invalid `TcgDexOptions` passed to `AddTcgDex` or `AddTcgDexWithCaching` now fail
  with `OptionsValidationException` when the host starts (`ValidateOnStart`), or on first resolution
  without a host, instead of `ArgumentException` at registration. Validation now covers the final
  options from every source, such as `appsettings.json` or another `Configure<TcgDexOptions>` call,
  and your configure delegate no longer runs an extra time at registration.
- `Where` now accepts array elements, indexers and parameterless instance methods on the value
  side, such as `c.Id == ids[i]` or `c.Name == name.Trim()`, and applies casts with C# semantics.
  It still never compiles the expression, so Native AOT is unaffected.
- `DeserializePricing = false` now also skips the pricing on every `VariantsDetailed` entry, not
  only `Card.Pricing`.
- Options are read once, when the client is created. GraphQL searches used to pick up later changes
  to `GraphQlEndpoint`, `Timeout` and `MaxResponseBytes`; they no longer do, matching REST.
- Calling `AddTcgDex` or `AddTcgDexWithCaching` a second time on one service collection now throws
  `InvalidOperationException`. The second call's options used to be silently ignored.

### Fixed

- The in-memory caches could throw `ArgumentException` ("IComparer.Compare() method returns
  inconsistent results") from a cache write when entries were evicted while other threads read
  them.
- `IOptions<TcgDexOptions>`, `IOptionsSnapshot` and `IOptionsMonitor` now see every configured
  property. Only `BaseAddress`, `Language` and `GraphQlEndpoint` used to reach them.
- Under `AddTcgDex`, the parsed-model cache (`MaxDeserializedCacheEntries`) is now shared by every
  resolved client. Each resolution used to get an empty cache, so it never hit.
- Under `AddTcgDexWithCaching`, the cache counters and request coalescing now survive
  `IHttpClientFactory` rebuilding its handlers (every two minutes by default). Both used to reset.
- `TcgDexOptions.Validate` now rejects a relative `GraphQlEndpoint` with `ArgumentException`.
- The `ActivitySource` now reports the SDK's version instead of `0.1.0`.
- In `Where`, a user-defined conversion operator on the value side is now applied; its operand's
  `ToString()` used to be sent. A throwing property getter now surfaces its own exception rather
  than `TargetInvocationException`, and a cast that cannot succeed (`(byte)300`,
  `(int)double.NaN`) throws `NotSupportedException` naming the expression.
- `MemoryTcgDexResponseCache` no longer throws, or stores an entry already expired, for a very large
  time-to-live.
- A card whose TCGplayer pricing carries a key with a non-object value (for example
  `"currency": "USD"`) no longer throws `JsonException`; the key is ignored.
- `Contains`, `StartsWith` and `EndsWith` send the value as written. Before, `Contains("%2A")` was
  decoded to `*` and matched every card.
- `SearchDetailedAsync` throws `ArgumentOutOfRangeException` for a `page` or `itemsPerPage` below
  one, as the REST query does, instead of sending it.
- A GraphQL search result whose `set` is null is dropped and logged, like a null entry, instead of
  being returned with a null `Set`.
- `TcgDexLanguages.All` can no longer be modified by casting it to a mutable list.

### Documentation

- `Where` documents that a `*` at the start or end of a value, and any `|`, are interpreted by the
  server as a wildcard and an OR, and cannot be matched literally.

## [0.6.3] - 2026-10-15

### Fixed

- A connection that drops partway through a response body now throws `TcgDexApiException` instead
  of a raw `IOException`. Successful responses, GraphQL and `TcgDexCachingHandler` carry the
  original failure as `InnerException`. Error responses report their status code instead.
- A `404` whose body cannot be read (reset or larger than `MaxResponseBytes`) now throws
  `TcgDexApiException` with `StatusCode` `404` instead of returning `null`, with the read failure as
  `InnerException`. Only the body tells a missing resource apart from an unsupported language.
- A GraphQL error response now reports its status code even when its body resets or is too large.
- Cancelling a request now always surfaces as `OperationCanceledException`, including on a
  platform that reports the cancelled body read as an `IOException`.
- On REST requests, a body that fails to read, or is not valid JSON, is now logged and marks the
  request's `Activity` as failed, like other failures.
- `TcgDexCachingHandler` keeps an `If-None-Match` the caller set, adding its own `ETag` beside it,
  instead of replacing it. A `304 Not Modified` is served from the cache when the caller sent no
  validator of their own, or when the 304 names the cached `ETag` (weak or strong form); otherwise
  it is returned to the caller untouched, rather than serving the stale cached body as current. A
  request message sent again after the cached `ETag` changed carries only the current one.
- `TcgDexCachingHandler` no longer adds its `ETag` beside a caller's `If-None-Match: *`, which
  produced an invalid header; a `304` answering `*` is returned to the caller untouched.
- With `CoalesceConcurrentRequests`, a request carrying its own `If-None-Match` is no longer
  merged with a concurrent request for the same URL, so it gets the answer to its own validator
  rather than the other request's.
- A request cancelled by the HTTP pipeline, such as a shorter `HttpClient.Timeout`, now throws
  `TcgDexApiException` saying so, instead of reporting that `TcgDexOptions.Timeout` expired. The
  "timed out" message is reserved for the SDK's own timeout.
- When the caller cancels a request, the `OperationCanceledException` now carries the caller's own
  token, so `catch (OperationCanceledException ex) when (ex.CancellationToken == token)` matches.
- An error response (such as a 500) whose body stalls past `TcgDexOptions.Timeout` now throws
  `TcgDexApiException` (timed out) instead of a raw cancellation exception.
- `TcgDexCachingHandler` now stores the new `ETag` when a `304 Not Modified` carries one, so the
  next revalidation uses the server's current validator.
- `TcgDexCachingHandler` no longer sends `If-None-Match` twice when a handler above it re-sends the
  same request.
- `TcgDexCachingHandler` disposes an error response if a custom cache's `RemoveAsync` throws.
- A response declaring a large `Content-Length` with a small body no longer pre-allocates the
  declared size. The initial buffer is capped at 1 MiB and grows as needed.

### Changed

- Documented: on .NET Framework, `TcgDexOptions.Timeout` may not end a response body that stalls
  after its headers arrive, because that platform's `HttpClientHandler` does not cancel an
  in-progress read. This is known platform behaviour, not measured by this SDK's tests.
- `TcgDexCachingHandler` now throws `TcgDexApiException` for a body whose connection fails mid-read,
  where it previously threw a raw `IOException`. An oversized body already threw
  `TcgDexApiException`. That choice is deliberate, so retry policies do not re-download it.

## [0.6.2] - 2026-10-15

### Changed

- **net10.0:** `Microsoft.Extensions.Http`, `Microsoft.Extensions.Logging.Abstractions` and
  `Microsoft.Extensions.Options` now require 10.0.13, up from 10.0.10. Each target tracks the
  latest patch of its own line, for the security, bug and performance fixes. The net8.0 and
  netstandard2.0 builds are unchanged and still depend on the 8.0.x line.

## [0.6.1] - 2026-09-12

### Added

- **`User-Agent` header on every outbound request**, `TcgDex.CSharpSdk/{version}` — requested by
  TCGdex directly so their logs (which store no IP address, for GDPR) can identify SDK traffic.
  Applied per-request; does not touch a caller-supplied `HttpClient`'s own headers.

## [0.6.0] - 2026-09-11

### Added

- **`ThirdParty` external marketplace ids.** TCGdex started serving Cardmarket, TCGplayer and
  Cardtrader product ids on 2026-09. New `Card.ThirdParty` and `DetailedVariant.ThirdParty`
  properties, each independently nullable and placed the same way `Pricing` already is — a card
  with several distinctly-priced printings carries it per printing; a card with one
  undifferentiated printing carries it at the card root instead. `Cardtrader` in particular is
  present only intermittently on the live API, so do not treat its absence on one fetch as
  permanent.

## [0.5.0] - 2026-09-06

**Breaking.** Client-side failover and mirror selection are gone. If you called
`UseFailover` or `UseMirror`, delete the call — the default configuration is now
the recommended one. To target a different server, set `BaseAddress` and
`GraphQlEndpoint`.

### Removed

- **`UseFailover()` and its two overloads, `FailoverEndpoints`,
  `FailoverAttemptTimeout`, `FailoverCooldown`.** TCGdex deployed new
  infrastructure in September 2026, and its main route now routes around a node
  that is down. Rotating between official nodes client-side was always meant to
  be removed once that existed — it was built on the understanding that it would
  be — and keeping it would mean a second, slower retry layer duplicating one the
  service already does better.

- **`UseMirror()` and the `TcgDexMirror` enum.** The same deployment retired the
  per-node hostnames. `api.na1.tcgdex.net` and `api.na2.tcgdex.net` no longer
  route at all, so two of the six members could only ever fail; upstream now
  recommends `api.tcgdex.net` for everyone. Left in place they would be an
  invitation to use hosts that no longer work.

- This also removes the `404` discrimination added after 0.4.0 but never
  released, which stopped an unrouted host's `404` being reported as a missing
  card. That defect is resolved by the feature it lived in no longer existing.
  The reasoning is kept in [`docs/learnings.md`](docs/learnings.md), because it
  is about status codes rather than about failover.

### Kept

- **Pointing the SDK at a server of your own.** `BaseAddress` and
  `GraphQlEndpoint` are unchanged, and remain the supported way to use an
  unofficial mirror, a self-hosted instance, or a local test server. Set both —
  GraphQL sits outside the language segment, so changing only `BaseAddress`
  leaves GraphQL queries going to the official host.

### Added

- **`BaseAddress` is now validated to end with `/`.** Paths resolve relative to
  it, so `https://mine/v2` + `en/cards/x` gave `https://mine/en/cards/x` — the
  API root silently dropped, every lookup returning "not found", and nothing
  pointing at the cause. The check previously existed only for failover
  endpoints; removing those left the identical hazard on the one address that
  still matters.

## [0.4.0] - 2026-09-01

### Added

- **Fall back to another server when one is unreachable.**
  `TcgDexOptions.UseFailover()` retries a request against the next endpoint when
  the current one refuses the connection, returns `502`/`503`/`504`, or hangs
  past `FailoverAttemptTimeout`. Off by default, and when enabled it sends **no
  extra requests while the service is healthy** — it acts only on a failure.

  Takes official nodes (`UseFailover()`, or named ones) or arbitrary API roots,
  so an **unofficial mirror or a server of your own** can be a fallback:
  `UseFailover(new Uri("https://tcgdex.example.dev/v2/"))`.

  Deliberately narrow about what counts as a failure. A `404` never rotates — a
  missing card is a normal result, and rotating would send every absent card to
  every configured node — and neither does `429`, because spreading a rate limit
  across endpoints is evasion rather than resilience. At most three endpoints are
  tried per request.

  **`GET`, and `POST` to the GraphQL endpoint — nothing else.** Rather than
  assume any request with a body is safe to repeat, the SDK replays exactly the
  set it authored: TCGdex's GraphQL schema has queries and no mutations, and the
  body was built by the SDK's own transport. The body and its content headers
  travel with the retry. A `POST` anywhere else, or another verb aimed at the
  GraphQL endpoint, is passed through untouched — narrower than today's API
  needs, so that a mutation endpoint appearing later is not replayed by
  accident.

  Two knobs, both with a reason: `FailoverAttemptTimeout` (default 10s) is what
  lets failover survive a server that accepts the connection and then hangs,
  since `Timeout` is a single budget for the whole request and would otherwise be
  spent on the first endpoint; `FailoverCooldown` (default 5 minutes) stops every
  subsequent request paying the dead endpoint's failure again, which is what
  keeps this from adding load to an API that is already struggling.

  Note that nodes sync pricing independently, so after a failover two calls can
  report different prices for the same card. See
  [docs/getting-started.md](docs/getting-started.md).

### Fixed

- **The response size limit now applies on the caching and GraphQL paths.**
  `MaxResponseBytes` reached neither: the caching handler drained bodies itself
  with an unbounded read, and the GraphQL path buffered the whole body inside
  `HttpClient` before the bounded read could reject it. The real ceiling was
  `HttpContent`'s 2 GB. It matters most with caching enabled through
  `TcgDexClient.Create`, which turns on automatic decompression — decompression
  happens below both, so a megabyte of hostile gzip could expand to roughly a
  gigabyte, and on the caching path was then stored. `TcgDexCachingHandler`'s
  constructor takes an optional `maxResponseBytes`, defaulting to the same
  32 MiB as `TcgDexOptions`.

- **A coalesced waiter is no longer told that someone else's request timed
  out.** When several callers ask for the same URL at once, one fetches and the
  rest wait. A leader that cancelled or hit its own timeout had that failure
  propagated into every waiter, where the transport — filtering on the *waiter's*
  token — reported it as `TcgDexApiException`: "the request timed out after
  00:00:30", to a caller that had waited milliseconds and cancelled nothing.
  Waiters now fall through to their own fetch. A waiter can also stop waiting:
  its own cancellation and its own `Timeout` previously did not apply while it
  was blocked on another caller's request.

- **The GraphQL path applies `TcgDexOptions.Timeout`.** It built no budget at
  all, so the ceiling was `HttpClient`'s 100-second default — the value that
  option exists to replace — and with a caller-supplied `HttpClient` set to
  `InfiniteTimeSpan` there was no ceiling at all. The cancellation contract there
  also now covers `OperationCanceledException` rather than only
  `TaskCanceledException`, so a handler you add through `AddTcgDex` cannot escape
  the single error type.

- **Connection recycling reaches every configured endpoint.** On
  `netstandard2.0` it is set per host, and only the base address had it — so
  after a failover the mirror the client had come to depend on was left
  unrecycled.

  **A documentation correction comes with it, and it may affect you.**
  `TcgDexClient.Create` was documented as recycling connections so a long-lived
  client never pins stale DNS. That holds on `net8.0` and later, and on .NET
  Framework — but **not on `net6.0` or `net7.0`**, which resolve the
  `netstandard2.0` assembly. The only mechanism available there is
  `ServicePoint.ConnectionLeaseTimeout`, which modern .NET ignores
  (`SYSLIB0014`), and nothing that assembly can reach sets a pooled lifetime on
  those runtimes. No behaviour changed; the guarantee was never delivered there
  and is now stated accurately, with the workaround — supply your own
  `HttpClient` over a `SocketsHttpHandler` you configure. See
  [docs/getting-started.md](docs/getting-started.md).

## [0.3.0] - 2026-08-31

### Added

- **Target a regional server node.** `TcgDexOptions.UseMirror(TcgDexMirror)`
  points both the REST and GraphQL endpoints at one of TCGdex's regional nodes
  (`Eu1`/`Eu2`/`Eu3`/`Na1`/`Na2`/`As1`) — for lower latency, or to fail over when
  the default host is unreachable. `BaseAddress` stays the escape hatch for a
  node not in the enum or a local server. The nodes serve the same catalogue;
  only pricing can differ briefly between them, since each syncs on its own
  schedule. See [docs/getting-started.md](docs/getting-started.md).

## [0.2.1] - 2026-08-22

### Fixed

- **`TcgDexClient.Create` no longer leaks a transport handler if the
  `configureCache` callback throws.** The HTTP handler was constructed before the
  caller's `configureCache` delegate ran, so a delegate that threw left that
  handler undisposed — it had not yet been handed to the `HttpClient` that owns
  and disposes it. The callback now runs first, before any handler exists, so no
  handler can leak on that path. Normal configuration is unaffected.

## [0.2.0] - 2026-08-19

### Added

- **A warning is logged when the API serves a malformed card.** When a card
  deserializes with a hole the API left — a nameless attack or ability — the SDK
  logs a `Warning` (event id 1400) naming the card and the field, e.g.
  `TCGdex card 2017sm-5 has malformed data: attack 2 has no name`. The field
  stays an honest `null`; the SDK does not invent a placeholder name, so a caller
  can tell the API produced the hole rather than the SDK. The whole check sits
  behind one `IsEnabled(Warning)` branch, so it costs nothing when warnings are
  off.
- **"When the API is having a moment"** ([`docs/getting-started.md`](docs/getting-started.md))
  — what a `502 Bad Gateway` from TCGdex looks like through the SDK, prompted by
  a real outage: it arrives as `TcgDexApiException` with the status code, a
  gateway body is HTML rather than problem-details JSON so `ex.Problem` is null,
  and failures are never cached. Points at
  `.AddStandardResilienceHandler()` for callers who want retries, and says why
  the SDK still ships none of its own.

- **Property-based testing** with [CsCheck](https://github.com/AnthonyLloyd/CsCheck)
  (Apache-2.0, test-only, never shipped) — seven properties over `BoundedLru` and
  `JsonShape`. [`docs/measuring.md`](docs/measuring.md) documents them, and now
  also carries an honest list of what is **not** measured and why — including the
  GraphQL nested-fetch claim that justifies the whole GraphQL layer and has never
  been timed.

  Test tooling, so no behaviour change. Recorded here only because the page
  describing it is one a consumer may read.

- **Which assembly you get** ([`docs/getting-started.md`](docs/getting-started.md))
  — a resolution table for the three shipped targets. `netstandard2.0` is why
  the NuGet listing shows so many compatible frameworks; it is the universal
  fallback rather than something to enable. Notes that **`net6.0` and `net7.0`
  resolve `netstandard2.0`**, not `net8.0`, so its two behavioural differences
  apply to them.

- **Pokémon TCG Pocket** ([`docs/api-info.md`](docs/api-info.md)) — TCGdex now covers the digital
  game alongside the physical TCG, in the same endpoints, models and id space, with no flag
  saying which is which. Documents how to recognise a Pocket card (serie `tcgp`, the `/tcgp/`
  asset path), and what differs: a separate rarity vocabulary (`One Diamond` … `Crown`),
  Pocket-only `boosters`, `pricing` present but with both providers null, `variantId` as the
  literal `generated`, no `regulationMark`, and no Energy cards at all.

  This accounts for a lot of otherwise puzzling behaviour — unfamiliar rarities, cards with no
  pricing, and languages whose catalogues look truncated.

- **Two verified API findings** ([`docs/api-info.md`](docs/api-info.md)):
  - A broad GraphQL filter can fail outright with
    `Cannot return null for non-nullable field AttacksListItem.name` — the schema
    declares the field non-nullable while some cards have unnamed attacks, so the
    whole query errors rather than omitting the card. REST types the same field
    as optional and is unaffected.
  - The enumeration endpoints are per-language in **values and size**.
    `/categories` is translated, and `pt-br` returns two entries rather than
    three because its pool is TCG Pocket only — Pocket has no Energy cards.

### Fixed

- **A card with a nameless attack or ability no longer fails to deserialize.**
  `Attack.Name` and `Ability.Name` were `required`, so a real card the API serves
  with an unnamed attack threw a `JsonException` and became unreadable *in full* —
  one bad nested field took the whole card down. Both are now `string?`. Found on
  `2017sm-5` (the McDonald's Collection 2017 Pikachu), whose "Electro Ball" attack
  ships with no `name` in the API data; the SDK now reads the card and reports
  that name as `null` rather than rejecting it. `Ability.Name` is the same
  descriptive-nested-object class and was relaxed alongside it. Callers that read
  attack or ability names should treat them as nullable.
- The install page claimed the package targets ".NET 8 and .NET 10", omitting
  `netstandard2.0` — the target that reaches .NET Framework, Unity and everything
  between.

## [0.1.1] - 2026-08-08

### Added

- **Unity guide** ([`docs/unity.md`](docs/unity.md)) — the 21-assembly
  `netstandard2.0` dependency closure to vendor, which seven of those are
  `netstandard2.1` polyfills that can cause duplicate-assembly errors, the
  `link.xml` needed if managed stripping removes the reflectively-read closure
  field, and how to use the SDK on WebGL (where `System.Net.Http` is unavailable)
  via a `UnityWebRequest`-backed `HttpMessageHandler`.

### Changed

- The README no longer claims Unity support flatly. Unity is supported *by
  construction* — no runtime codegen, no `Expression.Compile()`, no
  reflection-based serialization, with a Native AOT publish exercising the one
  reflective path under full trimming — but it has not been run inside a Unity
  project, and saying so plainly is worth more than the unqualified claim.

### Fixed

- `Card.LocalId` and `CardBrief.LocalId` now accept a JSON **number** as well as
  a string, instead of throwing.

  TCGdex documents `localId` as "String or Number", and both properties are
  `required` — so a single unquoted value would fail deserialization of the
  **whole card**, not just that one field. On a list response that means losing
  the entire page.

  No card the API currently serves is affected: `localId` is quoted for every
  card in the full card list, and the GraphQL schema declares it `String!`. This
  brings the SDK in line with the *documented* contract rather than only with
  today's data — the same assumption, made about `attacks[].damage`, is what
  broke an earlier iteration of this SDK on a large share of real cards.

  The value is still surfaced as `string`, so this is source- and
  binary-compatible. Numbers are read as their text form (`136` becomes
  `"136"`).

## [0.1.0] - 2026-08-08

First public release, published via NuGet Trusted Publishing.

### Added

- **Full REST surface** — cards, sets, series, random, and all 13 enumeration
  endpoints, across all 18 languages the API supports.
- **Typed query builder** covering every operator the API actually has
  (`eq` `neq` `gt` `gte` `lt` `lte` `like` `not` `null` `notnull`), translating
  expression trees to query parameters without ever calling
  `Expression.Compile()` — so it stays Native AOT compatible.
- **Opt-in GraphQL** for field projection and nested fetches, which turns the
  N+1 "set plus all its cards" shape into a single request.
- **Two-layer response caching** — bytes revalidated by `ETag`, so a stale entry
  costs a `304` and zero body bytes, plus a typed layer that retains the parsed
  model against the same `ETag`.
- **Auto-pagination** via `StreamAsync`, which handles the short-page end signal
  that the API's missing total count forces on every consumer.
- **Typed image URLs**, including the asymmetry that card artwork takes a quality
  segment and set assets do not.
- **Logging and tracing** through `ILogger` and `ActivitySource`, with no
  dependency on any telemetry vendor.
- **`IHttpClientFactory` integration** via `AddTcgDex()`, and
  `TcgDexClient.Create()` for applications without a container.
- **Configurable request timeout**, defaulting to 30 seconds.
- **Optional pricing deserialization** — on by default, opt out with
  `DeserializePricing = false` to skip the most expensive part of the parse.

### Notes

Targets `net10.0`, `net8.0` and `netstandard2.0`. Verified under Native AOT.
Models were built against verified live API responses, including the traps that
break a naive port: polymorphic `attacks[].damage`, `weaknesses[].value` as a
string, and `boosters` as an array of objects.

[Unreleased]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.8.1...HEAD
[0.8.1]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.6.3...v0.7.0
[0.6.3]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.6.2...v0.6.3
[0.6.2]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.6.1...v0.6.2
[0.6.1]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/releases/tag/v0.1.0
