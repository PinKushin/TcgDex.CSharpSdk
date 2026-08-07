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

## The parse is cached too

The table above is about bytes. There is a second layer above it that is about
**work**, and it exists because the first one could not be.

This cache stores bytes rather than objects, deliberately: it sits on the
`HttpMessageHandler` pipeline, which is what lets `ETag` revalidation work at all
and lets one implementation serve every endpoint. The cost was that a fresh hit
still deserialized the same bytes into the same object on every call — and
deserialization is roughly **86%** of the in-process cost of a request. A hit
avoided the network and paid nearly the full local price anyway.

`TcgDexOptions.MaxDeserializedCacheEntries` (default **64**, zero to disable)
retains the parsed model as well:

| Fetching a card whose bytes are already cached | Time | Allocated |
|---|---:|---:|
| Byte cache only | 25.71 µs | 16.25 KB |
| **With the parse cached** | **1.40 µs** | **2.12 KB** |

**Entries are validated by `ETag`, not by a lifetime of their own.** A stored
model is handed back only when the response carries the exact tag it was built
from — whether the server sent that header or the byte cache replayed it. So a
typed entry can never be staler than the bytes underneath it, and there is no
second expiry policy to keep in step with the first. A response with no `ETag`
is never served from this layer.

Two consequences worth knowing before you rely on it:

- **Callers share one instance.** Two fetches of an unchanged resource now return
  the same object rather than two equal ones. The models are records with
  `init`-only properties, so this is safe for anything the type system permits; a
  caller who casts an `IReadOnlyList<T>` property back to `List<T>` and mutates
  it would corrupt the entry for everyone. Set the bound to zero if that is a
  risk your codebase cannot rule out.
- **The bound counts entries, and parsed objects are large.** The unpaginated
  card list is 2.3 MB on the wire and roughly 8 MB once parsed, which is why the
  default is 64 rather than the byte cache's 512.

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

One more thing it cannot do: **the deserialization cache does not remove
requests.** Without the caching handler in front of it, every fetch still goes to
the network to learn the `ETag`; what it saves is the parse afterwards. The two
layers are worth roughly 20–50 ms and 24 µs respectively, which is the right
order to reach for them in.
