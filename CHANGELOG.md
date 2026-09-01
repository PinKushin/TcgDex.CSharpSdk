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
  across endpoints is evasion rather than resilience. Only `GET` is retried, at
  most three endpoints per request.

  Two knobs, both with a reason: `FailoverAttemptTimeout` (default 10s) is what
  lets failover survive a server that accepts the connection and then hangs,
  since `Timeout` is a single budget for the whole request and would otherwise be
  spent on the first endpoint; `FailoverCooldown` (default 5 minutes) stops every
  subsequent request paying the dead endpoint's failure again, which is what
  keeps this from adding load to an API that is already struggling.

  Note that nodes sync pricing independently, so after a failover two calls can
  report different prices for the same card. See
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

[Unreleased]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/releases/tag/v0.1.0
