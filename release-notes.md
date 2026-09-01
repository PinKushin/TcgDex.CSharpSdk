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
