# TCGdex v2 API Reference

Every statement in this document was verified against the live API on **2026-08-05** by direct
request. Where the official documentation at tcgdex.dev disagrees with observed behaviour, the
observed behaviour is documented and the discrepancy is called out.

> **This file is the SDK's specification.** If the SDK and this document disagree, one of them is
> a bug. Do not describe behaviour here that has not been observed from a real response.

---

## 1. Transport

| | Value |
|---|---|
| REST base | `https://api.tcgdex.net/v2/{lang}` |
| GraphQL | `https://api.tcgdex.net/v2/graphql` (POST) |
| Assets | `https://assets.tcgdex.net` |
| Methods | `GET` only. No POST/PUT/DELETE on REST. |
| Auth | None. Public API. |
| HTTPS | Required; HTTP redirects to HTTPS. |
| CORS | `Access-Control-Allow-Origin: *` |
| Caching | Responses send `Cache-Control: no-cache, no-store, must-revalidate` and a weak `ETag`. |

There is **no** `/v2/{lang}` index endpoint — it returns 404.

### Languages

18 accepted, confirmed from the API's own error payload:

```
en  fr  es  es-mx  it  pt  pt-br  pt-pt  de  nl  pl  ru  ja  ko  zh-tw  id  th  zh-cn
```

An unsupported code returns HTTP 404 with `type: .../errors/language-invalid` and a `details`
string enumerating the valid set.

**Accepted is not the same as populated.** Four languages route successfully but have no card
data, returning HTTP 200 with empty arrays rather than an error:

| Language | Cards | Catalogs | Sets |
|---|---|---|---|
| `pt-pt` | none | empty | 0 |
| `nl` | none | empty | 3 |
| `pl` | none | empty | 2 |
| `ru` | none | empty | 9 |

The other 14 serve full data. A client must treat an empty result as valid rather than as a
failure.

**Card ids are not universal across languages.** Each language is backed by its own card pool:
`swsh3-136` is a Western card and returns 404 in `ja`, `ko`, `th`, `id`, `zh-cn` and `pt-br`,
whose databases contain different sets entirely. To work in an arbitrary language, take ids from
that language's own list endpoint rather than assuming a shared id resolves.

Names are genuinely localised where the pool is shared — `swsh3-136` is *Furret* in `en`,
*Fouinar* in `fr`, and *Wiesenior* in `de`.

**The enumeration endpoints are per-language, in values and in size.** `/categories` returns
translated values, so they cannot be compared across languages:

| Language | `/categories` |
|---|---|
| `en`, `ja`, `ko`, `id`, `th`, `zh-tw`, `zh-cn`, `es-mx` | `Energy`, `Pokemon`, `Trainer` |
| `fr` | `Dresseur`, `Pokémon`, `Énergie` |
| `es` | `Energía`, `Entrenador`, `Pokémon` |
| `it` | `Allenatore`, `Energia`, `Pokémon` |
| `de` | `Energie`, `Pokémon`, `Trainer` |
| `pt` | `Energia`, `Pokémon`, `Treinador` |
| **`pt-br`** | **`Pokemon`, `Trainer` — two, not three** |

`pt-br` is not missing data. These endpoints report the values that language's cards *actually
use*, and `pt-br`'s pool is **TCG Pocket only** — all 11 of its sets are Pocket sets (`A1`,
`A1a`, `A2`, `A2a`, `A2b`, `A3`, `A4a`, `B1a`, `B2`, `B2a`, `P-A`) against 218 for `en`. Pocket
has no Energy cards; energy there is a game mechanic rather than a collectible card, so
`?category=eq:Energy` in `pt-br` correctly returns `[]`.

**Consequence:** never hard-code an enumeration result, and never assume one language's catalogue
size matches another's. Fetch `/categories` in the language you are querying.

---

## 2. Errors

The live API returns an [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457)-shaped
problem document. RFC 9457 obsoletes RFC 7807 — the wire format is the same, so
older references to 7807 describe the same shape:

```json
{
  "type": "https://tcgdex.dev/errors/not-found",
  "title": "The resource you are trying to reach does not exists",
  "status": 404,
  "endpoint": "/en/cards/does-not-exist-999",
  "method": "GET"
}
```

Language errors add two fields:

