namespace RimGovernor.Brain.Policy;

/// <summary>
/// The Empire title ladder, the permit catalog and the colonists' holdings, decoded from the native royalty
/// read. Every scalar the native read left absent is unknown.
/// </summary>
public sealed record RoyaltyFacts
{
    /// <summary>Every title by ascending seniority.</summary>
    public EquatableArray<RoyalRung> Ladder { get; init; }

    public IReadOnlyDictionary<string, RoyalPermit> Permits { get; init; } = new Dictionary<string, RoyalPermit>();

    /// <summary>Maps a colonist to its holdings, one per faction.</summary>
    public IReadOnlyDictionary<PawnId, EquatableArray<RoyalHolding>> Holders { get; init; } = new Dictionary<PawnId, EquatableArray<RoyalHolding>>();

    /// <summary>Maps a psycaster to its known psycasts.</summary>
    public IReadOnlyDictionary<PawnId, EquatableArray<Psycast>> Psycasts { get; init; } = new Dictionary<PawnId, EquatableArray<Psycast>>();

    /// <summary>Maps a psycaster to its psyfocus and neural heat at the read.</summary>
    public IReadOnlyDictionary<PawnId, PsycasterState> Casters { get; init; } = new Dictionary<PawnId, PsycasterState>();

    /// <summary>The colony's neuroformer stock by def. Neuroformers, Ceremonies and Thrones are the colony section's (WithColony), not the royalty read's.</summary>
    public IReadOnlyDictionary<string, Neuroformer> Neuroformers { get; init; } = new Dictionary<string, Neuroformer>();

    /// <summary>The pending bestowing ceremonies, by pawn.</summary>
    public EquatableArray<BestowingCeremony> Ceremonies { get; init; }

    /// <summary>The standing player thrones and their owners.</summary>
    public EquatableArray<RoyalThrone> Thrones { get; init; }

    /// <summary>f with the colony section's neuroformers, ceremonies and thrones, which the royalty read does not carry.</summary>
    public RoyaltyFacts WithColony(RoyaltyColony colony) =>
        this with { Neuroformers = colony.Neuroformers, Ceremonies = colony.Ceremonies, Thrones = colony.Thrones };
}

/// <summary>One standing throne: Owner is the colonist it is assigned to, empty when unassigned.</summary>
public sealed record RoyalThrone
{
    public string Id { get; init; } = "";

    public string Def { get; init; } = "";

    public PawnId Owner { get; init; }
}

/// <summary>
/// A psycaster's 0-1 psyfocus and neural heat against its ceiling at the read's tick; each is unknown when
/// the read left it absent (a pawn that needs no psyfocus has none).
/// </summary>
public sealed record PsycasterState
{
    public Fact<double> Psyfocus { get; init; }

    public Fact<double> Entropy { get; init; }

    public Fact<double> EntropyMax { get; init; }
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
/// One known psycast: Level is the psylink level that unlocks it, PsyfocusCost the 0-1 Psyfocus it spends,
/// Entropy the neural heat it adds and Target is None when native did not classify it.
/// </summary>
public sealed record Psycast
{
    public string Def { get; init; } = "";

    public Fact<int> Level { get; init; }

    public Fact<double> PsyfocusCost { get; init; }

    public Fact<double> Entropy { get; init; }

    public PsycastTarget Target { get; init; }

    public Fact<int> CooldownTicks { get; init; }

    /// <summary>The ticks left of the cooldown at the read.</summary>
    public Fact<int> CooldownRemaining { get; init; }
}

/// <summary>
/// The stock of one neuroformer def: the psylink neuroformer or a psycast neurotrainer (TeachesPsycast names
/// its ability). Held counts unforbidden stacks on the home maps; Craftable is an available recipe; Tradeable
/// is a trader selling it.
/// </summary>
public sealed record Neuroformer
{
    public string Def { get; init; } = "";

    public string TeachesPsycast { get; init; } = "";

    public Fact<int> Held { get; init; }

    public Fact<bool> Craftable { get; init; }

    public Fact<bool> Tradeable { get; init; }
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

    /// <summary>The title's bedroom area requirement: an absent one is none.</summary>
    public Fact<int> BedroomMinArea { get; init; }

    public Fact<int> BedroomMinImpressiveness { get; init; }

    public Fact<bool> BedroomFloored { get; init; }

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

    public Fact<double> CooldownDays { get; init; }

    /// <summary>The permit def's workerClass type name, empty when the read left it absent: what the permit does (PermitWorker* constants).</summary>
    public string Worker { get; init; } = "";
}

/// <summary>A colonist's standing with one faction. Title is empty for favor or permits without a current title.</summary>
public sealed record RoyalHolding
{
    public string FactionDef { get; init; } = "";

    public string Title { get; init; } = "";

    public Fact<int> Favor { get; init; }

    public Fact<int> PermitPoints { get; init; }

    public EquatableArray<string> Permits { get; init; }

    /// <summary>The native cooldown of each held permit, by permit def.</summary>
    public IReadOnlyDictionary<string, PermitCooldown> Cooldowns { get; init; } = new Dictionary<string, PermitCooldown>();
}

/// <summary>One held permit's native cooldown at the read's tick. LastUsedTick is unknown when the permit was never used; RemainingTicks is 0 when it can be used.</summary>
public sealed record PermitCooldown
{
    public Fact<int> LastUsedTick { get; init; }

    public Fact<int> RemainingTicks { get; init; }
}

/// <summary>The Royalty colony section: the neuroformer stock, the pending bestowing ceremonies and the standing thrones.</summary>
public sealed record RoyaltyColony
{
    public IReadOnlyDictionary<string, Neuroformer> Neuroformers { get; init; } = new Dictionary<string, Neuroformer>();

    public EquatableArray<BestowingCeremony> Ceremonies { get; init; }

    public EquatableArray<RoyalThrone> Thrones { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Whether the pawn's permit for the faction was used at or after tick since, from a royalty read taken
    /// after the attempt: the replay rule's way to resolve an uncertain ability receipt (an effective use is
    /// never re-sent). An unknown cooldown is false and the caller re-reads. A null facts is false.
    /// </summary>
    public static bool PermitUsedSince(this RoyaltyFacts? f, PawnId pawn, string faction, string permit, int since)
    {
        if (f is null || !f.Holders.TryGetValue(pawn, out var holdings))
        {
            return false;
        }
        foreach (var h in holdings)
        {
            if (h.FactionDef != faction)
            {
                continue;
            }
            if (h.Cooldowns.TryGetValue(permit, out var cooldown) && cooldown.LastUsedTick.TryGetValue(out var last) && last >= since)
            {
                return true;
            }
        }
        return false;
    }
}
