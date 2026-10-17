using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;

namespace RimGovernor.Brain.BuildingRuntime;

//
// What this client can and cannot do: the wire contract carries no def rows (thing_defs, terrain_defs, defs;
// Contracts/Observations.cs drops `defs`, `thing_defs`, `terrain_defs` and
// `game_constants`), and the C# DefinitionCatalog is built over the game's own Def objects with the load's DefStatTable
// (Bridge/Catalog/Catalog.cs). So the read here hands the native catalog record (class chains, derived facts, stat
// environment, thing facts) to CatalogFactory, which the lead installs and which builds the C# DefinitionCatalog from
// the game's Defs. Without a factory the catalog cannot be built and DefinitionCatalogAsync refuses. Catalog caching is
// by load token (there is no fingerprint reuse or disk cache of wire data: the
// rows are never on the wire).
public sealed partial class FacadeNativeClient
{
    private readonly object _catalogGate = new();
    private BridgeCatalog? _catalogHeld;
    private string _catalogHeldLoadToken = "";

    /// <summary>
    /// Builds the C# <see cref="BridgeCatalog"/> of a load from its native catalog record (derived facts, stat
    /// environment, thing facts) and the game's Def objects. Installed by the host composition; it must be set before
    /// the first catalog read.
    /// </summary>
    internal Func<PbObservations.DefinitionCatalog, PbCommon.Identity, BridgeCatalog>? CatalogFactory { get; set; }

    /// <summary>
    /// The catalog of identity's load, read the first time the load token is
    /// seen and kept until the token changes.
    /// </summary>
    internal async Task<BridgeCatalog> DefinitionCatalogAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var load = identity.LoadToken ?? "";
        lock (_catalogGate)
        {
            if (_catalogHeld is not null && string.Equals(_catalogHeldLoadToken, load, StringComparison.Ordinal))
            {
                return _catalogHeld;
            }
        }

        var wire = await CatalogReadWireAsync(identity, cancellationToken).ConfigureAwait(false);
        var factory = CatalogFactory ?? throw new InvalidOperationException("no definition catalog factory is installed: the game's Defs are not on the wire");
        var catalog = factory(wire, identity);
        lock (_catalogGate)
        {
            _catalogHeld = catalog;
            _catalogHeldLoadToken = load;
        }

        return catalog;
    }

    /// <summary>The catalog reply's observed payload, read with no held fingerprint.</summary>
    private async Task<PbObservations.DefinitionCatalog> CatalogReadWireAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.DefinitionCatalogRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var reply = await Facade.ReadDefinitionCatalogAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        return outcome.Observed!;
    }
}
