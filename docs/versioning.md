# Versioning and compatibility

What a version number of `TcgDex.CSharpSdk` promises. This page is the contract; the reasoning behind each
rule is in [DECISIONS](DECISIONS.md) (#36 and #39).

The package follows [Semantic Versioning 2.0](https://semver.org/spec/v2.0.0.html). Before `1.0.0` the
minor number carries breaking changes (0.8.0 and 0.9.0 did), and the
[changelog](https://github.com/PinKushin/TcgDex.CSharpSdk/blob/main/CHANGELOG.md) says so under each one.
From `1.0.0` the rules below hold.

## What counts as the public API

Everything in the pinned surface,
[`PublicApi.approved.cs`](https://github.com/PinKushin/TcgDex.CSharpSdk/blob/main/TcgDex.CSharpSdk.Tests/PublicApi.approved.cs).
A change to it fails a test, so it cannot happen by accident. Anything `internal`, and anything in a
namespace that is not in that file, is not public API and can change in any release.

## What each number means

| Release | May contain | Never contains |
|---|---|---|
| **Patch** (`1.0.1`) | Bug fixes, documentation, dependency updates that change no documented behaviour and take no new major version of a dependency | New public members, a change to documented behaviour |
| **Minor** (`1.1.0`) | New public types, new members on classes (never on an interface, below), new overloads, new optional properties on `CardFilter`, `CardQuery`, `TcgDexOptions` and the models, new enum values, `[Obsolete]` markings | Removing or renaming a public member, **dropping a target framework**, changing a signature (**adding a parameter, even an optional one, is a signature change**: it breaks compiled callers, so it ships as a new overload), changing what a documented call returns for the same input |
| **Major** (`2.0.0`) | Anything above, plus removals and signature changes, new members on an interface, and dropping a target framework | |

**What counts as a fix.** A change that makes a call do what its documentation says is a patch, even when a caller
had come to depend on the departure; the changelog says so. A change to observable behaviour the documentation
never promised (how many requests a call makes, an ordering, a default) is *not* a defect fix: it ships in a minor
release with a changelog entry. Before `1.0.0` this is not yet in force, and `0.9.0` is the example of what it
rules out as a patch: `StreamAsync` began honouring the query's page size, which changes the request count, and
it shipped as a breaking change.

**Binary compatibility is the firm promise; source compatibility is best effort.** One caveat: the assembly
version currently tracks the package version rather than being pinned to the major, so a .NET Framework consumer
(who gets the `netstandard2.0` asset) sees a new assembly version on every release, patches included. Whether to
pin it is not decided. A new overload can make an
existing call that passes `null` ambiguous, a parameter rename breaks named arguments, and a nullability change can
add a warning. These are avoided where possible and named in the changelog when they happen. They are not on their
own a reason for a major version.

**New members on a model or options type must be optional**: a settable property with a default that keeps
today's behaviour, never `required`, which would break every caller that constructs one. A model gaining a
property because the TCGdex API gained a field is a minor release. Adding a property also changes what the
model's value equality compares, which is expected.

**Enums grow.** `TcgDexErrorKind`, `ImageQuality` and `ImageFormat` may gain a value in a minor release, so a
`switch` over one needs a default arm. A value the API newly returns in an existing field (a new rarity, a
new category) is not a change to the SDK at all: those fields are text, not enums, for exactly this reason.

## Interfaces

`ITcgDexClient`, `ICardResource`, `ICardSetResource`, `ISeriesResource`, `ICatalogResource` and
`IRandomResource` exist so that you can inject and fake the client in your tests. They are for
**consuming**, but none of them will gain a member, or a parameter, within a major version: adding one would
break every class that implements the interface by hand, and that is a major change. Growth goes elsewhere:
new options go on the sealed types the interfaces take (`CardFilter`, `CardQuery`), and
`ICatalogResource.GetValuesAsync` and `GetNumberValuesAsync` reach an enumeration endpoint the SDK has no named
method for yet. If neither can carry a change, it waits for the next major version.

`ITcgDexResponseCache` is the other kind: a deliberate extension point (Redis, disk) that you are expected to
implement, and it likewise will not gain members within a major version. The types it passes around are the same
kind of extension point: `CachedResponse` may gain optional properties but never a `required` one, and
`TcgDexCacheOptions`, which you may subclass to override `GetTimeToLive`, may gain members. A new member on a
class you can subclass may share a name with one you declared (compiler warning CS0108); that is not treated as
a break.

## Target frameworks

The package ships `net10.0`, `net8.0` and `netstandard2.0`. NuGet picks the closest match, and
`netstandard2.0` serves .NET Framework and the older .NET versions; the table in
[getting started](getting-started.md#which-assembly-you-get) lists which framework gets which asset.

A target framework is **dropped only in a major version**, never in a minor or a patch, even after its runtime
is out of support.

The reason is that a drop is not free for the people on it. The API is the same on every asset, so nothing
stops compiling: a consumer on the dropped framework falls back to the `netstandard2.0` asset. But for `net8.0`
(out of support 2026-11-10) that consumer would lose:

- immediate cancellation while a response body is streaming (it becomes best-effort);
- `PooledConnectionLifetime`, so a long-lived client can hold a connection and miss a DNS change;
- the trimming and Native AOT declaration, which the `netstandard2.0` asset cannot make;
- and would gain the extra package dependencies that asset carries.

An upgrade within a major version should not cost anyone that, so `net8.0` ships for as long as `1.x` does,
whatever its runtime's support status. The first major version after its end of life is where it can go.

## Deprecation

A public member is marked `[Obsolete]` in a minor release, with a message naming the replacement, and is
removed no earlier than the next major release. There is no promise about how many minor releases lie
between the two, only that the mark comes first.

## Which versions get fixes

Only the latest release of the current major version; when `2.0.0` ships, `1.x` gets no more fixes. This is a
single-maintainer project, and holding a second line open would mean fixes arriving late on both. A security
report is handled as
[`SECURITY.md`](https://github.com/PinKushin/TcgDex.CSharpSdk/blob/main/SECURITY.md) describes, and
the fix ships as a patch release of the latest version.

## What is not promised

- **The TCGdex service itself.** This SDK reads a third-party API. It can change a field or add a
  rate limit without notice, and the SDK follows it; see [api-info](api-info.md) for what has been
  observed.
- **The wire requests** are not API: a minor release may send a different, equivalent request, for example a
  different page size. The changelog records a change a caller can observe.
- **Exact log text and exception messages.** Log categories, `TcgDexApiException.Kind` and the exception
  types are API; the wording is not.
- **A serialized model.** The property names on the models are the JSON names the SDK reads, not a format it
  promises to write. If you serialize a model yourself, a minor release may add a property to the output.

## How this is enforced

The pinned API file fails a test on any change to the source-level surface. And every pack validates the package
against the previous release, on every target framework: a removed or changed public member, or a member that
exists on one framework only, fails the build. The one check that is still by hand is moving that baseline to the
newest release when a new one is prepared.
