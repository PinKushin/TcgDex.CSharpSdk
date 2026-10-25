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
a merge is an explicit step taken after CI is green. *(Auto-merge superseded 2026-10-15 by #16:
now on, and still gated on every required check.)* Releases are git-tagged and published by
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

---

## 13. A `User-Agent` header, requested by TCGdex directly

Before this, the SDK sent none at all — `HttpClient` adds no default the way a browser or `curl`
does, so every request from every consumer of this library was, on the wire, indistinguishable
from any other .NET client hitting the API.

Raised in TCGdex's Discord while debugging an unrelated latency report. Avior: *"Might be better
to set a standard one with the version of the SDK them[selves]."* Asked why it mattered beyond
courtesy, since the traffic is already read-only and low-risk: *"i store logs for stats and
debugging purposes and to respect GDPR i don't store ip addresses, so the only way i can
'identify' [a client] is by using the user agent."* With no header at all, this SDK's traffic was
unidentifiable by design — not a minor gap, the only signal TCGdex's own privacy stance leaves
available.

Format agreed in the same conversation and matched exactly: `TcgDex.CSharpSdk/{version}`.

**Applied per `HttpRequestMessage`, not via `HttpClient.DefaultRequestHeaders`.** The SDK is used
two ways — owning its own `HttpClient` (`TcgDexClient.Create`, `AddTcgDex`) or given one the
caller built and shares with the rest of their application. Setting a header on the client object
itself would reach into a caller's shared instance, the same reasoning `RequestBudget` already
applies to the request timeout. Setting it per-request works identically either way and touches
nothing the SDK doesn't own.

**Version read from `AssemblyName.Version`, not `AssemblyInformationalVersionAttribute`.** Both
report "0.6.0" today — this repository sets no build-metadata suffix — but `AssemblyName.Version`
is assembly identity the runtime always preserves, while a custom attribute is metadata a trimmer
can remove when nothing else references it. Chosen so the header needs no Native AOT verification
of its own beyond what `TcgDex.CSharpSdk.AotSmokeTest` already provides by publishing and running
a real binary.

Verified live as well as in the test suite: sent one request with `User-Agent: pinkushin-debug/1.0`
directly against `api.tcgdex.net` so Avior could confirm the header actually reaches his logs
before relying on it.

## 14. CI stays on `ubuntu-latest` through the Ubuntu 26 migration — not pinned

GitHub annotated every Linux job on 2026-09-21: `ubuntu-latest` moves to Ubuntu 26 from
2026-10-19. Pinning `ubuntu-24.04` was offered and declined. The owner, 2026-09-23: *"let it
switch, i want my shit to stay updated and keep everything it uses updated, thats what makes a good
program imo."*

So the runner image follows GitHub's default, the same way Dependabot keeps packages and actions
current. If the switch breaks a job, fix the job for Ubuntu 26. Do not pin back.

## 15. Mutation testing moved from the Oracle box to GitHub Actions

By 2026-10-15 `mutation-box` had been unreachable for some time. The owner, 2026-10-15: *"the oracle
box is down, been down"*, *"we are using github now"*, and *"using github counts for all the
projects, the oracle box got shutdown, and not because it wasnt being used, but because some stupid
trial ended, and their customer service sucks so they care not getting my CC, trying to get money
back if they fuck up and charge me for soemthing will be impossible."*

So the move is permanent and covers every project, not only this one. Oracle ended a trial, and the
owner will not hand Oracle a card to keep the box, because a wrong charge would be unrecoverable.
Measurement work now runs on GitHub Actions.

`.github/workflows/mutation.yml` runs Stryker daily and on dispatch. It publishes the HTML report as
an artifact and the score in the job summary. A score below `stryker-config.json`'s `break`
threshold fails the run. `build/run-measurements.sh` still holds the box's `stryker` mode, and it is
dead code for as long as the box is down.

## 16. Repository auto-merge enabled; `main` stays protected

Asked whether to turn on the repository's **Allow auto-merge** setting, which was off. The owner,
2026-10-15: *"i mean sure as long as main is still protected."*

Enabled. Branch protection is unchanged: 6 required checks, branch must be up to date, no force
pushes, no deletions. An auto-merge PR merges only after every required check passes.

**Dependabot PRs: reviewed by risk — REVERSAL, same day.**

- **First position:** review every Dependabot PR before merge. The owner: *"i want to review
  dependabot wwith you, before a merge, it can fuck up sometimes i think."*
- **What changed it:** the owner, refining the worry: *"if they dont break anything, it should be
  fine, i just worry if we have a weak test it will bring a bug in."* Only packages that ship inside
  the SDK can bring a bug into it. Test tooling can quietly weaken tests, and the test count plus the
  daily mutation score catch that. Analyzers and CI actions never ship. The owner: *"yea thats a
  decision, go with that."*

The rule now:

| The bump touches | Who merges |
|---|---|
| A runtime dependency of the shipped library (`System.Text.Json`, `Microsoft.Extensions.*`, `Microsoft.Bcl.*`, …) | **Reviewed with the owner** before merge |
| Test tooling (NUnit, the test adapter, coverlet, CsCheck, Test.Sdk) | Merged without the owner, once CI is green and the test totals per TFM have not dropped |
| Analyzers (`PrivateAssets=all`) and GitHub Actions | Merged without the owner, once CI is green with no new annotations |

Classify by package, not by the Dependabot group name. The `runtime` group has carried analyzers.
Auto-merge is never enabled on Dependabot PRs.

## 17. Dependencies track the latest patch of each target's line — not a minimum-version policy — CORRECTION

`Directory.Packages.props` and `dependabot.yml` used to say runtime dependencies were pinned to the
*lowest* version that satisfies each target. That was never the owner's intent. The owner,
2026-10-15: *"I never meant for anything to be minimum version, I wanted true usage and backwards
compatibility for Unity and people who might not be on windows 10 or 11, but i never wanted the
modern .net to get behind in updates, because those updates are security and features and
performance."* And: *"latest patches, because those are the best performing and secure normally,
although if the lates is slower or less secure then of course we dont use it."*

The rule now:

- Each target framework stays on its own release line. net10.0 uses Microsoft.Extensions 10.x.
  net8.0 and netstandard2.0 use 8.0.x, because 10.x there would drag ASP.NET Core 8, .NET Framework
  and Unity consumers onto the 10.x graph. Moving a line stays a human decision.
- Within its line, every dependency tracks the **newest patch**. Not doing so is a finding.
- **Exception:** a release measured slower, or carrying a known vulnerability or regression, is
  skipped. Say why next to the pin.
- netstandard2.0 exists for compatibility, not to hold versions back.

The same review found a test gap. The 10.x condition was `!= net8.0 and != netstandard2.0`, which
also matched the net472 test leg. So the netstandard2.0 asset was only ever tested against 10.x
dependencies, never the 8.0.x ones it ships with. The condition is now `== net10.0`.
`DependencyLineTests` asserts the resolved major version on every leg, and it was seen failing on
net472 before the fix.

## 18. `TcgDexCachingHandler` throws `TcgDexApiException`, not `HttpRequestException`

`TcgDexCachingHandler` is a public `DelegatingHandler`. When a body is too large, or its connection
fails mid-read, it throws `TcgDexApiException` from inside the `HttpClient` pipeline. A review in
0.6.2 asked whether `HttpRequestException` would be more conventional for a handler.

The owner, 2026-10-15, asked which of the two was more useful, and answered: *"yea keep it, thats a
decision"*. The reasoning came from the assistant, and the owner accepted it:

- Retry policies such as `AddStandardResilienceHandler` retry `HttpRequestException`. A body that
  is too large will be too large again, so retrying only downloads it again, up to the limit, on
  every attempt.
- Callers going through `TcgDexClient` see `TcgDexApiException` either way, because the transport
  wraps `HttpRequestException` itself. Only a caller using the handler directly can tell the
  difference.

The behaviour is documented in the handler's XML remarks.

## 19. A soft bar for 1.0

The first cross-model review (Fable over Opus-written code, 2026-10-15) found one real bug and
several leaks in the exception contract, in the three files with the lowest mutation scores. On 1.0
those fixes would have pushed toward a major version. On 0.x they are a patch. The owner: *"im
actually glad this wasnt in a 1.0 now"*, then *"add that 1.0 bar to the decisions doc, at least a
soft bar, we do need to raise those scores if they can be."*

**Before tagging 1.0** (soft: a missed item needs a stated reason, not a silent skip):

1. **A full cross-model review comes back clean.** The whole shipped library must be reviewed by a
   model other than the one that wrote it. A review of a single diff does not count.
2. **The weakest files' mutation scores are raised toward the rest of the library**, as far as they
   can be. At 2026-09-23 those were `Caching/TcgDexCachingHandler.cs` 67%, `Http/BoundedContent.cs`
   65% and `TcgDexTransport.cs` 77%, against 88% overall. "If they can be": a surviving mutant that is
   equivalent, or untestable for a stated reason, is acceptable once written down.
3. **CI and the local gate measure the same thing.** Met on 2026-10-15: CI now gates coverage across
   all three target frameworks.

## 20. `TcgDexJsonContext` becomes internal before 1.0

The Fable full-library review (2026-10-17) flagged that the source-generated `TcgDexJsonContext` is
public. That makes every generated `JsonTypeInfo<T>` property part of the pinned public API, so after
1.0 renaming or removing a model would break twice. Offered: make it internal, or keep it public on
purpose. The owner, 2026-10-17: *"internal"*. No further reasoning given.

Done in 0.7.0. `PublicApi.approved.cs` shrinks accordingly, and tests reach the context through
`InternalsVisibleTo` if they need it.

## 21. `DeserializePricing = false` also skips per-variant pricing — CORRECTION