```json
{
  "type": "https://tcgdex.dev/errors/language-invalid",
  "title": "The chosen language is not available in the database",
  "status": 404, "endpoint": "/zz/cards/swsh3-136", "method": "GET",
  "lang": "zz",
  "details": "You must use one of the following languages (en, fr, ...) while you used \"zz\""
}
```

> **Discrepancy.** `tcgdex.dev/rest/card` still documents the error body as
> `{"error": "Endpoint or id not found"}`. That form was **not** observed from the live API.
> Clients should parse the problem-document shape above and tolerate the legacy shape.

Note that a bad *language* also yields 404, not 400 — so a 404 alone does not prove the resource
is missing. Discriminate on `type`.

---

## 3. Filtering, sorting, pagination

**Filters are top-level query parameters.** There is no `q` parameter.

```
GET /v2/en/cards?name=eq:Furret&hp=gt:100&sort:field=name&sort:order=DESC&pagination:page=2&pagination:itemsPerPage=50
```

### Operators

| Operator | Syntax | Meaning |
|---|---|---|
| *(default)* / `like:` | `name=pika` | Laxist substring match |
| `not:` | `name=not:pika` | Laxist exclusion |
| `eq:` | `name=eq:Furret` | Exact match |
| `neq:` | `name=neq:Furret` | Exact exclusion |
| `gt:` | `hp=gt:100` | Greater than (numeric) |
| `gte:` | `hp=gte:100` | Greater or equal |
| `lt:` | `hp=lt:100` | Less than |
| `lte:` | `hp=lte:100` | Less or equal |
| `null:` | `effect=null:` | Field absent |
| `notnull:` | `effect=notnull:` | Field present |

- **Wildcards** apply to laxist filters: `name=*chu` (ends with), `name=fu*` (starts with).
  - A bare `name=*` is valid — **200 with every card**, i.e. match anything.
    Verified 2026-08-06. `name=` (no value at all) behaves the same way, while
    `name=eq:` is an equality test against the empty string and returns `[]`.
  - The server URL-decodes before interpreting, so `name=%2A` is treated
    identically to `name=*`. The SDK still sends the wildcard literally and
    escapes only the surrounding value, which keeps the query readable.
- **OR** within one field uses `|`: `name=eq:Furret|Pikachu`.
- **AND** across fields: repeat parameters — `?category=Pokemon&rarity=eq:Rare`.

### Sorting

`sort:field={property}` and `sort:order=ASC|DESC`. Default order is `ASC`; default field
resolution is `releaseDate > localId > id`.

### Pagination

`pagination:page={n}` (default 1) and `pagination:itemsPerPage={n}` (default 100 when paginating).

> **No total count is exposed.** List responses are bare arrays with no envelope, and no
> `X-Total-Count`/`Content-Range` pagination headers are sent. A client cannot compute the number
> of pages up front; iterate until a short page is returned.

---

## 4. Endpoints

### Core

| Path | Returns |
|---|---|
| `/cards` | `CardBrief[]` |
| `/cards/{id}` | `Card` |
| `/sets` | `SetBrief[]` |
| `/sets/{id}` | `Set` (includes its `cards`) |
| `/series` | `SerieBrief[]` |
| `/series/{id}` | `Serie` (includes its `sets`) |
| `/random/card` | `Card` |
| `/random/set` | `Set` |
| `/random/serie` | `Serie` |

### Enumeration endpoints

Each returns a bare array of scalars — useful for building valid filter values.

