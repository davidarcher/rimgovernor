using RimGovernor.Brain.Bridge.Contracts;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using BridgeCatalogDerived = RimGovernor.Brain.Bridge.CatalogDerived;
using BridgeRaceGameFacts = RimGovernor.Brain.Bridge.RaceGameFacts;

namespace RimGovernor.Brain.Host;

// Go catalog.go (Client.DefinitionCatalog, RecordDefinitionCatalog, readCatalogWire, DecodeDefinitionCatalog,
// decodeStatic, validateDerived, classBases, forLoad's held-catalog reuse) and catalog_stats.go (decodeStatEnv).
//
// What this port can and cannot do: Go decoded the wire catalog into def rows (thing_defs, terrain_defs, defs). The
// wire contract no longer carries them (Contracts/Observations.cs drops `defs`, `thing_defs`, `terrain_defs` and
// `game_constants`), and the C# DefinitionCatalog is built over the game's own Def objects with an IStatSource
// (Bridge/Catalog/Catalog.cs). So the read here validates everything the wire still carries (context, class chains,
// derived facts, stat environment, thing facts) and hands the validated wire catalog to CatalogFactory, which the lead
// installs and which builds the C# DefinitionCatalog from the game's Defs. Without a factory the catalog cannot be
// built and DefinitionCatalogAsync refuses. Catalog caching is by load token, as Go's catalogCache (the fingerprint
// reuse and catalog_disk.go cache of wire data are not ported: the rows are never on the wire).
internal sealed partial class FacadeNativeClient
{
    private readonly object _catalogGate = new();
    private BridgeCatalog? _catalogHeld;
    private string _catalogHeldLoadToken = "";

    /// <summary>
    /// Builds the C# <see cref="BridgeCatalog"/> of a load from its validated wire catalog (derived facts, stat
    /// environment, thing facts) and the game's Def objects. Installed by the host composition; it must be set before
    /// the first catalog read.
    /// </summary>
    internal Func<PbObservations.DefinitionCatalog, PbCommon.Identity, BridgeCatalog>? CatalogFactory { get; set; }

    /// <summary>
    /// Go <c>Client.DefinitionCatalog</c>: the catalog of identity's load, read the first time the load token is
    /// seen and kept until the token changes.
    /// </summary>
    internal async Task<BridgeCatalog> DefinitionCatalogAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
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
        CatalogCheckWire(wire, identity);
        var catalog = factory(wire, identity);
        lock (_catalogGate)
        {
            _catalogHeld = catalog;
            _catalogHeldLoadToken = load;
        }

