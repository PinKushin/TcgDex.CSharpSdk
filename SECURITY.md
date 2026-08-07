# Security policy

## Reporting a vulnerability

**Use [GitHub's private vulnerability reporting](https://github.com/PinKushin/TcgDex.CSharpSdk/security/advisories/new)**
— it is enabled on this repository. That keeps the report private until a fix
exists, and gives us a place to work on it together.

Please do not open a public issue for a suspected vulnerability. Please also do
not report it to TCGdex: this is an independent client library and their team
did not write it.

### What to expect

This is maintained by one person, as a side project. That sets an honest
expectation rather than an aspirational one:

- **Acknowledgement within about a week.** If you have heard nothing after two,
  assume the notification was missed and open a public issue saying only that
  you are waiting on a private report — no details.
- **No bounty.** There is no budget for one, and saying so up front is fairer
  than letting you find out after the work.
- **Credit if you want it.** Advisories will name you unless you ask otherwise.

If a report turns out to be a bug rather than a vulnerability, it gets handled
as a normal issue and you will be told that plainly.

## Supported versions

| Version | Supported |
|---|---|
| Unreleased (`main`) | Yes |

**Nothing has been published to NuGet yet.** Once `1.0.0` ships this table will
list the supported line; until then `main` is the only thing that exists, and
fixes land there.

## What this library actually exposes

Worth being concrete, because "an HTTP client for a card database" sounds like
it has no attack surface and that is not quite true.

**It parses untrusted input.** Every response body comes from a server this
library does not control, and
[`TcgDexOptions.BaseAddress`](docs/getting-started.md) is deliberately
overridable so you can point it at a mirror or a proxy. A malicious or
compromised endpoint is therefore inside the threat model, not outside it. That
path is defended by a response-size ceiling applied to *decompressed* bytes, a
request timeout, and a single error contract — and it is
[fuzzed](docs/measuring.md), continuously and on every push.

**It builds URLs from your users' input.** A search box wired to
`Where(c => c.Name == value)` puts caller-supplied text into a query string.
Escaping that is this library's job, and it is fuzzed as its own case.

**It holds no credentials.** The TCGdex API needs no authentication, so there
are no keys or tokens to leak. If a future version gains them, this section
changes first.

### Things that are not vulnerabilities here

- **A slow or unavailable TCGdex API.** That is theirs, and the timeout exists
  so it does not become yours.
- **Denial of service by configuring the library badly** — setting
  `MaxResponseBytes` to zero, or `Timeout` to infinite, and then being surprised.
  Those are documented escape hatches with documented consequences.
- **Anything requiring an attacker who already controls the calling process.**

## How dependencies are handled

- **`NuGetAudit` is set to fail the build** on any known-vulnerable package,
  direct or transitive, at `low` severity and above. A vulnerable dependency
  cannot be merged, not merely reported.
- **Dependabot** raises updates weekly for NuGet packages and GitHub Actions,
  so advisories are not the only thing that moves versions.
- **CodeQL** runs on every push.
- Runtime dependencies are pinned to the *lowest* version that satisfies each
  target framework rather than the newest, because a library's floor becomes
  every consumer's floor. See `Directory.Packages.props` for the reasoning.
