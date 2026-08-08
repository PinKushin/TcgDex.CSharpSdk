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

### Fixed

- The fixture-drift check could report a **false breaking change**. A path whose
  kind varies across array elements — `attacks[].damage` is genuinely `Number` on
  one card and `String` on another — had its union built in encounter order, so
  the same document fingerprinted as `Number|String` or `String|Number` depending
  on which element came first, and the comparison read that as a retype. Unions
  are now canonically ordered. This affected only the drift check, never
  deserialization.

### Added

- **Tests for `JsonShape`**, the comparison engine every fixture-drift verdict is
  derived from, which had none. They need no network, so CI now runs the
  integration project's non-`Integration` tests on every push — previously the
  code deciding whether the API had changed was itself only exercised in the
  weekly live job.

- **Unity guide** (`docs/unity.md`) — the 21-assembly `netstandard2.0` dependency
  closure to vendor, which seven of those are `netstandard2.1` polyfills that can
  cause duplicate-assembly errors, the `link.xml` needed if managed stripping
  removes the reflectively-read closure field, and how to use the SDK on WebGL
  (where `System.Net.Http` is unavailable) via a `UnityWebRequest`-backed
  `HttpMessageHandler`.

### Changed

- **A field the API starts serving now fails the weekly drift job** instead of
  being written to test output. Additive drift was "reported" only to
  `TestContext.Out` in a job that reported green and nobody opened — which is how
  `pricing`, `variants_detailed` and `updated` came to be served by TCGdex while
  the official JS SDK's types omitted all three. Nothing is blocked by this: the
  drift fixtures run only on a schedule, so no pull request ever sees them.

- The README no longer claims Unity support flatly. Unity is supported *by
  construction* — no runtime codegen, no `Expression.Compile()`, no
  reflection-based serialization, with a Native AOT publish exercising the one
  reflective path under full trimming — but it has not been run inside a Unity
  project, and saying so plainly is worth more than the unqualified claim.

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
