# Roadmap

State at `3b91de6`, **published to NuGet as 0.1.0**. The SDK covers the full TCGdex REST surface, builds clean on
`netstandard2.0`, `net8.0` and `net10.0`, and is verified under Native AOT.

| | |
|---|---|
| Tests | **480 unit × 3 frameworks + 149 integration** |
| Mutation score | **88.19%**, break threshold 85 |
| Line coverage | **99.80%**, gated at 99.5 |
| Branch coverage | **96.53%**, gated at 95 |
| Warnings | zero — compiler, analyzers, DocFX, CI annotations |
| Docs | published to GitHub Pages on every push to `main` |
| Package | **published, 0.1.0**, 462 KB, three target frameworks |

Published 2026-08-08 via Trusted Publishing — no API key was ever created.

---

## Before 1.0

### 1. Publish to NuGet — done

**0.1.0 shipped 2026-08-08** via Trusted Publishing, from a `workflow_dispatch`
run of [`release.yml`](https://github.com/PinKushin/TcgDex.CSharpSdk/blob/main/.github/workflows/release.yml).
**No API key was ever created** — a laptop cannot mint a GitHub OIDC token, but a
button press runs inside Actions, which can.

Two facts that do not stop being true now it is done: a published version can be
unlisted but never deleted, and `TcgDex.CSharpSdk` is claimed permanently.

### 2. Get listed on tcgdex.dev/sdks — *the only remaining item*

TCGdex lists official SDKs for Java, JavaScript, Kotlin, PHP, TypeScript and
Python. Checked 2026-08-07: **still no C#/.NET one**, and the *Community SDKs*
section is empty, so this would be the first entry in it.

**The route is Discord, not a pull request.** The site says "Contact us on
Discord to have your SDK added here"; an earlier version of this page said to
open a PR against `tcgdex/documentation`, which is not what they ask for.

This is the step that makes publishing worth having done. A package with no
distribution is a repository with extra steps; a link on the API's own
documentation is the one place a person looking for a .NET client actually
passes through.

---

## Committed — waiting on upstream

Decided, not started, and deliberately blocked on TCGdex shipping the field —
because modelling against a *guessed* shape is the exact mistake the rewrite
existed to undo (the old SDK shipped ~10 fields the API never served).

- **Model `thirdParty` external IDs.** [`cards-database#2184`](https://github.com/tcgdex/cards-database/pull/2184)
  (merged 2026-08-27) removes the `deepOmit` that stripped `thirdParty` IDs
  (tcgplayer / cardmarket product IDs) from card responses, so they will start
  appearing as a new field. As of 2026-08-28 it is **not yet live** on
  `api.tcgdex.net` — `base1-4` shows no `thirdParty` field. Additive and
  non-breaking (the SDK ignores unknown fields), so there is no rush and no risk
  in waiting. The daily [`live-api.yml`](https://github.com/PinKushin/TcgDex.CSharpSdk/blob/main/.github/workflows/live-api.yml) drift
  check is the trigger: when the Live API run goes red on it, the failure message
  carries the exact shape — model against *that*, verify it round-trips, ship it.

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
  and zero bytes rather than a re-download — plus a second layer that retains the
  deserialized model against its ETag, taking a warm hit from 25.71 µs to
  1.40 µs. See [`caching.md`](caching.md).
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
- **Fuzzed and API-gated** — every path that consumes input the SDK did not
  produce is driven with corrupted fixtures on every push, and by
  coverage-guided fuzzing across seven modes weekly (1.8M executions, no
  crashes). The public surface is pinned by an approval test, so a breaking
  change cannot merge without someone accepting the diff. Tested on Linux,
  Windows and macOS.
- **Fixture drift detection** — the recorded responses every offline test relies
  on are re-fetched weekly and compared by shape, so an API change fails with a
  precise message instead of silently invalidating the offline suite.
- **Benchmarked at the sizes and states that actually occur** — the 2.3 MB
  unpaginated card list, and a response cache sitting at its bound. Both found
  something the small-payload benchmarks could not: a capacity hint previously
  written off as useless saves 2.24 MB per request there, and
  `ConcurrentDictionary.Count` was costing a cache store up to 17× the
  operation it was guarding. See [`measuring.md`](measuring.md).

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