`DeserializePricing` was documented as skipping `Card.Pricing`, and the code did exactly that. But the
pricing inside each `variants_detailed[]` entry was still parsed, so an application that turned
pricing off to save the work still paid for it on every printing. The second mutation round
(2026-10-17) pinned the old behaviour with a test and asked which was intended. The owner:
*"fix that deserialized bug too"*. So it is a bug: the option means no pricing is parsed anywhere on
a card.

Done in 0.7.0. The old behaviour had not reached `main` as a pinning test, so the test written is
`WhenTurnedOff_PerVariantPricingIsNullToo`. The option's docs and CHANGELOG say so.

## 22. .NET conventions everywhere; `ReleaseDate` becomes `DateTime?`

The owner, 2026-10-17, approving `ReleaseDate` changing from `string?` to a date type: *"i want .net
conventions everywhere, thats the point int he sdk really"*. The SDK exists to give .NET developers
idiomatic types, so where the JSON's shape and .NET convention differ, convention wins. Before 1.0
that applies even when the change is breaking.

`ReleaseDate` is `DateTime?` (date only, kind `Unspecified`) on every target. The modern convention
for a calendar date is `DateOnly`, but netstandard2.0 (Unity, .NET Framework) lacks it, and public
types that differ by target framework are ruled out. A string had been kept on the assumption of
partial dates. That assumption was wrong: on 2026-10-17 all 606 sets (en 220, fr 202, ja 184) had a
full `YYYY-MM-DD`. An absent or malformed value reads as null rather than failing the set.

## 23. 1.0 review notes, decided

The Fable full-library review (2026-10-17) raised notes that would be breaking or awkward to change
after 1.0. Each was put to the owner, who said *"do 8, and 9, fix 12 too that seems like a bug, and
support 11, 1-6 are obviously fixed"*. All are done in 0.7.0:

- **#8, failure kind:** `TcgDexApiException` gains `Kind` (a `TcgDexErrorKind`) and `RequestUri`, so
  callers branch on the failure instead of parsing the message. A language 404 stays `HttpStatus`
  with `IsLanguageError` set, so that fact lives in one place. `GraphQlError` exists because GraphQL
  errors arrive as `200`.
