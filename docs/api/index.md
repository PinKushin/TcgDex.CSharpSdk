# API reference

Generated from the source, so it always matches the shipped assembly. Every
public type and member carries XML documentation — the build enforces it.

Only the public surface appears here. The transports, resource implementations
and GraphQL wire types are internal, so browsing this is a reliable guide to
what you can actually call.

## Start here

| Type | What it is |
|---|---|
| [ITcgDexClient](TcgDex.ITcgDexClient.yml) | The entry point. Groups every resource. |
| [TcgDexClient](TcgDex.TcgDexClient.yml) | The implementation, constructible without a container. |
| [TcgDexOptions](TcgDex.TcgDexOptions.yml) | Language and endpoint configuration. |
| [TcgDexLanguages](TcgDex.TcgDexLanguages.yml) | The 18 accepted language codes. |
| [TcgDexApiException](TcgDex.TcgDexApiException.yml) | The single exception type the SDK throws. |
| [AddTcgDex](Microsoft.Extensions.DependencyInjection.TcgDexServiceCollectionExtensions.yml) | Dependency-injection registration. |

## Resources

| Type | Covers |
|---|---|
| [ICardResource](TcgDex.Resources.ICardResource.yml) | Cards: get, list, query, detailed search. |
| [ISetResource](TcgDex.Resources.ISetResource.yml) | Sets, including their card lists. |
| [ISerieResource](TcgDex.Resources.ISerieResource.yml) | Series, including their sets. |
| [IRandomResource](TcgDex.Resources.IRandomResource.yml) | Random card, set or series. |
| [ICatalogResource](TcgDex.Resources.ICatalogResource.yml) | All 13 enumeration endpoints. |

## Querying

| Type | What it is |
|---|---|
| [CardQuery](TcgDex.Querying.CardQuery.yml) | The fluent, expression-based query builder. |
| [CardFilter](TcgDex.Querying.CardFilter.yml) | Equality-only filter for the GraphQL search. |
| [QueryOperator](TcgDex.Querying.QueryOperator.yml) | Every operator the API supports. |

See the [querying guide](../querying.md) for the query string each form
produces.

## Models

| Type | Notes |
|---|---|
| [Card](TcgDex.Models.Card.yml) | The centre of the model graph. Fields are populated by category. |
| [CardBrief](TcgDex.Models.CardBrief.yml) | What list endpoints return — four fields only. |
| [Set](TcgDex.Models.Set.yml) / [SetBrief](TcgDex.Models.SetBrief.yml) | A set and its embedded form. |
| [Serie](TcgDex.Models.Serie.yml) / [SerieBrief](TcgDex.Models.SerieBrief.yml) | A series and its embedded form. |
| [Attack](TcgDex.Models.Attack.yml) | Note `Damage` is text; use `BaseDamage` for the number. |
| [Pricing](TcgDex.Models.Pricing.yml) | Cardmarket and TCGplayer, both optional. |
| [TcgPlayerPricing](TcgDex.Models.TcgPlayerPricing.yml) | Printing names are data, so they are a dictionary. |
| [TcgDexProblem](TcgDex.Models.TcgDexProblem.yml) | The API's error body. |

Two things worth knowing before reading the models:

- **Collections are never null.** An absent array arrives empty, so iterating a
  Trainer's `Attacks` is safe.
- **Category decides what is populated.** Pokémon carry `Hp`, `Types`,
  `Attacks`; Trainers carry `TrainerType`, `Effect`; Energy cards carry
  `EnergyType`. Anything category-specific is nullable because the API omits it.

The full namespace list is in the sidebar.
