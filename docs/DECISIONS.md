# Decisions

Choices that shaped this SDK, with **what was decided and why**, in the words of
whoever decided it where that was recorded.

The code shows the outcome. It cannot show the alternative that was rejected, or
the reason. Anyone asking "why is it like this?" months from now — including the
people who built it — has only this file to answer from.

Newest first. A **reversal** records both positions and what changed between
them; those are the entries most worth keeping and the easiest to quietly
overwrite.

---

## 1. Client-side failover: built, then removed — REVERSAL

**Decided:** 2026-09-06. **Reverses:** the 0.4.0 decision to build it, taken
2026-09-01.

### The original position

Failover was built at the owner's request after asking whether the SDK could fall
back to a working server if the main one went down. It shipped in 0.4.0 as
`UseFailover`, rotating to another endpoint on a refused connection,
`502`/`503`/`504`, or an attempt past a per-attempt timeout.

It was greenlit by Thomas at TCGdex, who asked at the time that it be **easy to
remove** once the API handled this server-side. That condition was written into
the design and is why the removal was a clean deletion rather than an unpicking.

The owner's reason for expecting part of it to outlive a server-side
implementation, recorded then and still correct as far as it goes:

> a server-side implementation can only rotate among nodes TCGdex runs, so an
> unofficial mirror or a self-hosted server is reachable only this way

### What changed

TCGdex deployed new infrastructure in early September 2026. Two things followed,
both confirmed by Thomas on 2026-09-06:

> i belive those routes are now no longer open as the new infra got deployed on na

> the main route should now route if any of the nodes are down

Avior described the backend topology that makes the second statement true:

> na1 and na2 are now both behind a single load balancer that automatically
> handle disconnection from the other servers that should be better for you

The owner's reading of that, and the right one — the assistant had first taken
it to mean the `na1`/`na2` *names* now sat behind a load balancer:

> i think he meant it as the servers that ran for NA had a load balancer now and
> the NA enpoints were not there

So the NA *machines* were pooled behind one balancer, and the NA *endpoints*
were retired — not kept and re-pointed. That matches what was measured after the
balancer went in: `api.na1` still answered `404`, `api.na2` still failed TLS,
and `api.tcgdex.net` served normally. The hardware is healthy and now fails over
by itself; the prefixed names are simply gone, which is the fact the enum
depended on.

The timing was expected rather than a surprise. From the same conversation:

> i was ready for this, you told me you guys were going to do the fallback last
> week, i just didnt expect it to be so fast lol

Ryan's reply — *"Needed doing quickly really"* — and the owner's note that the
maintainers are volunteers he was not going to rush are worth keeping for the
next time this SDK covers a gap upstream intends to close: the gap closed a week
after it was announced, not a quarter later.

Measured the same day, before that confirmation arrived: `api.na1.tcgdex.net`
resolved and its certificate covered the name, but the new front end had no route
for it and answered `404 page not found` as `text/plain`.
`api.na2.tcgdex.net` presented `CN=TRAEFIK DEFAULT CERT` and failed TLS outright.
`api.tcgdex.net` served correctly from the same IP — the difference was the
`Host` header, not the server.

So the condition Thomas set in 0.4.0 had been met, and two of the six mirror
enum members could no longer work at all.

### The decision

Remove client-side failover entirely, and the mirror enum with it. Keep the
ability to point the SDK at a custom server.

**All six enum members went, though only two were provably dead.** Measured at
the time of removal, `api.eu1`/`eu2`/`eu3` and `api.as1` still served; only the
NA endpoints had been retired, which matches Thomas naming NA specifically and
Ryan's *"Needed doing quickly really"*. The owner's expectation — an expectation
from the shape of the rollout, not something upstream stated — was that the same
treatment reaches the other regions:

> probably did that for every area, and theres probably a server on demand thing
> going if he's really smart about it and can afford it

The three EU names already resolved to a single address, which is consistent
with that region being pooled behind one balancer too. Whether anything scales
on demand is not observable from a client and is not claimed here. Either way the
decision does not rest on it: upstream recommends `api.tcgdex.net` for everyone,
so removing only the two confirmed-dead members would mean doing this again when
the rest follow.

The owner, on why the dead enum members were not simply left in place or marked
obsolete:

> i know it technically can be left in but itll never fire, and id rather not
> tempt people to use something they shouldnt use anymore

And on why no deprecation period was needed:

> no one is using the sdk yet im pretty sure

### What survived, and why

`BaseAddress` and `GraphQlEndpoint` stay. The owner's requirement:

> i want to keep the ability to point the sdk to a custom server though

Thomas named the same exception unprompted, which is as close to an upstream
endorsement of this split as it gets:

> yeah you shouldnt need to manually do it now. unless you wanna define like
> local apis or somthing

