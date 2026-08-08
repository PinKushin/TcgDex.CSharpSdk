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

Nothing yet.

## [0.1.1] - 2026-08-08

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

[Unreleased]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/PinKushin/TcgDex.CSharpSdk/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/PinKushin/TcgDex.CSharpSdk/releases/tag/v0.1.0
