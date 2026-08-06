---
_layout: landing
---

# TcgDex.CSharpSdk

A .NET SDK for the [TCGdex](https://tcgdex.dev) Pokémon TCG API — strongly typed
models, a fluent query builder over the full REST filter syntax, and first-class
support for dependency injection, trimming and Native AOT.

Targets **.NET 8** and **.NET 10**. No API key required.

```bash
dotnet add package TcgDex.CSharpSdk
```

```csharp
builder.Services.AddTcgDex();

var card = await tcgdex.Cards.GetAsync("swsh3-136", ct);
Console.WriteLine(card?.Name);   // Furret
```

---

## Where to go

| | |
|---|---|
| **[Getting started](getting-started.md)** | Install, configure, and make your first calls. |
| **[Querying](querying.md)** | Every filter operator, with the query string each one produces. |
| **[API reference](api/TcgDex.yml)** | Generated from the source. Every public type and member. |
| **[API notes](api-info.md)** | The TCGdex API itself, verified field by field against live responses. |
| **[Architecture](architecture.md)** | How the SDK is built, and how to extend it. |
| **[Learnings](learnings.md)** | Non-obvious behaviour discovered while building it. |
| **[Coverage](coverage.md)** | Test coverage: how it is measured and where it stands. |
| **[Roadmap](roadmap.md)** | What is left before 1.0. |

## Why this exists

TCGdex publishes official SDKs for Java, JavaScript, Kotlin, PHP, TypeScript and
Python. There is no C#/.NET one.

An earlier attempt at this SDK was a hard port of the Java client, and it shipped
roughly a dozen card fields that **do not exist in the API** — along with query
methods built on top of them. This is a rewrite from the API outward: every model
field, every endpoint and every filter operator was verified against live
responses before being written.

## What it gives you

- **Every endpoint** — cards, sets, series, random, and all 13 enumeration
  endpoints.
- **Typed queries** — `Where(c => c.Hp > 100)` becomes `hp=gt:100`, covering all
  ten operators the API actually has.
- **One error contract** — a missing resource returns `null`; everything else
  throws `TcgDexApiException`. Not four exception types depending on which
  method you called.
- **18 languages**, validated at registration rather than failing later as a
  confusing 404.
- **Trim- and AOT-safe** — verified in CI by publishing a native binary and
  running it, not merely asserted.
