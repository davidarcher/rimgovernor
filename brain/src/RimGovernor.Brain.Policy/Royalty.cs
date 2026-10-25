namespace RimGovernor.Brain.Policy;

/// <summary>
/// The colony's royalty read, by the colony section's Royalty gate: Inactive without the expansion (nothing is read,
/// no royalty Need is derived and no permit or psycast is called), Active with the read.
/// </summary>
public abstract record RoyaltyState
{
    private protected RoyaltyState() { }

    /// <summary>The Royalty expansion is not active.</summary>
    public sealed record Inactive : RoyaltyState;

    /// <summary>The Royalty expansion is active: the ladder, permits, holdings, psycasts and the colony section.</summary>
    public sealed record Active(RoyaltyFacts Facts) : RoyaltyState;
}

/// <summary>
/// The Empire title ladder and the permit catalog (def rows), the colonists' holdings, psycasts and psylinks (their
/// pawn rows' royalty blocks) and the colony section's neuroformers, ceremonies and thrones.
/// </summary>
public sealed record RoyaltyFacts
{
    /// <summary>Every title by ascending seniority.</summary>
    public EquatableArray<RoyalRung> Ladder { get; init; }

    public IReadOnlyDictionary<string, RoyalPermit> Permits { get; init; } = new Dictionary<string, RoyalPermit>();

    /// <summary>Maps a colonist to its holdings, one per faction.</summary>
    public IReadOnlyDictionary<PawnId, EquatableArray<PawnRoyalHolding>> Holders { get; init; } = new Dictionary<PawnId, EquatableArray<PawnRoyalHolding>>();

    /// <summary>Maps a psycaster to its known psycasts.</summary>
    public IReadOnlyDictionary<PawnId, EquatableArray<KnownPsycast>> Psycasts { get; init; } = new Dictionary<PawnId, EquatableArray<KnownPsycast>>();

    /// <summary>Maps a psylinked colonist to its psyfocus and neural heat at the read.</summary>
    public IReadOnlyDictionary<PawnId, PawnPsylink> Casters { get; init; } = new Dictionary<PawnId, PawnPsylink>();

    /// <summary>The colony's neuroformer stock by def. Neuroformers, Ceremonies and Thrones are the colony section's (WithColony), not the pawn rows'.</summary>
    public IReadOnlyDictionary<string, Neuroformer> Neuroformers { get; init; } = new Dictionary<string, Neuroformer>();

    /// <summary>The pending bestowing ceremonies, by pawn.</summary>
    public EquatableArray<BestowingCeremony> Ceremonies { get; init; }

    /// <summary>The standing player thrones and their owners.</summary>
    public EquatableArray<RoyalThrone> Thrones { get; init; }

    /// <summary>f with the colony section's neuroformers, ceremonies and thrones, which the pawn rows do not carry.</summary>
    public RoyaltyFacts WithColony(IReadOnlyDictionary<string, Neuroformer> neuroformers, RimGovernor.Contracts.Wire.Observations.RoyaltyColonyFacts colony) =>
        this with { Neuroformers = neuroformers, Ceremonies = colony.Ceremonies, Thrones = colony.Thrones };
}

/// <summary>The functions over the pawn rows' royalty blocks.</summary>
public static class RoyalHoldingRules
{
    /// <summary>Whether the holding's permit is off cooldown; a permit the holding does not hold is not ready.</summary>
    public static bool PermitReady(PawnRoyalHolding h, string permit) =>
        h.PermitCooldowns.Items.Any(c => c.Permit == permit && c.CooldownRemainingTicks == 0);
}

/// <summary>What a psycast aims at.</summary>
public enum PsycastTarget
{
    [WireName("")]
    None = 0,

    [WireName("self")]
    Self,

    [WireName("pawn")]
    Pawn,

    [WireName("thing")]
    Thing,

    [WireName("cell")]
    Cell,
}

/// <summary>
/// One known psycast's ability row: PsyfocusCost is the 0-1 Psyfocus it spends, Entropy the neural heat it adds,
/// CooldownTicks its longest cooldown and Target is None when the ability's targeting fits no kind.
/// </summary>
public sealed record Psycast
{
    public required string Def { get; init; }

    public required double PsyfocusCost { get; init; }

    public required double Entropy { get; init; }

    public required PsycastTarget Target { get; init; }

    public required int CooldownTicks { get; init; }
}

/// <summary>A psycast a pawn knows: its def row and the ticks left of its cooldown at the read.</summary>
public sealed record KnownPsycast(Psycast Psycast, RimGovernor.Contracts.Wire.Observations.PawnPsycast Observed)
{
    public int CooldownRemaining => Observed.CooldownRemainingTicks;
}

/// <summary>
/// The stock of one neuroformer def: the psylink neuroformer or a psycast neurotrainer. Held counts
/// unforbidden stacks on the home maps; Craftable is an available recipe; Tradeable is a trader selling it.
/// </summary>
public sealed record Neuroformer
{
    public required RimGovernor.Contracts.Wire.Observations.NeuroformerStock Observed { get; init; }

    public int Held => Observed.Held;

    public bool Craftable => Observed.Craftable;

    public required bool Tradeable { get; init; }
}

/// <summary>
/// One title on the ladder (a RoyalTitleDef row); FavorNeeded is the favor that earns it. Throne is the title's
/// throne-room requirement (RoyalTitleDef.throneRoomRequirements, bridge ThroneRequirements): a title that asks
/// for no throne holds a requirement with no Things.
/// </summary>
public sealed record RoyalRung
{
    public required string Title { get; init; }

    public required int Seniority { get; init; }

    public required int FavorNeeded { get; init; }

    public required ThroneRequirements Throne { get; init; }

    /// <summary>The title's bedroom minimum impressiveness; zero when the title sets none.</summary>
    public required int BedroomMinImpressiveness { get; init; }

    /// <summary>The title's bedroom furniture; none needs no furniture.</summary>
    public required EquatableArray<BedroomThing> BedroomThings { get; init; }
}

/// <summary>
/// One permit definition (a RoyalTitlePermitDef row). Aid is the royal aid a holder calls with it, null for a
/// passive permit; MinTitle is the title it asks for, null when it asks for none.
/// </summary>
public sealed record RoyalPermit
{
    public required string Name { get; init; }

    public required string? MinTitle { get; init; }

    public required int PermitPoints { get; init; }

    public required RoyalAid? Aid { get; init; }

    /// <summary>The permit def's workerClass type name (the base RoyalTitlePermitWorker for a permit with no worker of its own): what the permit does (PermitWorker* constants).</summary>
    public required string Worker { get; init; }
}

/// <summary>The royal aid an acting permit calls (RoyalTitlePermitDef.royalAid): the favor a call spends.</summary>
public sealed record RoyalAid(int FavorCost);
