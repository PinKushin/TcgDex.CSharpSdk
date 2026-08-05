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

### 2. Reserve the ID prefix (optional but worth it)

`TcgDex.` is a prefix you may want to hold if you later publish
`TcgDex.CSharpSdk.Extensions` or similar. Request it under **Manage Packages →
ID Prefix Reservation**. Reserved prefixes also get a verified-owner tick on the
package page.

### 3. Create an API key

**Account → API Keys → Create**:

| Field | Value |
|---|---|
| Key name | something identifiable, e.g. `tcgdex-ci` |
| Expiry | 365 days (the maximum) |
| Scopes | **Push** only |
| Glob pattern | `TcgDex.CSharpSdk*` |

Scope it to the glob pattern rather than "all packages". If the key leaks, the
blast radius is one package rather than your whole account.

**Copy the key immediately — it is shown exactly once.**

### 4. Store the key as a GitHub secret

Repository → **Settings → Secrets and variables → Actions → New repository
secret**, named `NUGET_API_KEY`.

Never put the key in a file, a commit, or a workflow literal. If it ever lands
in git history, revoke it on nuget.org first — rewriting history does not
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

```bash
dotnet nuget push ./artifacts/TcgDex.CSharpSdk.0.1.0.nupkg \
  --api-key <YOUR_KEY> \
  --source https://api.nuget.org/v3/index.json
```

The `.snupkg` pushes automatically alongside it. Indexing takes a few minutes
before the package is installable.

### Automated, afterwards

Once the manual run has proven the package, publish on tag push. Add
`.github/workflows/release.yml`:

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

      - name: Push
        run: >
          dotnet nuget push "./artifacts/*.nupkg"
          --api-key ${{ secrets.NUGET_API_KEY }}
          --source https://api.nuget.org/v3/index.json
          --skip-duplicate
```

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
