using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;

namespace RimGovernor.Brain.BuildingRuntime;

// The wire carries no def rows (Contracts/Observations.cs drops `defs`, `thing_defs`, `terrain_defs` and
// `game_constants`): the C# DefinitionCatalog is built over the game's own Def objects with the load's DefStatTable
// (Bridge/Catalog/Catalog.cs). The read here only has native prepare the load's stat table and race facts on the game
// thread; CatalogFactory, which the host installs, then builds the catalog from the game's Defs. Without a factory the
// catalog cannot be built and DefinitionCatalogAsync refuses. Catalog caching is by load token.
public sealed partial class FacadeNativeClient
{
    private readonly object _catalogGate = new();
    private BridgeCatalog? _catalogHeld;
    private string _catalogHeldLoadToken = "";

    /// <summary>
    /// Builds the C# <see cref="BridgeCatalog"/> of a load from the game's Def objects, its derived facts and stat
    /// environment. Installed by the host composition; it must be set before
    /// the first catalog read.
    /// </summary>
    internal Func<PbCommon.Identity, BridgeCatalog>? CatalogFactory { get; set; }

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

        // Native prepares the load's stat table and race facts on the game thread; a refused or unavailable prepare throws.
        _ = await Facade.ReadDefinitionCatalogAsync(new PbObservations.DefinitionCatalogRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } }, cancellationToken).ConfigureAwait(false);
        var factory = CatalogFactory ?? throw new InvalidOperationException("no definition catalog factory is installed: the game's Defs are not on the wire");
        var catalog = factory(identity);
        lock (_catalogGate)
        {
            _catalogHeld = catalog;
            _catalogHeldLoadToken = load;
        }

        return catalog;
    }
}