And the shape was stated to the maintainers before it was built, so the SDK's
behaviour and what upstream was told match:

> im removing the prefix enums completely, and just leaving the api.tcgdex.net
> endpoint, while letting the host be overridden for custom endpoints

This is the durable half of the original reasoning. A server-side implementation
can only route among nodes TCGdex runs, so an unofficial mirror or a self-hosted
instance is reachable this way and no other. What changed is that *rotation
between endpoints* stopped being the SDK's job — not that *choosing* an endpoint
did.

### What it cost, and what it bought

An intermediate design was built and abandoned the same day: falling back to the
official host automatically when a mirror stopped working, with the official host
held as a reserved final attempt. It was complete and passing when the upstream
confirmation arrived and made it pointless. It is preserved unmerged on
`feat/fall-back-to-official-host` rather than deleted.

Removing failover also removed the `404`-discrimination fix that had been merged
days earlier — a real defect, fixed correctly, in code that no longer exists. The
reasoning is kept in [`learnings.md`](learnings.md) because it is about reading
status codes rather than about failover.

One thing the removal exposed and improved: the trailing-slash guard existed only
on failover endpoints. `BaseAddress` had the identical hazard, had never been
checked, and became the only way to reach a custom server. That validation was
added in the same change.

**The general lesson**, which is why this entry exists rather than just a
changelog line: a feature built to cover a gap in someone else's system has a
built-in expiry date, and the time to agree how it comes out is while it is going
in. Thomas asking for it to be easy to remove is the only reason this was a
deletion instead of an excavation.

---

## 2. `dotnet test` on the multi-target Tests project must pin `--framework`

Added a CI job (`verify-test-coverage`) that asserts the union of test names across all lanes
equals a fixed count. It caught a pre-existing flake: `build-and-test` and `macos-test` ran
`dotnet test TcgDex.CSharpSdk.Tests.csproj` without `--framework`, against a project multi-targeting
`net10.0;net8.0;netstandard2.0`. Which TFM's build `dotnet test` picks when none is specified is not
guaranteed stable — two macOS runs on the identical commit produced 518 and 519 distinct test names,
differing by exactly `ThePublicSurface_MatchesTheApprovedBaseline`, a test gated by TFM.

Both jobs now pin `--framework net10.0` explicitly. `framework-test` already pinned `net472` for the
same reason — this closes the gap on the other two.

With both pinned, the two lanes report 519 each and agree run to run. The expected union is **532**:
519 unit (`net10.0`, same names in `unit-tests.trx` and `macos-tests.trx`) + 11 offline integration
+ 2 that exist only under `net472` in `framework-tests.trx`. Re-derive it that way if a lane changes;
do not adjust the constant to whatever CI last printed.

The general lesson: an exact-count coverage assertion is only as trustworthy as the determinism of
what it counts. It surfaced this bug rather than causing it — the flake predates the coverage job and
had been running unnoticed since nothing compared lane counts before.

---

## 3. Unit tests are hermetic — never reach the network

A unit test can be hermetic only while the code is correct. `Create_DisposesItsOwnHttpClient`
tried to verify disposal by awaiting a real request and expecting `ObjectDisposedException`. Under
Stryker, every mutant that defeated the disposal dialled the live API. On a night the API was
down, each mutant hit the 30-second timeout: the run ballooned from ~20 minutes to 2h38m and
silently starved a neighbouring job on the shared measurement box.

Assert observable state directly, never behaviour that depends on external infrastructure being
reachable. See [`learnings.md`](learnings.md) for the full measurement.

---

## 4. Query builder never calls `Expression.Compile()`

Runtime code generation is not AOT-safe and breaks Unity and Native AOT consumers. Expression
trees are walked and translated to query parameters, never compiled to delegates.

---

## 5. Collection initializers require coalescing backing fields

The System.Text.Json source generator discards collection initializers. A property declared as
`= []` deserializes to `null` when the field is omitted in the JSON. Use a coalescing backing
field to guarantee non-null.

---

## 6. Public API surface is pinned by `PublicApi.approved.cs`

`TcgDex.CSharpSdk.Tests/PublicApi.approved.cs` (PublicApiGenerator) pins the public surface.
An intentional change requires regenerating that baseline. **Regenerate with LF line endings
only** — the CRLF variant fails the comparison on the line endings, not on the surface itself.

---

## 7. Strict analyzers are scoped to the library only

`AnalysisMode=All` + SonarAnalyzer on test code is near-total noise: S2699 on every CsCheck
property, CA2000 on every undisposed test `HttpClient`. The signal-to-noise ratio is unusable.
Analyzers are enabled **only on `TcgDex.CSharpSdk`, not on test or benchmark projects.**

---

## 8. Native AOT publish needs VS Installer directory on `PATH`

