# Caching

Opt-in response caching that avoids the network when data is fresh, and avoids
the *payload* even when it isn't.

```csharp
builder.Services.AddTcgDexWithCaching();
```

That is the whole setup. Everything below is tuning.

---

## Why it works the way it does

The API sends `Cache-Control: no-cache, no-store, must-revalidate`, so nothing
caches by default and enabling this is a decision about your own tolerance for
stale data.

But it also sends a weak `ETag` and **honours `If-None-Match`**. That changes the
economics entirely:

```
GET /v2/en/sets/swsh3                          200   21,922 bytes
GET /v2/en/sets/swsh3   If-None-Match: W/"…"   304        0 bytes
```

So an expired entry does not mean a re-download. It means a revalidation that
costs a round trip and **zero bytes of body** unless the data actually changed.

## The three paths

| Path | When | Network | Body transferred |
|---|---|---|---|
| **Fresh hit** | within the freshness window | none | none |
| **Revalidation** | past the window, `ETag` held, unchanged | one `304` | **0 bytes** |
| **Miss** | no entry, or content changed | one `200` | full |

Time-to-live therefore controls *how long you serve data without asking*, not how
long before you pay for it again. That makes short windows cheap — the default
for a single card is one minute, because cards embed pricing.

## Defaults

| Resource | Window | Reason |
|---|---|---|
| Enumeration endpoints (`/rarities`, `/types`, …) | **12 hours** | Change when an expansion ships, and applications hit them on every screen to build filters. |
| Single card (`/cards/{id}`) | **1 minute** | Embeds market pricing, which moves daily. |
| Everything else | **5 minutes** | Sets, series, card lists. |

Entries are retained past their window so the `ETag` stays usable — that is what
turns a re-download into a `304`.

## Tuning

```csharp
builder.Services.AddTcgDexWithCaching(
    configureCache: cache =>
    {
        cache.DefaultTimeToLive = TimeSpan.FromMinutes(15);
        cache.CatalogTimeToLive = TimeSpan.FromDays(1);
        cache.PricingTimeToLive = TimeSpan.FromSeconds(30);
        cache.MaxEntries = 2048;
    });
```

For a policy that isn't path-based, override `GetTimeToLive`:

```csharp
public sealed class MyPolicy : TcgDexCacheOptions
{
    public override TimeSpan GetTimeToLive(Uri requestUri) => TimeSpan.FromMinutes(2);
}
```

## What is never cached

- **Anything that is not a `GET`.** The GraphQL endpoint is a `POST`, so
  `SearchDetailedAsync` always goes to the network.
- **Failures.** A cached `404` would suppress a card that appears later, and a
  cached `5xx` would turn a momentary blip into a persistent outage for the
  caller. An error also evicts any stale entry for that URL.

## Stampede protection

Concurrent requests for the same URL are collapsed into one. Without this a cold
cache under load sends one request per caller for the same resource — ten
concurrent reads of the same card become ten requests.

It is on by default and can be turned off:

```csharp
cache.CoalesceConcurrentRequests = false;
```

Only the cacheable result is shared. An `HttpResponseMessage` has a single
content stream, so a non-cacheable response cannot be handed to several waiters
and those callers issue their own request.

## Replacing the store

The default is bounded and per-process. To share a cache across instances,
implement `ITcgDexResponseCache` and register it before calling
`AddTcgDexWithCaching`:

```csharp
builder.Services.AddSingleton<ITcgDexResponseCache, RedisResponseCache>();
builder.Services.AddTcgDexWithCaching();
```

The interface stores raw bodies rather than deserialized models, so one entry
serves every caller regardless of the type they deserialize into, and nothing
mutable is shared.

## Checking it works

The handler counts what it did:

```csharp
var handler = new TcgDexCachingHandler(new MemoryTcgDexResponseCache());

handler.FreshHits;      // served with no network at all
handler.Revalidations;  // refreshed by a 304, no body transferred
handler.Misses;         // full responses fetched
```

## Without a container

```csharp
var caching = new TcgDexCachingHandler(new MemoryTcgDexResponseCache())
{
    InnerHandler = new HttpClientHandler(),
};

using var http = new HttpClient(caching);
var tcgdex = new TcgDexClient(http, new TcgDexOptions());
```

## A note on what caching cannot speed up

Filtering and sorting happen **server-side** — `Where(c => c.Hp > 100)` becomes
`hp=gt:100` and the API does the work. There is no local index to optimise.

So the only way to make a search faster is to not send it, which is what caching
does for repeated identical queries. A query whose parameters differ is a new URL
and therefore a new request. If you need genuinely local search over a fixed
working set, fetch the set once and query it in memory with LINQ — that is a
different tool from this one, and the SDK does not pretend otherwise.