- **#9, options:** options are read once, when the client is created, on REST and GraphQL alike.
- **#11, value expressions:** `Where` evaluates indexers, array elements, parameterless instance
  methods and casts on the value side by interpretation, never `Expression.Compile()` (#4).
- **#12, handler rotation:** a bug, as the owner said. The caching handler's coalescing and counters
  live in a singleton (`TcgDexCacheStatistics`) and survive `IHttpClientFactory` rebuilding handlers.
- **Also decided in the same pass:** a second `AddTcgDex` throws rather than silently keeping the
  first options. `#13`, a server page-size cap, was measured, not assumed: there is none (5000
  returned 5000).

## 24. Breaking changes on 0.x bump the minor version

The release prepared as 0.6.4 contained breaking changes (`ReleaseDate` became `DateTime?`,
`TcgDexJsonContext` became internal, invalid options moved from `ArgumentException` at registration
to `OptionsValidationException` at start). It was renamed to 0.7.0 before it shipped. The owner,
2026-10-17: *"yep sounds good"*, agreeing with the proposal that a release with breaking changes
ships as 0.7.0 rather than 0.6.4, following semver practice for 0.x.

From now on, any breaking change on 0.x bumps the minor version. A patch release contains fixes
only.

## 25. The 1.0 naming pass: .NET conventions over the API's names, before 1.0

The owner, approving the 0.8.0 scope taken from the Fable .NET-convention audit: *"i want .net
conventions everywhere, thats the point in the sdk"* — the same principle as #22, applied to names.
Every public name follows the .NET Framework Design Guidelines, and the change ships before 1.0
because after it each rename is a major version. Under #24 it ships as 0.8.0.

What that meant in practice:

- **No non-words or abbreviations.** `Serie` → `Series`, `Hp` → `HitPoints` (matching
  `GetHitPointsAsync`), `Avg7` → `Average7Days`, `IdProduct` → `ProductId`, `FirstEd` →
  `FirstEdition`, `Lang` → `Language`.
- **No type named after a keyword or a general concept.** `Set` → `CardSet`. The rest of the family
  moved with it (`CardSetBrief`, `CardSetAbbreviation`), so a brief still pairs with its full type
  the way `CardBrief` pairs with `Card`.
- **One casing per brand.** `TcgPlayer` everywhere, matching the `TcgPlayerPricing` type; `CardTrader`,
  the marketplace's own casing.
- **Methods are verbs.** Resource methods without one gained `Get` (`GetRaritiesAsync`).
- **Nothing public that no caller needs.** `QueryOperator` became internal; the status-only
  `TcgDexApiException` constructor, which produced `Kind = Unspecified`, was removed; the caching
  handler's counters collapsed into the one `TcgDexCacheStatistics`.

**JSON keys did not change.** Every renamed property carries `[JsonPropertyName]` with the API's
key, and tests pin the written keys, since case-insensitive reading means a round trip cannot catch
a missing mapping. The query builder now takes a property's field name from that attribute: before,
it camel-cased the .NET name, and `c.HitPoints > 100` would have become the unknown `hitPoints`.

Names the audit raised but that were left alone, because the case is not clear-cut, are listed in
the 0.8.0 report rather than changed on a guess.

## 26. `CachedResponse` has reference equality, not record equality

Decided while doing the naming pass (#25). The owner asked for the decision to be made and recorded
rather than giving one; the reasoning below is the implementer's, not the owner's.

`CachedResponse` was a `record`, so it had value equality, but record equality compares its
`byte[] Body` by reference. Two entries holding the same bytes therefore compared equal or not
depending on whether they happened to share an array, which is equality nobody can rely on. The
alternatives were:

- **Compare the bytes.** Correct, but every comparison walks the body (22 KB for a set), and the
  hash code would change if anyone wrote to the array, which breaks it as a dictionary key.
- **Plain class, reference equality.** Honest about what an entry is: a stored blob with a
  timestamp, not a value. Nothing in the SDK compares entries, and a custom backend (Redis, disk)
  builds a fresh one on every read anyway.

The second was taken. `CachedResponse` is a `sealed class`; it loses `with` and `==`, which the
SDK's own revalidation path used once and now replaces with an initializer.

## 27. Full image URLs are `Uri?`; the API's base URLs stay `string?`

Decided while doing the naming pass (#25). As with #26, the owner asked for the decision to be made
and recorded; the reasoning is the implementer's.

The .NET guideline (CA1055/CA1056) is that a URL is a `System.Uri`, not a string. The helpers that
build a fetchable address (`ImageUrl.Build`, `ImageUrl.BuildAsset`, `GetImageUrl`, `GetLogoUrl`,
`GetSymbolUrl`) now return `Uri?`, which a caller hands straight to `HttpClient` or an image control.

The model properties `Card.Image`, `Logo` and `Symbol` stay `string?`. They are not addresses: the
API sends a base without a file extension, and requesting it as-is returns 404. Typing them as
`Uri` would present something unfetchable as a URL, which is the mistake the helpers exist to
prevent. The helpers' `baseUrl` parameter is a string for the same reason.

A base that does not make an absolute http(s) URL (relative, or not a URL at all) produces `null`,
following the tolerance policy of `ReleaseDate` and the timestamps: a value from the API that
cannot be read becomes null rather than an exception. The scheme check also stops `/en/swsh/1`
parsing as a `file://` URI off Windows. An unknown `ImageQuality` or `ImageFormat` still throws,
because that is a caller's mistake, not the API's.

## 28. JSON converters stay internal

Decided while doing the naming pass (#25); the owner's scope was "internal unless a consumer
genuinely needs one", and the reasoning is the implementer's.

The SDK's converters (`FlexibleStringConverter`, `ReleaseDateConverter`, `TimestampConverter`,
`TcgPlayerPricingConverter`) are all internal, and stay so. They are attached to the models with
`[JsonConverter]`, so reflection-based `JsonSerializer` applies them without a consumer naming one.
Public, each would be pinned API with nothing to call. They do appear in `PublicApi.approved.cs`,
but only as the type argument of those attributes, which is metadata rather than callable surface.

The cost, already noted in the 0.7.0 CHANGELOG: a consumer's own source-generated
`JsonSerializerContext` cannot include the models, because the generated code would have to
construct an internal converter. Serializing models under Native AOT is therefore not supported.
That is the consequence of #20 as much as of this entry, and no consumer has asked for it. Two other
cases would justify a public converter: that one, or a consumer modelling their own type with the
same quirk (a string-or-number field, a tolerant date). Making a type public later is not breaking,
whereas making it internal after 1.0 would be, so internal is the default until someone asks.

## 29. The doubtful renames: the owner's picks

The naming pass (#25) left a list of names whose case was not clear-cut. On 2026-10-18 the owner
chose from that list in a multiple-choice question. No further reasoning was given for any pick or
rejection, and none is supplied here.

**Renamed** (each keeps its JSON key through `[JsonPropertyName]`):

- `Card.Retreat` / `CardFilter.Retreat` → `RetreatCost`
- `Card.VariantsDetailed` → `DetailedVariants`
- `Card.EvolveFrom` / `CardFilter.EvolveFrom` → `EvolvesFrom`
- `ISetResource` → `ICardSetResource`
- `GraphQl` → `GraphQL` in every public name (`TcgDexOptions.GraphQLEndpoint`,
  `TcgDexErrorKind.GraphQLError`), with internal names following
- `Card.Legal` / `CardSet.Legal` → `Legality`

**Left as they are:** the type of `TcgDexProblem.Status`, `WPromo`, `Attack.Cost`, `Holo` and
`CardQuery.Page`.

**Kept from the first round:** the three renames the implementer added beyond the audit —
`CardSetBrief`, `CardTrader`, and the plural `DexIds` / `Stamps`. `CardFilter.DexId` stays singular
because it filters by one id; the 0.8.0 migration table says so.

## 30. 0.8.0 ships as a normal release, not a 1.0 release candidate

The naming pass (#25) was the last item on the 1.0 bar (#19), so it could have shipped as
`1.0.0-rc.1`. The owner chose `0.8.0`: "it wont be served on nuget natively if we do a rc tag, so
everyone would be on old stuff, so im leaning towads the 0.8.0 for a little". NuGet hides
prerelease versions unless a consumer opts in, so an RC would have left every default install on
0.7.0.

1.0.0 follows on the same code once 0.8.0 has had some use. A published NuGet version cannot be
renamed, so that is a small version-bump PR (csproj `<Version>` plus a `[1.0.0]` CHANGELOG
section) and a new `v1.0.0` tag, not a retag of 0.8.0.

Same release: the GitHub Release now carries the published `.nupkg` and `.snupkg`, and its notes
and the Discord post link the NuGet page and give the install command. The owner had been linking
the GitHub release on Discord, which until now pointed only at source zips: "people havent
actually seen a real release lol i think".

## 31. Mutation runs skip the live-API tests

Every Mutation run, including the daily 05:17 UTC one, discovered 894 tests: the 723 unit tests
plus all 171 in `TcgDex.CSharpSdk.IntegrationTests`, because Stryker takes the solution
(`stryker-config.json` names `TcgDex.CSharpSdk.Tests` as the only test project, but the solution
brings the other one in). The live tests therefore called the real TCGdex API for every mutant
they covered, and were credited with kills: 180 in run 39065526941, which inflated that run's
per-file scores. The number of requests was not measured.

`"test-case-filter": "TestCategory!=Integration"` excludes exactly the live tests, which all carry
that category through `LiveApiFixture`, and keeps the hermetic `JsonShape` tests. A mutant only a
live test used to kill now survives, and shows up as a gap in the unit suite. That is the point:
per-push CI does not run the live tests either.

The owner asked for this one-line change to be split out of the mutation-score work and merged the
same day.

## 32. No file's mutation score stays below its best without being examined

After 0.8.0 the total was 92.45% against 92.57%, and I called that flat, within the ±2.7 noise.
The owner overruled it: "if things are lower than they were anywhere, it needs looked at". The
noise bound applies to the total, not to one file.

Per file, against each file's best score in any past run counting only unit-test kills (#31), six
files were lower: `TcgDexServiceCollectionExtensions` (89.1% from 100%), `ExpressionTranslator`
(84% from 92%), `TcgDexClient` (81.2% from 87.5%), `CardImage` (87.1% from 93.1%),
`MemoryTcgDexResponseCache` (87.1% from 91.3%) and `TcgDexTransport` (88.2% from 89.3%). Comparing
the survivors by mutator, replacement and source line, not by count, showed which were new code and
which had lost a test. Of the 33 survivors in those files, 21 now have a killing test, each checked
by applying the mutant by hand, and 12 are written off in `docs/coverage.md` with a reason. One
code comment that called a mutant equivalent was wrong: removing that early return makes a miss
read the clock and `Remove` a key that was never stored.

The first run after those tests found five more survivors in `TcgDexTransport` (four constructor
statements) and `TcgDexJsonContracts` (one guard). A run had "killed" them only through
`PublicApiTests.ThePublicSurface_MatchesTheApprovedBaseline`, which cannot test a constructor guard,
so those were accidental kills. Each now has a test that fails against exactly its mutant.
`MemoryTcgDexResponseCache` still reads 90.3% against a best of 91.3%, but only because it has fewer
mutants than it did (31, from 46): its three survivors are the written-off equivalents.

The survivors in files that were not below their best were read as well, because the owner asked
for everything that "might be able to be killed": fourteen more got a killing test and four are
written off in `docs/coverage.md` (fourth round). Among them, one comment that said a survivor "only
shows up as a lower hit rate, invisible to an assertion about a value" was wrong: a tagless entry in a
full `DeserializedResponseCache` evicts one that can be served, which a test can see.

The rule from here: a file below its best-ever score is examined before a release or merge, whatever
the total does.

## 33. A string indexer is accepted as a filter value; shipped as 0.8.1

The translator evaluates the value side of a filter by interpretation (#4, #11). It accepted
`names[0]` on an array, a list and an `IReadOnlyList`, but rejected `text[0]`, because the string
indexer is `String.get_Chars` and the check looked for `get_Item` only. Nothing pinned either
behaviour. The question put to the owner was whether to accept both or reject both, with a
recommendation to accept both because the same kind of expression should behave the same way. The
owner: "ok accept both". The check now accepts any indexer getter (`get_` plus arguments).

Shipped as a patch release, 0.8.1, not a minor one: under #24 only a breaking change on 0.x bumps the
minor version, and this makes an input that threw work without changing anything that worked. The
owner confirmed the release: "yes, fix everything we have tallked about and do the release".

## 34. The packed package is installed and run, in CI and before every publish

Before 1.0 the owner asked what would show the API is stable, and the first thing put on the list
was installing the built package into a fresh project. His reply: "install smoke tests first, those
whould really already be there". They were not. CI ran `dotnet pack` and uploaded the result, and the
AOT smoke test reaches the library through a `ProjectReference`; nothing installed the package, so a
missing target framework, README or icon, or README links that only work on GitHub, would have passed
every check.

`scripts/Test-Package.ps1` reads the `.nupkg` and `.snupkg` (the `lib` assets and XML docs for
netstandard2.0, net8.0 and net10.0, README, icon, nuspec licence, icon, readme and commit, one
dependency group per target, no `.pdb` in the `.nupkg`, no repo-relative link in the packaged README),
restores `TcgDex.CSharpSdk.PackageSmokeTest` from it and runs it. That project is a consumer: it is
outside the solution, inherits nothing from the repository's build props, and uses the public API
only. It runs on net472, which loads the netstandard2.0 asset, as well as net8.0 and net10.0.

Two things make it able to fail rather than just run:

- **nuget.org already has the version in the csproj**, so a restore that is not pinned would install
  the published package and pass. The restore uses a `nuget.config` with package source mapping that
  sends `TcgDex.CSharpSdk` to the local feed alone, into an empty packages folder, and the program
  fails unless the assembly it loaded carries the version and the git commit that were just packed.
  Mapping the id to nuget.org by hand made it fail on all three frameworks, naming the published
  commit.
- **The frameworks are listed in the script**, not read from the project, so one that silently drops
  out fails the run. Dropping net8.0 from the library failed it five ways.

CI runs it on Ubuntu and Windows after the package is built and uploads the symbols package with it;
the release workflow runs it on `./artifacts` before the NuGet login, on Linux, so the netstandard2.0
asset is covered at release time by CI's Windows lane. It was first added as an ordinary job, since
required checks are branch protection and the owner's to change; he then asked for it: "make it
required". Both matrix jobs, `Package install smoke test (ubuntu-latest)` and `(windows-latest)`,
joined the six required checks on 2026-10-19, making eight. A flaky restore from nuget.org therefore
blocks a merge until the job is re-run, which is the price of the check being worth having.

## 35. The dependency-injection entry points are in the pinned public API

Found while building the package smoke test (#34): `PublicApi.approved.cs` had no `AddTcgDex` or
`AddTcgDexWithCaching`. PublicApiGenerator treats every `Microsoft.*` and `System.*` namespace as not
part of the public API unless told otherwise, and the extension methods sit in
`Microsoft.Extensions.DependencyInjection`, the convention for them. So the way most consumers
register the SDK could change without the test that exists for exactly that noticing. I said it
sounded like a bug; the owner: "fix that it soundss like a bug".

`AllowNamespacePrefixes` now names that one namespace, and the baseline gains the two methods (8 lines,
nothing else). `ThePublicSurface_IncludesTheDependencyInjectionEntryPoints` fails if they ever leave it,
so the allow-list cannot be dropped quietly. Changing the signature of `AddTcgDex` now fails the baseline
test at the line of the method; before, it passed.

## 36. The public interfaces stay interfaces; paging and the catalog are reshaped before 1.0

The interface review the soak plan called for (#30), run on 2026-10-24 at the owner's request ("yep, do
the interface review"). Run in-session, not on the credits queue: the owner, "not on the api, do it
here, we have basically no api creds left". Two independent reviews, then a comparison: mine first, then
a Fable review ("do a cross review with fable, give it zero, none, of your findings"). Fable's prompt
held the question and file pointers and none of my verdicts.

**Both reviews agreed, so this is the decision:**
- `ITcgDexClient`, `ICardResource`, `ICardSetResource`, `ISeriesResource`, `IRandomResource` and
  `ITcgDexResponseCache` stay interfaces. A sealed class cannot be stubbed, which is the reason a consumer
  wants these types, and `ITcgDexResponseCache` is implemented by consumers on purpose (Redis, disk). A
  non-sealed class with `virtual` members buys nothing: nobody subclasses a resource.
- The real risk is growth after 1.0. An interface cannot gain a member without breaking a hand-written
  implementer, and netstandard2.0 has no default interface methods. So growth goes into parameters of
  sealed types (`CardFilter`, `CardQuery`), which can gain `init` properties freely, not into new
  interface members.
- `SearchDetailedAsync(CardFilter, int? page, int? itemsPerPage, ...)` was the worst signature: loose
  optionals on the method, while REST paging lives on `CardQuery.Page`. Sort would have been a fourth
  optional or an overload.

**Where the reviews differed, and which was right:** I said keep `ICatalogResource` as it was. Fable said
change it: upstream grows by adding enumeration endpoints (13 members, one per endpoint), every new list
would be a new member and a major bump, and nobody stubs 13 near-identical methods. Fable's reasoning
held; I had weighed mockability and not growth. It also found `StreamAsync`'s `pageSize` had the same
two-sources-of-truth problem as `SearchDetailedAsync`, which I had missed.

**What changed (the owner: "Yes: branch, record the decision in docs/DECISIONS.md (both reviews), then do
the ICatalogResource and paging changes in one PR, TDD, with PublicApi.approved.cs updated"):**
- `ICatalogResource` keeps its 13 named methods and gains `GetValuesAsync(name)` (text) and
  `GetNumberValuesAsync(name)` (numbers), so an endpoint the SDK does not model yet is reachable without a
  breaking release. The name is escaped as a single path segment, like an id.
- `CardFilter` gains `Page` and `ItemsPerPage`; `SearchDetailedAsync` becomes `(CardFilter,
  CancellationToken)`.
- `StreamAsync(CardQuery, CancellationToken)`: the page size is the query's `Page(_, itemsPerPage)`, 100
  when unset; its page number is still ignored.

**Review of the change itself (2026-10-25):** three reviewers, run independently of each other and of me: a
sonnet diff review, a sonnet test-validity review, and Fable. All were given the branch and the intent, no
findings. They agreed on two defects, both fixed in the same PR: a null or blank catalog name threw naming
`id` instead of `name` (it went through the id escaper), and `.` or `..` survived `Uri.EscapeDataString`
and were collapsed by the URI layer, so the "single segment" claim was false. The second also affected ids,
so the fix is in the shared segment escaper and ids are rejected too. Also fixed: the CHANGELOG said the
wire requests were unchanged, which was false for a query that set a page size; the test gaps (each rule
exercised through both catalog methods); and the page-size exceptions now name `Page` and `ItemsPerPage`.
Fable asked whether `CardFilter.Page` (an `init` property) against `CardQuery.Page(page, size)` (a method)
is intended. It is: each follows its own type's idiom, a settings record against an immutable fluent
builder, and unifying them would be a bigger change than this PR.

**Not done:** merging the two `ListAsync` overloads into `ListAsync(CardQuery? query = null, ...)`, which
Fable listed. It would make `ListAsync(cancellationToken)` stop compiling for every caller, to head off a
third overload that no interface has asked for. Recorded as a known trade-off, not a regret.

## 37. The pre-1.0 checklist: Fable's cold read, none of it verified yet

Asked on 2026-10-25, after 0.9.0 (#36): "what else should be done before a real 1.0 release, have fable
answer that question too, without the context of our 'plan' for release right noww". Fable ran with no
release plan, no soak timeline and none of my conclusions, and was told to form its own list from the
code, tests, CI and package metadata before reading this file. The owner then agreed to record it
here ("sure").

**Every row is Fable's claim, not a measurement.** None has been checked against the code. Check each
before acting, and replace its status here. Evidence paths are Fable's.

| # | Sev | Item | Evidence | Status |
|---|---|---|---|---|
| 1 | blocker | `SECURITY.md` says nothing is published to NuGet and lists only `main` as supported | `SECURITY.md:30-36` | **confirmed** 2026-10-25: read the lines; 0.9.0 is live on NuGet |
| 2 | blocker | No written compatibility contract: SemVer scope, the interface-growth rule (#36), TFM lifecycle, `[Obsolete]` lead time, how long an old major gets fixes. .NET 8 reaches end of life 2026-11-10, so decide if `net8.0` stays | `CHANGELOG.md:5-6`, `SECURITY.md:28-36` | **confirmed** 2026-10-25: no SemVer, deprecation or support-window text in the README, SECURITY.md, CONTRIBUTING.md or `docs/*.md` (only the CHANGELOG header names SemVer). `docs/learnings.md:127` notes the net8.0 end-of-life date but nothing decides it |
| 3 | blocker | Models are `record`s holding `IReadOnlyList<T>`, so two cards from identical JSON may not compare equal; record to class later is breaking (#26 did it for `CachedResponse`) | `Models/Card.cs:21,27-34`, `PublicApi.approved.cs:177` | **confirmed by measurement** 2026-10-25: two `Card`s deserialized from the same JSON (one `types` entry) gave `Equals` false, `==` false and different hash codes. Cards whose collections are all empty compare equal, because `[]` is a shared empty array, so the bug shows only on real data. No model overrides `Equals` |
| 4 | blocker | No `EnablePackageValidation` with a baseline; the API baseline test is net10.0-only and does not check binary compatibility | `TcgDex.CSharpSdk.csproj:56-80` | **confirmed** 2026-10-25: no `EnablePackageValidation`, `PackageValidationBaselineVersion` or ApiCompat setting in any csproj, props, targets or workflow |
| 5 | should | `release.yml` does not require green CI on the tagged commit; unit tests on Ubuntu only, no net472, coverage gate or AOT smoke; a comment claims net472 runs | `release.yml:97-123` | unverified |
| 6 | should | Publish job's actions pinned by tag, not SHA | `release.yml:47,49,136,153` | unverified |
| 7 | should | No `ContinuousIntegrationBuild`, no `global.json` | `Directory.Build.props:45` | unverified |
| 8 | should | No 429 handling; `TcgDexApiException` exposes no `Retry-After`. Additive later, but 1.0 docs should say what a rate-limited caller sees | `TcgDexApiException.cs:46-73` | unverified |
| 9 | should | `StreamAsync` ignores the query's page number. Decided in #36; Fable still flags it | `Resources/Resources.cs` | decided, re-open only if the owner wants |
| 10 | should | `ITcgDexClient` has no escape hatch for a new resource group | `TcgDexClient.cs:17-33` | partly decided (#36) |
| 11 | should | `required` on `Id`, `Name`, `LocalId`, `Category`, `Set`: one upstream card missing `name` fails the whole `ListAsync` | `Models/CardBrief.cs:18,31,34` | unverified |
| 12 | should | `docs/publishing.md` stale (version, secret name, dates) | `docs/publishing.md:205,328-330,364` | unverified |
| 13 | nice | Three `TcgDexApiException` constructors give `Kind = Unspecified` | `TcgDexApiException.cs:20-37` | unverified |
| 14 | nice | `TcgDexClient.Create(configureCache: ...)` validates differently from the DI path | `TcgDexClient.cs:272-275` | unverified |
| 15 | question | `AddTcgDex` throws on a second call; the convention is idempotent | `TcgDexServiceCollectionExtensions.cs:57-66` | decided in #23 |
| 16 | nice | `ReleaseDate` is `DateTime?`, `Updated` is `DateTimeOffset?` | `ReleaseDateConverter.cs:5-9` | decided in #22 |
| 17 | nice | Docs: `index.md` omits netstandard2.0; README says .NET Framework 4.6.1+ but only net472 runs; `PublicApiTests` remark about `IAsyncEnumerable` is wrong; thread-safety and the `"TcgDex"` log category are not documented publicly | `docs/index.md:11`, `README.md:26,218`, `PublicApiTests.cs:33-38` | unverified |
| 18 | nice | No signing, no `CODEOWNERS` or templates; tag protection for `v*` not visible in the repo; `CONTRIBUTING.md` bans `--no-build` while the workflows use it | `release.yml:107,115`, `CONTRIBUTING.md:67` | unverified |
| 19 | nice | The netstandard2.0 asset is never run on a modern Core runtime, only net472 | `TcgDexClient.cs:237-263` | unverified |

Fable's three first steps: write the compatibility contract (rows 1, 2, 12), settle model equality (row 3),
and turn on package validation with SHA-pinned actions and a CI gate on release (rows 4 to 7). It found
nothing against the interface decisions in #36.

## 38. The Discord announcement is called from the release workflow

The owner, 2026-10-25, after 0.9.0 published: "i dont think the sdk sent the update notification to the
discord, i see nothing." He was right. `discord-release.yml` (PR #95, secret set 2026-10-21) had **no runs at
all**. It listens for `release: published`, but `release.yml` creates the GitHub Release with the built-in
`GITHUB_TOKEN`, and GitHub does not start workflows from events that token causes (`workflow_dispatch` and
`repository_dispatch` are the exceptions). So the trigger it was built on could never fire for a tag-driven
release. Nothing had exercised it first: the manual `workflow_dispatch` test message was never run either.

The fix: `discord-release.yml` also takes `workflow_call` with a `tag`, and a new `announce` job in
`release.yml` (after `publish`, tag pushes only) calls it. The workflow reads the release back with
`gh release view`, so the fields have one source and a tag that does not exist fails loudly instead of
posting an empty message. A manual `workflow_dispatch` with a `tag` announces an existing release, which is
how 0.9.0 is announced late. The `release` trigger stays for a release a person publishes by hand, which does
fire it; the two cannot both fire for one release. The announce is its own job so a Discord failure cannot fail
or re-run the NuGet publish, and the publish workflow's file name and job are untouched (the trusted
publishing policy names `release.yml`).

Verified with a dry run from the branch before merging (no secret needed); the first real post is the late
0.9.0 announcement, which is the owner's call because it is outward-facing.

## 41. `dotnet pack` validates the package against the last release

Blocker 4 of #37: nothing stopped a release from breaking an assembly someone had already compiled against.
`PublicApi.approved.cs` pins the source-level surface, but on net10.0 only, and it does not look at binary
compatibility or at whether the three target frameworks still expose the same API. The owner asked for it ("Fix
the four confirmed blockers ... package validation"); he gave no view on the settings.

`EnablePackageValidation` with `PackageValidationBaselineVersion` 0.9.0 and `EnableStrictModeForCompatibleTfms`
in the library csproj. Measured before and after, on one change: renaming the public
`MemoryTcgDexResponseCache.Clear()` packed without a word before, and fails the pack after with `CP0002: Member
'void ...Clear()' exists on` the baseline, once for each of `net10.0`, `net8.0` and `netstandard2.0`. The
unmodified code packs clean, with no warnings, including in strict mode.

**The baseline has to move.** It is the release just published, so it is bumped in the PR that prepares the next
one, and it is 0.9.0 until 1.0.0 ships. An intended break before 1.0.0 goes in `CompatibilitySuppressions.xml`
(generated with `-p:ApiCompatGenerateSuppressionFile=true`), recorded here, not by lowering the check. Forgetting
to move it is not dangerous (every break against an older release is still caught); it only makes the check
looser about what the newer release added.

**Cost:** `pack` now restores the baseline package from nuget.org, so a flaky restore can fail it, as the package
smoke test already can (#34).