        return catalog;
    }

    /// <summary>
    /// Go <c>Client.RecordDefinitionCatalog</c>: the wire catalog of identity's load, read fresh (the held catalog is
    /// not consulted) and checked.
    /// </summary>
    internal async Task<PbObservations.DefinitionCatalog> RecordDefinitionCatalogAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var wire = await CatalogReadWireAsync(identity, cancellationToken).ConfigureAwait(false);
        CatalogCheckWire(wire, identity);
        return wire;
    }

    /// <summary>Go <c>readCatalogWire</c> with no held fingerprint: the catalog reply's observed payload.</summary>
    private async Task<PbObservations.DefinitionCatalog> CatalogReadWireAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.DefinitionCatalogRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var reply = await Facade.ReadDefinitionCatalogAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply?.Outcome;
        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        return outcome?.Observed ?? throw Contract("missing definition catalog outcome");
    }

    /// <summary>
    /// Go <c>DecodeDefinitionCatalog</c> and <c>decodeStatic</c> over what the wire still carries: the context, the class
    /// chains, the derived facts, the stat environment and the thing facts.
    /// </summary>
    private static void CatalogCheckWire(PbObservations.DefinitionCatalog? v, PbCommon.Identity identity)
    {
        if (v is null || !G3ContextValid(v.Context) || !SameIdentity(v.Context!.Identity, identity))
        {
            throw Contract("invalid definition catalog context");
        }

        CatalogClassChains(v.ClassChains);
        CatalogDerivedOf(v.Derived);
        CatalogCheckStatEnv(v.StatEnv);
        CatalogRaceFactsOf(v.ThingFacts);
    }

    /// <summary>Go <c>classBases</c>: an unnamed or repeated chain, or one with an empty base name, is a contract violation.</summary>
    internal static void CatalogClassChains(IEnumerable<PbObservations.ClassChain> chains)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chain in chains)
        {
            if (chain is null || string.IsNullOrEmpty(chain.Name) || chain.Bases.Any(string.IsNullOrEmpty))
            {
                throw Contract("invalid catalog class chain");
            }

            if (!names.Add(chain.Name))
            {
                throw Contract($"duplicate catalog class chain {chain.Name}");
            }
        }
    }

    /// <summary>Go <c>validateDerived</c>: refuses an absent derived block, an empty def name and a non-finite or non-positive rot rate; returns the Bridge form.</summary>
    internal static BridgeCatalogDerived CatalogDerivedOf(PbObservations.CatalogDerived? v)
    {
        if (v is null)
        {
            throw Contract("catalog carries no derived facts");
        }

        if (string.IsNullOrEmpty(v.CurrencyDef))
        {
            throw Contract("catalog derived currency_def is empty");
        }

        if (string.IsNullOrEmpty(v.WortDef))
        {
            throw Contract("catalog derived wort_def is empty");
        }

        var rate = (double)v.FullRotRateC;
        if (!double.IsFinite(rate) || rate <= 0)
        {
            throw Contract($"catalog derived full_rot_rate_c is {rate}");
        }

        return new BridgeCatalogDerived(v.CurrencyDef, v.WortDef, v.FullRotRateC);
    }

    /// <summary>Go <c>decodeStatEnv</c>: refuses an empty mod name and a repeated or invalid scenario factor or difficulty setting. An absent environment is fine.</summary>
    internal static void CatalogCheckStatEnv(PbObservations.StatEnv? v)
    {
        if (v is null)
        {
            return;
        }

        foreach (var mod in v.ActiveMods)
        {
            if (mod.Length == 0)
            {
                throw Contract("catalog stat environment names an empty mod");
            }
        }

        var factors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var factor in v.ScenarioFactors)
        {
            if (!factors.Add(factor.Stat) || !IsValidId(factor.Stat) || !float.IsFinite(factor.Factor))
            {
                throw Contract($"invalid or repeated catalog scenario stat factor \"{factor.Stat}\"");
            }
        }

        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in v.DifficultyFlags)
        {
            if (!flags.Add(flag.Name) || !IsValidId(flag.Name))
            {
                throw Contract($"invalid or repeated catalog difficulty setting \"{flag.Name}\"");
            }
        }
    }

    /// <summary>
    /// Go <c>decodeThingFacts</c> (the part that needs no def rows): each thing facts row names a def once. Returns the
    /// race facts the C# catalog takes (<c>raceFacts</c> of its constructor) by def name.
    /// </summary>
    internal static IReadOnlyDictionary<string, BridgeRaceGameFacts> CatalogRaceFactsOf(IEnumerable<PbObservations.ThingDefFacts> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var races = new Dictionary<string, BridgeRaceGameFacts>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var name = row.DefName;
            if (!seen.Add(name))
            {
                throw Contract($"catalog thing facts for repeated def \"{name}\"");
            }

            if (row.Race is { } race)
            {
                races.Add(name, new BridgeRaceGameFacts([.. race.EdibleDefs], race.TamenessCanDecay, race.TamenessDecayPeriodTicks, race.TameChanceFactor, race.AdultFeedPerDay));
            }
        }

        return races;
    }

    /// <summary>The Go idiom <c>ValidateContext(ctx) == nil</c>: whether the context validates.</summary>
    private static bool G3ContextValid(PbCommon.ObservationContext? context)
    {
        try
        {
            ValidateContext(context);
            return true;
        }
        catch (BridgeContractException)
        {
            return false;
        }
    }
}