| Path | Element type | Sample values |
|---|---|---|
| `/categories` | `string[]` | `Energy`, `Pokemon`, `Trainer` |
| `/rarities` | `string[]` | `Common`, `Uncommon`, `Crown`, `ACE SPEC Rare`, … |
| `/types` | `string[]` | `Colorless`, `Darkness`, `Dragon`, `Fairy`, `Fighting`, `Fire`, `Grass`, `Lightning`, `Metal`, `Psychic`, … |
| `/illustrators` | `string[]` | `5ban Graphics`, `"Big Mama" Tagawa`, … |
| `/stages` | `string[]` | `Basic`, `Stage1`, `Stage2`, `BREAK`, `LEVEL-UP`, `MEGA`, `RESTORED`, `V-UNION`, `VMAX`, `VSTAR` |
| `/suffixes` | `string[]` | `EX`, `GX`, `Legend`, `Prime`, `SP`, `TAG TEAM-GX`, `V`, `ex` |
| `/variants` | `string[]` | `firstEdition`, `holo`, `normal`, `reverse`, `wPromo` |
| `/energy-types` | `string[]` | `Normal`, `Special` |
| `/regulation-marks` | `string[]` | `D`–`J`, `None` |
| `/trainer-types` | `string[]` | `Item`, `Rocket's Secret Machine`, `Stadium`, `Supporter`, `Technical Machine`, `Tool` |
| `/retreats` | `int[]` | `1`–`5` |
| `/hp` | `int[]` | `10`, `30`, `40`, … |
| `/dex-ids` | `int[]` | `1`, `2`, `3`, … |

Note `/retreats`, `/hp` and `/dex-ids` yield **numbers**, not strings — a single deserializer for
"list of scalars" must handle both.

---

## 5. Models

### Card — `GET /cards/{id}`

Always present: `id`, `name`, `category`, `localId`, `set`, `illustrator`, `variants`,
`variants_detailed`, `updated`.
`image` is documented as required but is **absent on some cards** (e.g. `exu-!`).

| Field | Type | Notes |
|---|---|---|
| `id` | `string` | `{setId}-{localId}`, e.g. `swsh3-136`. May be URL-encoded (`exu-%3F`). |
| `localId` | `string` | Not numeric — `"!"` and `"%3F"` occur. |
| `name` | `string` | |
| `category` | `string` | `Pokemon` \| `Trainer` \| `Energy`. **This is the card's type field; there is no `type` field.** |
| `illustrator` | `string` | |
| `image` | `string` | Base URL **without extension** — see §6. Optional in practice. |
| `rarity` | `string` | |
| `set` | `SetBrief` | |
| `variants` | `Variants` | |
| `variants_detailed` | `DetailedVariant[]` | |
| `updated` | `string` | ISO 8601 with offset, e.g. `2026-07-01T21:27:44+01:00`. |
| `pricing` | `Pricing` | Optional. |
| `legal` | `Legal` | |
| `regulationMark` | `string` | Modern cards only. |
| `boosters` | `Booster[]` | **Array of objects**, not a string. Observed on `A4-139`: `[{"id":"boo_A4-ho-oh","name":"Ho-Oh"}]` — only `id` and `name` were present. |
| `stage` | `string` | Not Pokémon-exclusive — observed on the Energy card `base1-102`. |
| **Pokémon** | | |
| `hp` | `int` | |
| `types` | `string[]` | Elemental types. Distinct from `category`. |
| `dexId` | `int[]` | |
| `evolveFrom` | `string` | |
| `description` | `string` | Flavour text. |
| `suffix` | `string` | Observed: `"EX"` (`xy1-1`), `"ex"` (`sv03.5-006`). Case is meaningful. |
| `attacks` | `Attack[]` | |
| `abilities` | `Ability[]` | Observed on `base1-1`, `pl1-1`. |
| `weaknesses` | `WeakRes[]` | |
| `resistances` | `WeakRes[]` | Rarer than `weaknesses`; observed on `pl1-1`. |
| `retreat` | `int` | |
| **Trainer** | | |
| `trainerType` | `string` | `Item`, `Supporter`, `Tool`, `Stadium`, … |
| `effect` | `string` | |
| **Energy** | | |
| `energyType` | `string` | `Normal` \| `Special`. Observed on `base1-102`. |

**Declared in the GraphQL schema but not observed in any REST response:** `level`, `item`.
Treat both as optional and absent-by-default until a real payload proves otherwise.

#### Attack

| Field | Type | Notes |
|---|---|---|
| `name` | `string` | |
| `cost` | `string[]` | Energy **names**, e.g. `["Grass","Grass","Colorless"]`. Not a number. |
| `damage` | `int` \| `string` | **Polymorphic.** `60` on `xy1-1`; `"50+"` on `swsh1-1`. Absent on effect-only attacks. GraphQL normalizes it to `String`. |
| `effect` | `string` | |

#### Ability
`name: string`, `type: string`, `effect: string`.
Observed `type` values include `Pokemon Power` (`base1-1`) and `Poke-BODY` (`pl1-1`) — era-specific
labels, so treat as free text rather than an enum.