The native link step fails with a misleading error if the C++ toolchain is not reachable. VS
Installer puts those tools in a subdirectory that is not on `PATH` by default. Add it before
running Native AOT publish, or the build will fail with a cryptic message about `ml64.exe` or
similar.

---

## 9. `main` is protected

Direct pushes to `main` are disabled. All work goes through a pull request. Auto-merge is off, so
a merge is an explicit step taken after CI is green. Releases are git-tagged and published by
workflow, not by hand.

This arrangement ensures every commit to the main branch has been reviewed and CI-validated.

---

## 10. Roslynator.Analyzers and Meziantou.Analyzer added alongside SonarAnalyzer

Requested by the owner directly ("i use it in PBJ, and want it here too"), and traced to
[`PinKushin/ANALYZER-COVERAGE-LOG.md`](https://github.com/PinKushin/PinKushin/blob/main/ANALYZER-COVERAGE-LOG.md)
(entry 2026-09-08), which surveyed the house's C# repos and identified Meziantou as a genuine
fourth analyzer rather than a duplicate of Sonar/Roslynator — its standout categories are async
correctness (`ConfigureAwait`, and `using` on a `Task<IDisposable>` disposing the Task itself
rather than the awaited result) and culture-sensitive string comparison, which neither existing
tool focuses on as heavily. This SDK is an async HTTP client throughout, exactly the shape those
rules target.

Adding both surfaced 49 unique findings across four rule IDs, resolved as follows rather than
suppressed wholesale:

- **MA0048** ("file name must match type name"), 33 sites — disabled repo-wide via
  `.editorconfig`. This codebase deliberately groups small, tightly related types in one file
  (`Resources.cs` holds six resource classes, `GraphQlMessages.cs` a `JsonContext` plus the
  message types it serializes) so a reader following one concept finds every type for it in one
  place. Splitting them would be a purely mechanical, purely cosmetic change against how the
  codebase is organized on purpose — not a defect the rule caught.
- **MA0015** ("is not a valid parameter name"), 6 sites, all in `TcgDexOptions.Validate()` —
  the identical pattern Sonar's `S3928` was already suppressed for at the same call sites:
  `Validate()` is parameterless by design and reports the offending *property* name via
  `nameof` for an actionable `ArgumentException.ParamName`, which neither analyzer's parameter-
  matching heuristic recognises as valid. Folded into the existing suppressed span rather than a
  second pragma repeating the same reasoning.
- **MA0051** ("method is too long"), 7 sites, longest 107 lines — threshold raised from 60 to
  130 via `.editorconfig`, not disabled. This codebase documents *why* alongside *what*, so a
  method that exceeds 60 lines usually does so because of the comments explaining a non-obvious
  decision, not because the control flow does too much. The 107-line outlier is
  `BoundedContent.ReadAsBytesAsync` — the decompression-bomb guard — where splitting it for a
  line count would fragment security-relevant logic across methods with no independent
  correctness meaning, in a codebase where the "verify by manipulation" testing discipline
  demands re-verifying anything touched. 130 leaves headroom without disabling the rule outright.
- **RCS1139** ("add summary element"), 3 sites — fixed for real rather than suppressed: three
  methods had a `<remarks>` block with no `<summary>`, which is a genuine documentation gap
  (`<summary>` is what an IDE tooltip shows; `<remarks>` is secondary) rather than a false
  positive.

Full local gate green after: build (0 warnings across all three TFMs), unit tests (519/518/514,
unchanged — no test source touched), coverage (99.82% line / 96.29% branch, both above gate),
docs (0 warnings).

---

## 11. `thirdParty` modelled against the real live shape, not the pre-deployment guess

Waited deliberately, per `thirdparty-pending-upstream` in memory: TCGdex merged
[`cards-database#2184`](https://github.com/tcgdex/cards-database/pull/2184) on 2026-08-27,
removing the `deepOmit` that stripped `thirdParty` from card responses, but it had not
redeployed to `api.tcgdex.net` yet. Modelling against a merged-but-undeployed PR is exactly the
mistake the SDK rewrite existed to undo — the discarded SDK shipped ~10 fields the API never
actually served. The daily `live-api.yml` fixture-drift check was left as the trigger rather than
guessing a redeploy date, and it went red on 2026-09-07.

**The real shape differed from what the memory anticipated in two ways**, both found by fetching
live cards rather than assumed from the PR description:

- **A third marketplace.** The memory named `tcgplayer`/`cardmarket`; the live payload also
  carries `cardtrader`, observed only *intermittently* — present on one fetch of a card and gone
  on the next fetch of the same card. Modelled as `int?` alongside the other two rather than
  assumed absent because a given fixture snapshot happened not to catch it.
- **Two independent placements, not one.** `thirdParty` sits at the card root **or** inside each
  `variants_detailed[]` entry depending on the card — `swsh3-136` (two distinctly-priced
  printings) carries it per-variant with no root-level field; `swsh1-1` (one undifferentiated
  `"generated"` printing) carries it at the root instead, with its single variant carrying none.
  This exactly mirrors how `Pricing` was already placed at both levels, which is what made the
  right shape recognisable rather than another guess: `ThirdParty` follows the same pattern as
  the type it sits beside, not a new one invented for this field.

Landed alongside an unrelated drift in the same fixture-refresh: `Serie.LastSet` (typed
`SetBrief`, which already had `Logo`/`Symbol`) had a recorded fixture where the referenced set
predated those fields being populated for it — a data-completeness gap in the recording, not a
schema gap, so no model change, only `scripts/Update-Fixtures.ps1`.

New tests written first (red on a compile pass against fixtures with no `thirdParty` yet, since
the model didn't exist), confirmed red for the right reason, then the model added and fixtures
refreshed per `Update-Fixtures.ps1`'s own instruction to update the SDK before refreshing — a
refresh first would have made the drift check pass while hiding the change it was reporting.

**Addendum, 2026-09-11 afternoon — the deploy itself may not have been intentional.** Asked in the
TCGdex Discord whether the 2026-09-07 change was announced anywhere. Ryan (FalconChipp) and
Thomas, the maintainers, had no record of it — Thomas: *"that would be impressive if it was, i
dont even know that"*. After the owner described the shape, Thomas: **"well this might have been
an unintentional release when the infra switched over."**

Doesn't change the model or the decision to wait for the live shape — that reasoning holds
regardless of whether the deploy was deliberate.

"Unintentional" and "unstable" are separate claims, though, and only the first is established
here. **Owner's read, and the more relevant one going forward:** *"i dont think it will be
reverted, the fact it rolled out and worked for 5 days without complaints, or even some people
noticing, means it should be ok to run publicly."* Five days of live operation with no incident,
no rollback and no visible complaint is real evidence toward the field staying, not just an
absence of evidence against it. The practical risk is low; "unplanned at deploy time" describes
how it arrived, not how long it is expected to last.

**Sharper still, from the owner:** *"this was a planned update, and pretty much done that im
aware, so it just needed rolled out, it doing it accidently during the server migration is just a
happy accident i guess."* "Unintentional" describes the TRIGGER, not the FEATURE — the field was
already finished, planned work on TCGdex's side; what was accidental is only that the infra
migration flipped it on as a side effect instead of a formal announcement doing it. That is a
stronger stability signal than an experimental field slipping out early: this is the real,
intended release, just reached production by the side door rather than the front one.

No SDK change is warranted either way — every `ThirdParty` property is nullable by design, so if
it is ever pulled back the field just deserializes to `null`, a state already handled. Recorded so
a future "why did `thirdParty` disappear" investigation starts from this conversation rather than
re-diagnosing it as a bug, whichever way it turns out.

---

## 12. C3's per-lane floors get raised whenever tests are added — REVERSAL

Reverses the "no need to bump a floor when tests are added" framing decision #10's neighbouring
PR (#56, C3's union-to-per-lane-floors change) shipped with.

### The original position

`scripts/Assert-TestCount.ps1`'s header and `ci.yml`'s comment both said a floor's advantage over
an exact count is that it "does not need an edit every time a test is added" — motivated by this
repo's old union-total check having been hand-bumped twice in one week (`9499f9e`, `c879724`) for
exactly that kind of churn. When three new tests (this PR, thirdParty) pushed the real count from
519/519/514 to 522/522/517, the floors were left at 519/519/514 on the reasoning that 522 ≥ 519
already passes, so nothing needed to change.

### The correction

Owner: **"no floors get bumped when you add tests too or they are worthlessx."**

A floor set once and never raised drifts further from the true count with every addition, and
the gap it leaves IS the blind spot the whole check exists to close. If the true count grows to
600 while the floor stays at 519, up to 81 tests could silently stop running and the check would
still pass — silently, which is exactly the failure mode C3 exists to make loud. "Does not need
an edit" was true in the narrow sense that CI would not turn red, and wrong in the sense that
mattered: the check's actual sensitivity had quietly degraded.

### What actually follows

The floor's real exemption from exact-count maintenance is narrower than the original framing
claimed: it need not be bumped for a transient dip that resolves on its own (a flaky skip, a
timing-dependent count). Anything that changes the suite **on purpose** — tests added or removed
— changes the floor in the same commit. Raised here: 519/11/519/514 → 522/11/522/517, in the
same PR as the tests that moved the count. `Assert-TestCount.ps1`'s header and the `ci.yml`
comment both corrected to say this outright, and `PinKushin/C3-COVERAGE-LOG.md` carries the same
correction for whichever repo copies this pattern next.
