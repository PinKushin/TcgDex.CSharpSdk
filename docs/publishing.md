# Publishing to NuGet

Written for a first-time publisher. **Nothing here has been done yet** — the
package builds locally but has never been pushed, and by decision it will not be
until test coverage is where it should be.

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

That is why the pre-flight checklist below matters more than the mechanics.

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
| Repository Owner | `PinKushin` |
| Repository | `TcgDex.CSharpSdk` |
| Workflow File | `release.yml` — **filename only**, no `.github/workflows/` prefix |
| Environment | leave empty unless the job declares `environment:` |

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
| Expiry | 30 days — see below |
| Scopes | **Push** only |
| Glob pattern | `TcgDex.CSharpSdk*` |

Scope it to the glob pattern rather than "all packages". If the key leaks, the
blast radius is one package rather than your whole account.

**Copy the key immediately — it is shown exactly once.**

The 365-day option this document previously recommended is being withdrawn:

- **From 2026-08-17**, new keys are capped at **30 days**; 365 is gone.
- **On 2026-11-01**, every key created before 2026-08-17 expires, whatever
  duration it was issued with.

([announcement](https://devblogs.microsoft.com/dotnet/strengthening-nuget-supply-chain-security-reducing-api-key-lifetime/))

So a key-based release pipeline now means re-issuing a secret every month
indefinitely. That recurring chore is the reason to do the Trusted Publishing
setup instead — it is a one-time configuration with no expiry to track.

If you do use a key, store it as a GitHub secret named `NUGET_API_KEY`:
Repository → **Settings → Secrets and variables → Actions → New repository
secret**. Never put it in a file, a commit, or a workflow literal. If it ever
lands in git history, revoke it on nuget.org first — rewriting history does not
un-leak it, because anyone who cloned still has it.

---

## Pre-flight checklist

Everything here is already true except the last two:

- [x] `PackageId`, `Version`, `Authors`, `Description`, `PackageTags` set
- [x] `PackageLicenseExpression` (MIT) and `LICENSE.txt` shipped
- [x] `README.md` embedded — this is the package page body
- [x] `RepositoryUrl` + SourceLink, so debuggers can step into the source
- [x] `IncludeSymbols` with `snupkg`, so symbols publish alongside
- [x] Builds clean with `-warnaserror`, both `net8.0` and `net10.0`
- [ ] **Test coverage at target** — the current gate, see [`coverage.md`](coverage.md)
- [ ] **Version decided** — currently `0.1.0`

## Choosing the first version

Semantic versioning, and the choice carries meaning:

| Version | Signals |
|---|---|
| `0.1.0` | Early. API may change without a major bump. Reasonable for a first release. |
| `1.0.0` | The public API is stable and you will not break it without a major version. |

Publish `0.x` while the shape is still moving. **Do not ship `1.0.0` until you
are willing to live with the API as it stands**, because breaking it afterwards
means a `2.0.0` and a migration story for every consumer.

Pre-release versions (`1.0.0-beta.1`) do not surface by default and are the
right way to get real usage before committing.

---

## Publishing

### Manual, the first time

Do the first publish by hand so you see each step.

```bash
dotnet pack TcgDex.CSharpSdk/TcgDex.CSharpSdk.csproj -c Release -o ./artifacts
```

Inspect the result before pushing — a `.nupkg` is a zip, so open it and confirm
the DLLs, README and licence are all present and there is nothing that should
not ship.

A manual push needs a real API key: Trusted Publishing issues its token to a CI
job, so there is nothing to exchange from your laptop. Treat that key as
disposable — create it, push, then delete it on nuget.org the same day.

```bash
dotnet nuget push ./artifacts/TcgDex.CSharpSdk.0.1.0.nupkg \
  --api-key <YOUR_KEY> \
  --source https://api.nuget.org/v3/index.json
```

The `.snupkg` pushes automatically alongside it. Indexing takes a few minutes
before the package is installable.

If you would rather never handle a key at all, skip the manual push and let the
tag-triggered workflow below do the first release too. You lose the chance to
watch the push happen; you gain having no long-lived credential ever exist.

### Automated, afterwards

Once the manual run has proven the package, publish on tag push. Add
`.github/workflows/release.yml` — and note the filename must match the **Workflow
File** in the Trusted Publishing policy exactly:

```yaml
name: Release
on:
  push:
    tags: ["v*"]

permissions:
  contents: read

jobs:
  publish:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      id-token: write   # lets this job request the OIDC token; without it, login fails
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            8.0.x
            10.0.x

      # Never publish something that is not green.
      - run: dotnet test TcgDex.CSharpSdk.Tests/TcgDex.CSharpSdk.Tests.csproj -c Release

      # Version comes from the tag, so the tag and the package can never disagree.
      - name: Pack
        run: |
          VERSION="${GITHUB_REF_NAME#v}"
          dotnet pack TcgDex.CSharpSdk/TcgDex.CSharpSdk.csproj \
            -c Release -o ./artifacts -p:Version="$VERSION"

      # Exchange the OIDC token for a one-hour key. Last step before the push:
      # request it early and it can expire before the push runs.
      - name: NuGet login
        uses: NuGet/login@v1
        id: login
        with:
          user: ${{ secrets.NUGET_USER }}   # nuget.org profile name, NOT the email

      - name: Push
        run: >
          dotnet nuget push "./artifacts/*.nupkg"
          --api-key ${{ steps.login.outputs.NUGET_API_KEY }}
          --source https://api.nuget.org/v3/index.json
          --skip-duplicate
```

`NUGET_USER` is a repository secret holding your nuget.org **profile name** —
not your email address, which is the commonest reason `NuGet/login` fails. It is
not sensitive; it is a secret only so the workflow file stays copy-pasteable.

On an API key instead, drop the `NuGet login` step and the `id-token` permission,
and use `--api-key ${{ secrets.NUGET_API_KEY }}`.

Release with:

```bash
git tag v0.1.0
git push origin v0.1.0
```

`--skip-duplicate` stops a re-run from failing on an already-published version.
Deriving the version from the tag removes the commonest release mistake:
tagging `v0.2.0` while the csproj still says `0.1.0`.

---

## Before submitting to tcgdex.dev/sdks

TCGdex lists community SDKs. Once published, open a pull request against
[tcgdex/documentation](https://github.com/tcgdex/documentation) adding this one.
There is currently **no C#/.NET SDK listed** — Java, JavaScript, Kotlin, PHP,
TypeScript and Python only — which is the whole reason this project exists.

Worth having in place first: a published package, a README that stands on its
own, and green CI. All three are the point of the checklist above.

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