#### WeakRes (weaknesses / resistances)
`type: string`, `value: string`. **`value` is always a string**, never numeric — observed `"×2"`
(multiplier) and `"-20"` (signed modifier).

#### Item
`name: string`, `effect: string`. GraphQL schema only — not observed in a REST response.

#### Variants
`normal`, `reverse`, `holo`, `firstEdition`, `wPromo` — all `bool`.

#### DetailedVariant
`type: string`, `subtype: string`, `size: string`, `stamp: string[]`, `foil: string`,
`variantId: string`, `pricing: Pricing`.

> `variantId` and `pricing` appear in REST responses but **not** in the GraphQL schema.

#### Pricing
`cardmarket` and `tcgplayer`, each optional and independently nullable.

- `cardmarket`: `updated`, `unit` (`EUR`), `idProduct`, then numeric keys `avg`, `low`, `trend`,
  `avg1`, `avg7`, `avg30` and holo counterparts `avg-holo`, `low-holo`, `trend-holo`, `avg1-holo`,
  `avg7-holo`, `avg30-holo`. **Hyphenated keys require explicit name mapping.** Values are
  nullable.
- `tcgplayer`: `unit` (`USD`), `updated`, plus one object **per variant name**. Observed keys:
  `normal` and `reverse-holofoil` (`swsh3-136`, `sv03.5-001`), `holofoil` (`base1-4`, `xy1-1`).
  Each holds `productId`, `lowPrice`, `midPrice`, `highPrice`, `marketPrice`, `directLowPrice`
  (any price may be `null`).

> The tcgplayer variant keys are **data, not a fixed schema** — model as a dictionary, not as
> fixed properties.

Pricing appears both at the card root and inside each `variants_detailed[]` entry.

### CardBrief — list responses

`id`, `localId`, `name`, `image` (optional). Nothing else — `category`, `rarity` and
`trainerType` are **not** in list results; fetch the card to get them.

### Set — `GET /sets/{id}`

`id`, `name`, `logo`, `symbol`, `abbreviation`, `cardCount`, `releaseDate`, `legal`, `serie`,
`tcgOnline`, `cards` (`CardBrief[]`).

### SetBrief — embedded in a card
`id`, `name`, `logo`, `symbol`, `cardCount`.

### CardCount
`official`, `total`, and optionally `normal`, `holo`, `reverse`, `firstEd` — all `int`.

### Serie — `GET /series/{id}`
`id`, `name`, `logo`, `releaseDate`, `firstSet`, `lastSet`, `sets` (`SetBrief[]`).
List form (`/series`) is `id`, `name`, `logo`.

### Legal
`standard: bool`, `expanded: bool`

### Booster
GraphQL declares `id`, `name`, `logo`, `artwork_front`, `artwork_back` — all `string`.
The one REST payload observed (`A4-139`) carried only `id` and `name`, so every field except `id`
should be treated as optional.

---

## 6. Assets

The card's `image` and the set's `logo`/`symbol` are **base URLs without a file extension** —
but the two are addressed **differently**, which is easy to miss because the fields look alike.

**Card artwork** takes a quality segment:

```
{image}/{quality}.{format}      quality = high | low       format = png | webp | jpg
https://assets.tcgdex.net/en/swsh/swsh3/136/high.png     -> 200 image/png
https://assets.tcgdex.net/en/swsh/swsh3/136/low.webp     -> 200 image/webp
https://assets.tcgdex.net/en/swsh/swsh3/136.png          -> 404
```

**Set logos and symbols take no quality at all** — only an extension:

```
{logo}.{format}
https://assets.tcgdex.net/en/swsh/swsh3/logo.png          -> 200 image/png
https://assets.tcgdex.net/en/swsh/swsh3/logo/high.png     -> 404
https://assets.tcgdex.net/univ/swsh/swsh3/symbol.png      -> 200 image/png
```

Applying the card pattern to a logo returns 404. Set logos are language-scoped (`/en/...`) while
symbols are served language-neutral from `/univ/...`.

---

## 7. Pokémon TCG Pocket

