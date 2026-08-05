# Documentation

| Document | What it is for |
|---|---|
| [`api-info.md`](api-info.md) | **The specification.** The TCGdex v2 API, verified field by field against live responses. If the SDK and this disagree, one of them is a bug. |
| [`architecture.md`](architecture.md) | How the SDK is put together and why each piece is shaped that way. Start here to extend it. |
| [`learnings.md`](learnings.md) | Non-obvious behaviour discovered while building it, each with the evidence. Read before debugging something strange. |
| [`coverage.md`](coverage.md) | Coverage goal, how to measure it correctly, and the exact remaining gap. |
| [`publishing.md`](publishing.md) | First-time walkthrough for publishing to NuGet. Nothing here has been done yet. |
| [`roadmap.md`](roadmap.md) | What is left before 1.0, and what is deliberately out of scope. |

The user-facing guide is the [root README](../README.md).

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
