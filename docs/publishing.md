# Publishing to NuGet

Two things in one page: how a release happens **now** (the next section), and the
one-time setup and background it was built on, written for a first-time
publisher. **0.1.0 was published on 2026-08-08** by a `workflow_dispatch` run of
`release.yml`, using Trusted Publishing — no API key was ever created. The first
releases were dispatches too; since 0.2.0 they have been tag pushes. The current version is whatever nuget.org
lists for [TcgDex.CSharpSdk](https://www.nuget.org/packages/TcgDex.CSharpSdk);
this page does not say, because a version written here goes stale at the next
release.

> **The nuget.org facts below were verified 2026-08-07.** Everything about
> *this repository's* release pipeline is described in "How a release happens
> now" and in `release.yml` itself, which is the source of truth for it.
>
> Everything about *nuget.org* was re-checked against Microsoft's own pages,
> because [advice about someone else's service is the fastest-rotting kind of
> doc](learnings.md). Sources and their update dates:
> [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
> (2026-08-03),
> [publish a package](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package)
> (2026-06-11),
> [scoped API keys](https://learn.microsoft.com/en-us/nuget/nuget-org/scoped-api-keys)
> (2026-02-02).
>
> One caveat found while doing it: **the API key lifetime change below is not in
> the Learn documentation**, only in the linked devblogs announcement, and the
> Learn pages still show a 365-day example. The announcement is the authority
> here and the dates in it are quoted verbatim — but if the two ever appear to
> disagree, that is why.

---

## What publishing actually is

A NuGet package is a `.nupkg` file — a zip containing your compiled DLLs, the
README, the licence, and a manifest. Publishing means uploading that file to
nuget.org, after which anyone can run `dotnet add package TcgDex.CSharpSdk`.

Two facts worth internalising before the first push:

- **A published version is permanent.** You can *unlist* a version so it stops
  appearing in search, but you cannot delete it. Anyone who already depends on
  it keeps resolving it. There is no "undo".
- **The package ID is claimed on first publish.** `TcgDex.CSharpSdk` becomes
  yours permanently and nobody else can ever use that ID.

That is why a release is gated on green CI and a reviewed change, not on a button.

---

## How a release happens now

The steps, in order. The mechanics live in
[`release.yml`](https://github.com/PinKushin/TcgDex.CSharpSdk/blob/main/.github/workflows/release.yml);
this is the shape of it.

1. **Prepare the release in the pull request that needs it**, not in a follow-up:
   bump `<Version>` in `TcgDex.CSharpSdk/TcgDex.CSharpSdk.csproj`, rename the
   `[Unreleased]` heading in `CHANGELOG.md` to `[x.y.z] - date` (and add the
   compare link at the bottom), and move
   `PackageValidationBaselineVersion` in the same csproj to the **previous**
   release, the newest one already on nuget.org, never the one being prepared
   (the baseline is downloaded from nuget.org at restore, so a version that is
   not published yet fails every build). The workflow refuses to publish if
   `<Version>` or the changelog heading does not match the tag.
2. **Review it, then merge.** Reviews come before auto-merge. The merge commit is
   the thing that gets tagged.
3. **Tag that merge commit and push the tag:** `git tag vX.Y.Z <merge-sha>` then
   `git push origin refs/tags/vX.Y.Z`. Verify the version in the merge commit
   first (`git show <sha>:TcgDex.CSharpSdk/TcgDex.CSharpSdk.csproj`); a stale
   merge once got a release tag.
4. **The tag push runs `release.yml`:**
   - `ci-gate` waits for CI on that exact commit and fails the release unless it
     passed (`scripts/Assert-CiPassed.ps1`, DECISIONS #42). If CI was slow or
     briefly unreachable, "Re-run all jobs" on that run is the recovery.
     **This job has not yet run in a real release:** 0.9.0 was tagged before it
     existed, so the next tag is its first run.
   - `publish` resolves the version from the tag, builds with `-warnaserror`, runs
     the unit tests (net8.0 and net10.0 on its Linux runner; net472 and the rest
     of CI are what the gate vouches for), packs, installs and runs the packed
     package on net8.0 and net10.0 (`scripts/Test-Package.ps1`), logs in to
     nuget.org with a one-hour OIDC key, pushes, and creates the GitHub Release
     with the packages attached.
   - `announce` posts the GitHub Release to Discord through the repository secret
     `DISCORD_WEBHOOK_URL`. If that secret is unset, it warns and succeeds, so
     nothing is posted and the run stays green; the job summary of `publish`
     carries the text to paste by hand. To announce a release by hand, dispatch
     `discord-release.yml` with its tag (it must look like `v1.2.3`, the secret
     must be set, an empty tag posts a test message, and `dry_run` needs no secret).
5. **Check afterwards:** NuGet lists the version (usually a few minutes; 0.9.0 took
   over ten, so give it time before assuming a failure), the DLLs in the package
   carry the version and the commit, and the workflow run has no annotations
   beyond the known notices (the Ubuntu 26 migration notice on Ubuntu jobs,
   DECISIONS #14, and the macOS queue-time notice).

Versions mean what [Versioning and compatibility](versioning.md) says they mean.

---

## One-time setup

### 1. Create a nuget.org account

Sign in at [nuget.org](https://www.nuget.org) with a Microsoft account.

### 2. ID prefix reservation — there is no self-serve page

Reservation is **not** a button in the account UI. Nothing under
`nuget.org/account/Packages` offers it. The process is an email to
**account@nuget.org** giving your nuget.org owner display name and the prefixes
you want, per
[the official reference](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation).
A human reviews it and replies with an acceptance or a rejection reason.

**For this package, expect a rejection.** The first published criterion is
whether the prefix "properly and clearly identify the reservation owner", and
`TcgDex.` identifies TCGdex — the upstream API — not you. Reserving it would
also block the TCGdex maintainers from ever publishing under their own name,
which is the exact confusion the criteria exist to prevent.

The honest options are to skip reservation, or to ask the TCGdex maintainers
whether they want to reserve `TcgDex.` and delegate a subset to you (the process
supports delegation). Neither blocks publishing — an unreserved ID publishes
normally; it just does not carry the reserved-prefix tick.

### 3. Publishing credentials — prefer Trusted Publishing

Long-lived API keys are on their way out, so set this up before writing any
workflow.

**Trusted Publishing** is the current recommendation. Your workflow requests a
short-lived OIDC token from GitHub, nuget.org validates it against a policy you
registered and hands back an API key valid for **one hour**. Nothing long-lived
is ever stored — there is no secret to leak, rotate, or accidentally commit.

Set it up at **nuget.org → your username → Trusted Publishing**, adding a policy:

| Field | Value |
|---|---|
| Policy name | `TcgDex.CSharpSdk release.yml` — see below |
| Repository Owner | `PinKushin` |
| Repository | `TcgDex.CSharpSdk` |
| Workflow File | `release.yml` — **filename only**, no `.github/workflows/` prefix |
| Environment | leave empty unless the job declares `environment:` |

**On the policy name.** Microsoft's page does not document this field, so treat
it as an account-level label the way an API key name is — nothing on nuget.org
appears to surface it, but that is observation rather than a documented
guarantee, so do not put anything sensitive in it.

Name it after the **repository and workflow**, not the package. A policy is not
package-scoped: it *"will apply to all packages owned by the selected owner"*,
constrained by the repository and workflow file. So when a second package
appears, the repo and workflow are what tell two policies apart, and the package
name would not.

The one thing that must match exactly is the filename. A policy for `release.yml`
does not trust `publish.yml`, and the failure is a rejected push at the moment
you are trying to ship rather than anything earlier.

Two behaviours worth knowing in advance:

- The feature is **still rolling out gradually**. If the menu item is not there,
  it is not yet enabled for your account — use the API key fallback below.
- A policy on a **private** repo starts *temporarily active for 7 days*, because
  nuget.org needs the repository and owner IDs (which arrive with the first
  successful publish) to pin the policy. Publish inside that window or the
  policy goes inactive; you can restart the window any time.

### 4. API key — fallback, and now short-lived by design

Only if Trusted Publishing is unavailable to your account. **Account → API Keys
→ Create**:

| Field | Value |
|---|---|
| Key name | something identifiable, e.g. `tcgdex-release` |
| Expiry | **the shortest offered** for a one-off manual push; 30 days for a pipeline — see below |
| Scopes | **Push** only |
| Glob pattern | `TcgDex.CSharpSdk*` |

For the first manual push the key exists for minutes. Pick the shortest expiry
the form allows and delete it afterwards anyway — the expiry is the backstop for
forgetting, not the plan.

Scope it to the glob pattern rather than "all packages". If the key leaks, the
blast radius is one package rather than your whole account.

**Copy the key immediately — it is shown exactly once.**

The 365-day key option is being withdrawn:

- **From 2026-08-17**, new keys are capped at **30 days**; 365 is gone.
- **On 2026-11-01**, every key created before 2026-08-17 expires, whatever
  duration it was issued with.

([announcement](https://devblogs.microsoft.com/dotnet/strengthening-nuget-supply-chain-security-reducing-api-key-lifetime/))

So a key-based release pipeline now means re-issuing a secret every month
indefinitely. That recurring chore is the reason to do the Trusted Publishing
setup instead — it is a one-time configuration with no expiry to track.

**What the dates mean if you are publishing right now.** A key created before
2026-08-17 may still be issued for longer than 30 days, but it dies on
2026-11-01 regardless of the duration printed on it. So there is no window to
race for and nothing to rush: for a single manual push the duration is
irrelevant, and for anything automated the answer is Trusted Publishing either
way. nuget.org emails the account holder ten days before a key expires, which is
the only warning you get.

If you do use a key, store it as a GitHub secret named `NUGET_API_KEY`:
Repository → **Settings → Secrets and variables → Actions → New repository
secret**. Never put it in a file, a commit, or a workflow literal. If it ever
lands in git history, revoke it on nuget.org first — rewriting history does not
un-leak it, because anyone who cloned still has it.

---

## First-publish checklist (2026-08-07, kept as a record)

This is what was checked before 0.1.0, each by running it rather than
remembering it. Numbers in it are from that day and are **not** maintained: the
size, coverage figures and dependency list below are for 0.1.0. For a release
now, the pipeline above enforces the equivalents on every one (the coverage gate,
the pinned public API, package validation against the last release, and the
install-and-run package test), so this list is history, not a to-do.

- [x] `PackageId`, `Version`, `Authors`, `Description`, `PackageTags` set
- [x] `PackageLicenseExpression` (MIT) and `LICENSE.txt` shipped
- [x] `README.md` embedded — this is the package page body
- [x] `RepositoryUrl` + SourceLink, so debuggers can step into the source
- [x] `IncludeSymbols` with `snupkg`, so symbols publish alongside
- [x] Builds clean with `-warnaserror` on `netstandard2.0`, `net8.0` and `net10.0`
- [x] `.nuspec` read out of the packed `.nupkg` — dependency versions and the
      recorded commit are permanent once published
- [x] README links absolute, since the package page renders it standalone
- [x] Security analyzers and `NuGetAudit` enabled, both verified failing
- [x] **Coverage gates met** — 99.80% line, 96.58% branch, both above the CI
      thresholds. See [`coverage.md`](coverage.md)
- [x] **Public API pinned** — `PublicApiTests` fails on any surface change, so
      what ships is what was reviewed
- [x] **Nothing test-only leaked into the package** — the `.nuspec` lists only
      the three `Microsoft.Extensions.*` runtime dependencies per target. No
      SharpFuzz, no PublicApiGenerator, no test packages
- [x] **Packs at 462 KB**, far inside nuget.org's ~250 MB limit, with three
      `lib` folders and XML docs in each
- [x] **The packed package is installed and run** — `scripts/Test-Package.ps1` reads
      the `.nupkg` and `.snupkg`, restores them into a fresh project from a feed
      that maps `TcgDex.CSharpSdk` to the local copy alone, and runs a consumer on
      net472, net8.0 and net10.0 (DECISIONS #34). CI does this on every push, on
      Linux and Windows, and the release workflow does it on the exact package
      before the push to nuget.org
- [x] **Version decided** — `0.1.0`

## Choosing a version

What each number promises from `1.0.0` on is in
[Versioning and compatibility](versioning.md). Before `1.0.0` the minor number
carries breaking changes, and the changelog says so under each one.

**Do not ship `1.0.0` until you are willing to live with the API as it stands**,
because breaking it afterwards means a `2.0.0` and a migration story for every
consumer. Pre-release versions (`1.0.0-beta.1`) do not surface by default and are
a way to get real usage first, but a release candidate was rejected for this
project (DECISIONS #30): consumers on the old version would never see it.

---

## Publishing

**You almost certainly do not need an API key at all.** Read this before creating
one.

Trusted Publishing exchanges a GitHub OIDC token for a one-hour key, and GitHub
only issues those tokens *inside a workflow run*. Your laptop cannot mint one, so
there is genuinely nothing to exchange from a terminal — that part of the
constraint is real.

What does not follow is that a release has to happen from a terminal. A
`workflow_dispatch` trigger on the release workflow (it has one) runs in Actions,
so OIDC works, and you get the same "watch it happen, one step at a time" that a
manual push offers, with a live log — and no long-lived credential is ever
created, stored, or forgotten about. That is how the first release was done.

So the ordering is:

| | Key needed | When it makes sense |
|---|---|---|
| **Tag push** | **none** | **The normal release** ("How a release happens now"). Runs the CI gate, creates the GitHub Release and announces it. |
| `workflow_dispatch` | none | How 0.1.0 was published. It runs from the branch you choose, so `ci-gate` and the version checks look at that branch's head, not at a tag; it publishes but creates no GitHub Release and does not announce. To retry a tag's release, re-run that tag's own run, or dispatch with the tag selected as the ref. |
| `dotnet nuget push` from a terminal | yes | Only if Trusted Publishing is not yet enabled on your account |

A failed publish costs nothing and claims nothing — the package ID is only taken
on a *successful* push — so there is no risk in trying the keyless route first
and falling back if the policy is not active yet.

### Manual from a terminal — only if Trusted Publishing is unavailable

The rollout is gradual, so if **Trusted Publishing** is not in your nuget.org
account menu yet, this is the fallback.

```bash
dotnet pack TcgDex.CSharpSdk/TcgDex.CSharpSdk.csproj -c Release -o ./artifacts
```

Inspect the result before pushing — a `.nupkg` is a zip, so open it and confirm
the DLLs, README and licence are all present and there is nothing that should
not ship.

This path needs a real API key, for the reason above: there is no OIDC token to
exchange outside a workflow run. Treat the key as disposable — create it, push,
then delete it on nuget.org immediately afterwards.

**Pass the key by environment variable, not on the command line.** A key in
`--api-key` lands in your shell history, and on Windows in
`ConsoleHost_history.txt`, where it outlives the "delete it the same day" plan.
`NUGET_API_KEY` is supported from NuGet 7.6 (.NET SDK 10.0.300). The repo's own
SDK floor is lower (`global.json`, 10.0.100), so check `dotnet --version` first.

```bash
# PowerShell
$env:NUGET_API_KEY = '<paste-here>'
dotnet nuget push ./artifacts/TcgDex.CSharpSdk.<version>.nupkg `
  --source https://api.nuget.org/v3/index.json
Remove-Item Env:\NUGET_API_KEY
```

```bash
# bash — the leading space keeps it out of history when HISTCONTROL=ignorespace
 export NUGET_API_KEY='<paste-here>'
dotnet nuget push ./artifacts/TcgDex.CSharpSdk.<version>.nupkg \
  --source https://api.nuget.org/v3/index.json
unset NUGET_API_KEY
```

On an older SDK, `--api-key` is the only option — in which case delete the key
on nuget.org immediately after the push rather than at the end of the day.

The `.snupkg` pushes automatically alongside it. Indexing takes a few minutes
before the package is installable.

If you would rather never handle a key at all, skip the manual push and let the
workflow do it. You lose the chance to watch the push happen; you gain having no
long-lived credential ever exist.

### Notes on `release.yml`

The workflow lives at
[`.github/workflows/release.yml`](https://github.com/PinKushin/TcgDex.CSharpSdk/blob/main/.github/workflows/release.yml).
Its filename must match the **Workflow File** in the Trusted Publishing policy
exactly, so it is never renamed. It is deliberately not reproduced here: a copy
in prose is a copy that drifts, and this document has already been wrong once by
describing something the repository does not do.

Read the file itself; every non-obvious line carries its reason. The parts that
matter:

| | Why |
|---|---|
| `ci-gate` job, which `publish` needs | A tag can be pushed to any commit. This waits for CI on the exact commit and fails the release unless it passed. It is its own job so `publish` keeps its name; the policy names the file. |
| `id-token: write` | Lets the job request the OIDC token. Missing it is the commonest reason trusted publishing fails first time, and the error is unhelpful. |
| `Resolve version` runs **before** `Build` | The version has to reach the compile, not just the pack. `dotnet pack --no-build -p:Version=` alone stamps the package while the assembly inside keeps the csproj value — a DLL and a package that disagree. |
| Semantic-version, `<Version>` and changelog checks | Fail loudly on a typo or a release whose pull request forgot the bump. NuGet accepts a surprising amount of nonsense as a version, and a published one cannot be withdrawn. |
| `ContinuousIntegrationBuild` on the build and pack | Keeps the runner's checkout path out of the binary and its symbols. |
| Every action pinned to a commit SHA | `NuGet/login` receives the publish token, so a moved tag there matters most. |
| `user: PinKushin` | The nuget.org **profile name**, not an email — the commonest `NuGet/login` failure. Written literally rather than as a secret because it is public: it is the package owner on every package page and in the policy itself. |
| `--skip-duplicate` | Makes a re-run harmless, which is what lets a retry follow a partial run. |
| `announce` job | Posts the release to Discord after the publish; a Discord failure cannot fail the publish. |

On an API key instead, drop the `NuGet login` step and the `id-token` permission,
and use `--api-key ${{ secrets.NUGET_API_KEY }}`.

**History: how the first release was done (a dispatch, not a tag).** GitHub →
**Actions** → **Release** → **Run workflow** → enter the version → run, and watch
the log. If the policy is not active or the login step fails, nothing has been
published and nothing has been claimed; fix it and press it again. Today a
dispatch would also run `ci-gate` against the head of the branch you pick, and it
creates no GitHub Release and no announcement (see the table above).

Since then, releases are a tag on the merge commit ("How a release happens
now"). `--skip-duplicate` stops a re-run from failing on an already-published
version, which is what makes a retry, or a dispatch followed by a tag push, safe
to repeat.

The version that ships is read from the tag (or the dispatch input) and stamped
into the build, so the package and the DLL inside it agree. The workflow then
**refuses to publish** if the csproj `<Version>` or the changelog heading does
not match, which catches the commonest release mistake, tagging `v0.2.0` while
`<Version>` still says `0.1.0`, before anything is pushed.

---

## Getting listed on tcgdex.dev/sdks

**Checked 2026-10-26.** Still **no C#/.NET SDK listed** — the site shows Java,
JavaScript, Kotlin, PHP, TypeScript and Python as official SDKs, and the
*Community SDKs* section is empty apart from *"Contact us on Discord to have your
SDK added here."* This would be the first entry in it.

The route the site names is Discord, not a pull request. Worth having in place
before asking: a published package, a README that stands on its own, and green
CI, all of which the release pipeline above produces.

---

## If something goes wrong

- **Pushed a broken version** — publish a fixed one immediately and *unlist* the
  broken version on nuget.org. You cannot delete it.
- **Leaked the API key** — revoke it on nuget.org first, then rotate the GitHub
  secret. Revocation is what stops the leak; removing the file does not.
- **Wrong package ID** — unlist it and publish under the right ID. The wrong ID
  stays claimed permanently, so check spelling before the first push.
- **`NuGet/login` fails to authenticate** — in order of likelihood: `user:` is
  an email address instead of the profile name; the job is missing
  `id-token: write`; the policy's **Workflow File** does not match the actual
  filename, or was entered with the `.github/workflows/` path; the job declares
  an `environment:` the policy does not.
- **Publishing worked, then stopped** — check the policy is still active. A
  private-repo policy that never published inside its 7-day window goes
  inactive, and an org-owned policy goes inactive if you leave the org.
- **A push that used to work now 403s** — an API key expired. Every key created
  before 2026-08-17 dies on 2026-11-01 regardless of its stated duration.