TCGdex now covers **Pokémon TCG Pocket**, the digital game, alongside the physical TCG. Its cards
share the same endpoints, the same models and the same id space as printed cards — there is no
separate API and no flag that says "this is a Pocket card". Verified 2026-08-08.

This is where a good deal of apparent strangeness comes from: rarities you have never seen, cards
with no printings, and languages whose catalogues look truncated.

### Recognising a Pocket card

There is no boolean. Three reliable markers, in order of directness:

| Marker | Value |
|---|---|
| Serie | `tcgp` — *Pokémon TCG Pocket* |
| Set ids | `A1`, `A1a`, `A2`, `A2a`, `A2b`, `A3`, `A4`, `A4a`, `B1a`, `B2`, `B2a`, `P-A`, … |
| Asset path | `https://assets.tcgdex.net/{lang}/tcgp/{set}/{localId}` |

The asset path is the one that survives new set ids: the `/tcgp/` segment is present on every
Pocket image and absent from every physical one.

Scale in `en` as of 2026-08-08: **15 Pocket sets out of 218**. In `pt-br` it is **all 11 of
them** — that language has no physical coverage at all.

### What differs

| | Physical TCG | TCG Pocket |
|---|---|---|
| `rarity` vocabulary | `Common`, `Uncommon`, `Rare`, `Holo Rare`, `Illustration rare`, … | `One Diamond`, `Two Diamond`, `Three Diamond`, `Four Diamond`, `One Star`, `Two Star`, `Three Star`, `One Shiny`, `Two Shiny`, `Crown` |
| `boosters` | absent | present — Pocket's pack structure, e.g. `{ "id": "boo_A4-ho-oh", "name": "Ho-Oh" }` |
| `pricing` | populated from CardMarket / TCGplayer | **present but empty**: `{"cardmarket": null, "tcgplayer": null}` |
| `variants_detailed[].variantId` | a real id, e.g. `endfynwn4n10gzq` | the literal string `generated` |
| `regulationMark` | present on Standard-legal cards | absent |
| Energy cards | yes | **none** — energy is a game mechanic in Pocket, not a collectible card |

`legal` is present on both.

### Consequences for a client

- **`/rarities` is the union of two disjoint vocabularies.** Filtering `rarity=eq:Common` returns
  only physical cards; `rarity=eq:One Diamond` returns only Pocket cards. Nothing in the response
  says which game a rarity belongs to, so a rarity picker built from that endpoint will mix them
  with no separator.
- **A non-null `pricing` does not mean there is pricing.** Pocket cards carry the container with
  both providers null, so check `Cardmarket`/`Tcgplayer` rather than the object itself. A digital
  card has no secondary market to price.
