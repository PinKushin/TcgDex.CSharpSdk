# Logging and tracing

The SDK emits structured logs through `ILogger` and distributed-tracing spans
through `ActivitySource`. Both are **free when nobody is listening** and require
no configuration to stay silent.

Crucially, the SDK does **not** depend on any telemetry product. It writes to
.NET's standard abstractions, and whatever you have wired up — Serilog, Seq,
Application Insights, Datadog, OpenTelemetry, Sentry — receives it automatically.

---

## Logging

With dependency injection there is nothing to do. `AddTcgDex` picks up the
container's `ILoggerFactory`:

```csharp
builder.Services.AddTcgDex();
```

Without a container, pass one in:

```csharp
using TcgDexClient client = TcgDexClient.Create(loggerFactory: myLoggerFactory);

// or
TcgDexClient client = new(httpClient, options, myLoggerFactory);
```

Everything is logged under the single category **`TcgDex`**, so one rule
configures all of it:

```json
{
  "Logging": {
    "LogLevel": {
      "TcgDex": "Debug"
    }
  }
}
```

### What you get at each level

| Level | What appears |
|---|---|
| `Trace` | Cache hits, misses, coalesced requests. High volume. |
| `Debug` | Every request and its status and duration; cache revalidations; missing resources. |
| `Information` | Client configuration, once at startup. Nothing per-request. |
| `Warning` | Timeouts, unparseable responses, dropped GraphQL entries. |
| `Error` | Failed requests with the API's own problem detail. |

**A missing card logs at `Debug`, not `Warning`.** Asking for something that does
not exist is an ordinary outcome that returns `null`, and logging it louder makes
normal use look broken.

**Nothing is logged at `Information` per request.** A library that does floods
its consumer's default configuration, which is how people end up turning SDK
logging off entirely.

### Event ids

Stable across versions, so you can alert on them.

| Range | Area | Ids |
|---|---|---|
| 1000 | Request lifecycle | 1000 sending · 1001 completed · 1002 not found · 1003 failed · 1004 errored · 1005 timed out · 1006 deserialization failed |
| 1100 | Caching | 1100 hit · 1101 revalidated · 1102 miss · 1103 coalesced · 1104 evicted |
| 1200 | Configuration | 1200 configured |
| 1300 | GraphQL | 1300 search completed · 1301 errors · 1302 dropped entries |

### Cost when disabled

Messages are built with the `LoggerMessage` source generator, which emits a
cached delegate and an `IsEnabled` check per message. A disabled level costs a
branch — no string formatting, no boxing, no allocation.

That is not true of interpolated logging: `logger.LogDebug($"…{uri}")` formats
the string and boxes the arguments *before* the level is checked, so it costs
something on every call whether or not anyone is listening. A test asserts that
nothing is formatted when logging is off.

### What is not logged

- **Raw HTTP request and response bodies.** When registered through
  `AddTcgDex`, `IHttpClientFactory` already logs every request and its timing
  under `System.Net.Http.HttpClient`. Duplicating that would double the noise
  and disagree on the detail.
- **Response payloads.** The API carries no secrets, but logging whole bodies is
  a habit that goes badly wrong the moment an SDK is used against something that
  does.

---

## Tracing

Subscribe to the source and spans flow into any OpenTelemetry-compatible
backend:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(TcgDexActivity.SourceName)   // "TcgDex.CSharpSdk"
        .AddHttpClientInstrumentation());
```

Each SDK operation becomes one client span tagged with `url.full`,
`http.request.method` and `http.response.status_code`. Failures set the span
status to `Error` with an `error.type` tag, following OpenTelemetry conventions
so they surface as errors without extra mapping.

Spans cover **SDK operations**, not raw HTTP. `HttpClient` emits its own spans,
so adding `AddHttpClientInstrumentation` nests the transport detail underneath
rather than duplicating it.

When nothing subscribes, starting an activity returns `null` and the cost is a
null check.

---

## Using Sentry, Datadog, or anything else

Wire it up in **your application**, not through this SDK. Sentry's logging
integration is an example:

```csharp
builder.Logging.AddSentry();
```

Every SDK log then flows into Sentry with no further work, because the SDK
writes to `ILogger` rather than to a specific backend.

This is deliberate, and it is worth being explicit about why the SDK does not
take that dependency itself:

- **A library must not choose your telemetry vendor.** A hard dependency would
  force Sentry on every consumer, including those using something else or
  nothing at all — along with its version constraints and transitive packages.
- **A library must never phone home.** An SDK that reported errors to its
  *author's* account from *your* process would be exfiltrating your data to a
  third party you never agreed to. That is a property of malware, not of a
  well-behaved dependency, regardless of intent.
- **`ILogger` and `ActivitySource` already are the integration point.** Anything
  a vendor-specific dependency would buy is available by configuring that vendor
  in the application that owns the decision.

The same reasoning applies to metrics, crash reporting and alerting: the SDK's
job is to emit good signals through standard abstractions and let the application
decide where they go.
