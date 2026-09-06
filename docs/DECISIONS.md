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