- **A language can be Pocket-only.** `pt-br` is, which is why its `/categories` returns two values
  rather than three — see [Languages](#languages). Its card ids will not resolve in `en`.
- **`boosters` is Pocket-only**, and there is no `/boosters` endpoint (404). The data exists only
  embedded in a card.

### Why this matters for the SDK's design

Pocket is the part of this API that is actively growing, and it arrived carrying a **whole new
rarity vocabulary**. Every one of those values would have thrown on a client that had modelled
`rarity` as a closed enum, because they did not exist when such an enum would have been written.

That is the concrete argument for the SDK typing `rarity`, `stage`, `suffix`, `category`, `types`,
`trainerType` and `energyType` as `string` with the known values exposed as constants: an
enumeration that grows is a source of new values, not a fixed set. See
[architecture.md](architecture.md).

---

## 8. GraphQL

`POST https://api.tcgdex.net/v2/graphql`, body `{"query": "..."}`. Introspection is enabled.

### Root fields

```graphql
cards  (filters: CardsFilters,  pagination: Pagination, sort: Sort): [Card]
sets   (filters: SetFilters,    pagination: Pagination, sort: Sort): [Set]
series (filters: SerieFilters,  pagination: Pagination, sort: Sort): [Serie]
card   (id: ID, set: String, filters: CardsFilters): Card
set    (id: ID, filters: SetFilters): Set
serie  (id: ID, filters: SerieFilters): Serie
```

`Pagination { page, itemsPerPage }` · `Sort { field, order }`

`CardsFilters` accepts: `category`, `description`, `energyType`, `evolveFrom`, `hp`, `id`,
`localId`, `dexId`, `illustrator`, `level`, `name`, `rarity`, `regulationMark`, `stage`, `suffix`,
`trainerType`, `retreat`.
`SetFilters`: `id`, `name`, `serie`, `releaseDate`, `tcgOnline`. `SerieFilters`: `id`, `name`.

### Limits — verified, and they matter

1. **No language support.** There is no language argument or path segment, and an
   `Accept-Language: fr` header is ignored. Responses are always English.
2. **Equality-only filtering.** Filter fields are typed scalars, so `hp` accepts an `Int`.
   Passing `hp: "gt:100"` fails with
   `Int cannot represent non-integer value: "gt:100"`. No ranges, OR, wildcards, or null checks.
3. **No pricing.** `Card` has no `pricing` field, and `DetailedVariants` exposes only
   `type`, `subtype`, `size`, `stamp`, `foil` — no `variantId`, no `pricing`.
4. **A broad filter can fail outright on a schema/data mismatch.** Verified 2026-08-08:

   ```
   POST /v2/graphql  { cards(filters: { rarity: "Common" }) { … attacks { name } } }

   Cannot return null for non-nullable field AttacksListItem.name.
   ```

   The schema declares `AttacksListItem.name` non-nullable, but some cards have attacks with no
   name. GraphQL cannot return a partial list for a non-nullable field, so the *entire query*
   errors rather than omitting the offending card. A narrow filter that happens to miss those
   cards succeeds, which makes this look intermittent — it is not; it is determined by whether
   the result set contains an unnamed attack.

   The same cards deserialize without complaint over REST, which types the field as optional.
   **Consequence:** a caller cannot rely on `SearchDetailedAsync` for broad filters, and the SDK
   surfaces the error as `TcgDexApiException` rather than pretending to a partial result.

### Where GraphQL wins

Field selection (smaller payloads) and nested fetch in a single round trip:

```graphql
{ set(id: "swsh3") { name cardCount { official total } cards { id name } } }
```

REST needs one call per card for the same data.

**Consequence for SDK design:** REST must remain the primary transport — GraphQL cannot serve 17
of the 18 languages, any range filter, or any pricing data. GraphQL is an opt-in optimization for
projection and nested fetch.

---

## 9. Fields that do NOT exist

Listed because they are plausible-sounding and easy to assume into a model. None of them appear in
any live response or in the GraphQL schema:

`cost` · `attack` · `defense` · `artistId` · `artistName` · `artistIdStr` · `effectStr` ·
`effectText` · `size` (on a card) · `isInDeck` · `type` (the card's type is `category`) · `tagline`

Likewise, `Spell`, `Monster` and `Artifact` are not Pokémon TCG categories — the only values are
`Pokemon`, `Trainer`, `Energy`.

---

## 10. Fixture cards for tests

| Id | Why |
|---|---|
| `swsh3-136` | Pokémon, Stage1, full field coverage: attacks, weaknesses, retreat, legal, pricing |
| `sv03.5-155` | Trainer with `trainerType: Tool` and `effect` |
| `base1-102` | Energy card — has `energyType`, and also `stage` |
| `swsh1-1` | `attacks[].damage` is the **string** `"50+"` |
| `xy1-1` | `attacks[].damage` is an **int**; also `suffix: "EX"` and `holofoil` pricing |
| `exu-!` | `localId` is `"!"` and `image` is **absent** |
| `exu-%3F` | URL-encoded id |
| `sv03.5-001` | Multiple `variants_detailed` entries with differing pricing |
| `A4-139` | Has `boosters` — proves it is an object array, not a string |
| `base1-1` | Has `abilities` (`type: "Pokemon Power"`) |
| `pl1-1` | Has `resistances` with a signed string value (`"-20"`) and `Poke-BODY` ability |
| `base1-4` | `tcgplayer` pricing keyed `holofoil` rather than `normal` |

---

## 11. Sources

- Live API, verified 2026-08-05 (authoritative for this document)
- https://tcgdex.dev/rest — endpoint overview
- https://tcgdex.dev/rest/filtering-sorting-pagination — operator list
- https://tcgdex.dev/sdks — official SDK list (Java, JavaScript, Kotlin, PHP, TypeScript, Python;
  **no C#/.NET SDK exists**)
