# Documentation

These files are both the repository's documentation and the source for the
published site at
**[pinkushin.github.io/TcgDex.CSharpSdk](https://pinkushin.github.io/TcgDex.CSharpSdk/)**.

| Document | What it is for |
|---|---|
| [`index.md`](index.md) | Site landing page. Not shown when browsing GitHub. |
| [`getting-started.md`](getting-started.md) | Install, configure, first calls, error handling. |
| [`querying.md`](querying.md) | Every filter operator and the query string it produces. |
| [`api-info.md`](api-info.md) | **The specification.** The TCGdex v2 API, verified field by field against live responses. If the SDK and this disagree, one of them is a bug. |
| [`architecture.md`](architecture.md) | How the SDK is put together and why. Start here to extend it. |
| [`learnings.md`](learnings.md) | Non-obvious behaviour discovered while building it, each with its evidence. |
| [`coverage.md`](coverage.md) | Coverage goal, how to measure it correctly, and where it stands. |
| [`publishing.md`](publishing.md) | First-time walkthrough for publishing to NuGet. |
| [`roadmap.md`](roadmap.md) | What is left before 1.0, and what is out of scope. |

`api/` holds the generated API reference. Only `api/index.md` is committed —
everything else there is produced from XML doc comments at build time, because
committing generated reference material guarantees it drifts from the code.

---

## Building the site locally

```bash
dotnet tool restore
dotnet docfx metadata docfx.json
dotnet docfx build docfx.json --serve
```

Then open <http://localhost:8080>. Drop `--serve` to build into `_site/`
without a server.

The API reference is generated from the SDK's XML doc comments, so the project
must compile first. CI builds with `--warningsAsErrors`, which makes a broken
cross-reference or an undocumented public member fail the build rather than
quietly degrade the published reference.

---

## Two things to know before changing anything

**`api-info.md` is authoritative, not the code.** An earlier version of this SDK
documented roughly a dozen card fields that do not exist in the API, because the
docs were written to match the code instead of the service. Verify against a
live response, not against the model.

**Every collection on a model needs a null-coalescing backing field.**
`System.Text.Json`'s source generator discards property initializers, so `= []`
deserializes to `null`. A plain initializer compiles, passes review, and throws
in production. See [`learnings.md`](learnings.md).
