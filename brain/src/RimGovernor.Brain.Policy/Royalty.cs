namespace RimGovernor.Brain.Policy;

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
/// One known psycast: PsyfocusCost is the 0-1 Psyfocus it spends,
/// Entropy the neural heat it adds and Target is None when native did not classify it.
/// </summary>
public sealed record Psycast
{
    public string Def { get; init; } = "";

    public Fact<double> PsyfocusCost { get; init; }

    public Fact<double> Entropy { get; init; }

    public PsycastTarget Target { get; init; }

    public Fact<int> CooldownTicks { get; init; }
}

/// <summary>A psycast a pawn knows: its def row and the ticks left of its cooldown at the read.</summary>
public sealed record KnownPsycast(Psycast Psycast, int CooldownRemaining);

/// <summary>
/// The stock of one neuroformer def: the psylink neuroformer or a psycast neurotrainer. Held counts
/// unforbidden stacks on the home maps; Craftable is an available recipe; Tradeable is a trader selling it.
/// </summary>
public sealed record Neuroformer
{
    public required int Held { get; init; }

    public required bool Craftable { get; init; }

    public required bool Tradeable { get; init; }
}

/// <summary>
/// One title on the ladder; FavorNeeded is the favor that earns it. Throne is the title's throne-room
/// requirement read from the def mirror (RoyalTitleDef.throneRoomRequirements, bridge ThroneRequirements):
/// unknown until the mirror is read, and a title that asks for no throne holds a requirement with no Things.
/// </summary>
public sealed record RoyalRung
{
    public string Title { get; init; } = "";

    public Fact<int> Seniority { get; init; }

    public Fact<int> FavorNeeded { get; init; }

    public Fact<ThroneRequirements> Throne { get; init; }

    public Fact<int> BedroomMinImpressiveness { get; init; }

    /// <summary>The title's bedroom furniture; none needs no furniture.</summary>
    public EquatableArray<BedroomThing> BedroomThings { get; init; }
}

/// <summary>
/// One permit definition. Acts marks a permit the holder calls (rather than a passive one); FavorCost is then
/// the favor a call spends and unknown for a passive permit.
/// </summary>
public sealed record RoyalPermit
{
    public string Name { get; init; } = "";

    public Fact<string> MinTitle { get; init; }

    public Fact<int> PermitPoints { get; init; }

    public Fact<bool> Acts { get; init; }

    public Fact<int> FavorCost { get; init; }

    /// <summary>The permit def's workerClass type name, empty when the read left it absent: what the permit does (PermitWorker* constants).</summary>
    public string Worker { get; init; } = "";
}
