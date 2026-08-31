namespace TcgDex;

/// <summary>
/// A regional TCGdex API server node. Pass one to
/// <see cref="TcgDexOptions.UseMirror(TcgDexMirror)"/> to route requests to a
/// specific node instead of the default global host.
/// </summary>
/// <remarks>
/// <para>
/// The nodes serve the same catalogue, so choosing one is a latency and
/// availability decision, not a data one — a consumer nearer <see cref="As1"/>
/// than the EU nodes, or wanting to fail over when the default host is
/// unreachable, points here and every request follows.
/// </para>
/// <para>
/// One caveat: <b>pricing</b> is synced per node on its own schedule, so it can
/// differ briefly between nodes after a restart. Card data and asset URLs are
/// consistent across nodes (asset URLs are fixed at build time). The live list
/// of nodes and their health is at <see href="https://status.tcgdex.dev"/>; for
/// a node not listed here, or a local test server, set
/// <see cref="TcgDexOptions.BaseAddress"/> directly.
/// </para>
/// </remarks>
public enum TcgDexMirror
{
    /// <summary>EU1 — the global node (<c>api.eu1.tcgdex.net</c>).</summary>
    Eu1,

    /// <summary>EU2 — France (<c>api.eu2.tcgdex.net</c>).</summary>
    Eu2,

    /// <summary>EU3 — Germany (<c>api.eu3.tcgdex.net</c>).</summary>
    Eu3,

    /// <summary>NA1 — Canada (<c>api.na1.tcgdex.net</c>).</summary>
    Na1,

    /// <summary>NA2 — North and South America (<c>api.na2.tcgdex.net</c>).</summary>
    Na2,

    /// <summary>AS1 — Asia and Oceania (<c>api.as1.tcgdex.net</c>).</summary>
    As1,
}
