using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The shapes a player goal can take (#3489). Closed: a new kind is code. The vocabulary is wider than what the
/// brain pursues; a kind with no method still validates, persists and renders.
/// </summary>
public enum GoalKind
{
    /// <summary>A research project finished; <see cref="GoalRecord.Target"/> is the project def.</summary>
    ResearchProject,

    /// <summary>A research tier reached; <see cref="GoalRecord.Target"/> is the tier name.</summary>
    ResearchTier,

    /// <summary>A stock of a thing held; <see cref="GoalRecord.Target"/> is the thing def, <see cref="GoalRecord.Count"/> the stock.</summary>
    StockCount,

    /// <summary>A number of a building built; <see cref="GoalRecord.Target"/> is the building def.</summary>
    BuiltCount,

    /// <summary>Colonists wearing a garment; <see cref="GoalRecord.Target"/> is the garment def, <see cref="GoalRecord.Qualifier"/> the stuff (empty for any), <see cref="GoalRecord.Count"/> the colonists.</summary>
    WornCount,

    /// <summary>A colonist head count.</summary>
    Colonists,

    /// <summary>A number of animals; <see cref="GoalRecord.Target"/> is the race def.</summary>
    Animals,

    /// <summary>A room of a role; <see cref="GoalRecord.Target"/> is the role.</summary>
    RoomRole,

    /// <summary>A power surplus; <see cref="GoalRecord.Count"/> is the watts.</summary>
    PowerSurplus,

    /// <summary>A precept adopted; <see cref="GoalRecord.Target"/> is the precept def.</summary>
    Precept,

    /// <summary>A meme adopted; <see cref="GoalRecord.Target"/> is the meme def.</summary>
    Meme,

    /// <summary>An animal venerated; <see cref="GoalRecord.Target"/> is the race def.</summary>
    VeneratedAnimal,

    /// <summary>A number of believers; <see cref="GoalRecord.Count"/> is the believers, 0 for every colonist.</summary>
    Believers,

    /// <summary>A named prisoner dealt with; <see cref="GoalRecord.Target"/> is the pawn id.</summary>
    NamedPrisoner,

    /// <summary>A named quest completed; <see cref="GoalRecord.Target"/> is the quest id.</summary>
    NamedQuest,

    /// <summary>A ritual held; <see cref="GoalRecord.Target"/> is the ritual def.</summary>
    Ritual,

    /// <summary>A skill level; <see cref="GoalRecord.Target"/> is the pawn id, <see cref="GoalRecord.Qualifier"/> the skill def, <see cref="GoalRecord.Count"/> the level.</summary>
    Skill,

    /// <summary>A health recipe applied; <see cref="GoalRecord.Target"/> is the pawn id, <see cref="GoalRecord.Qualifier"/> the recipe def.</summary>
    Health,

    /// <summary>A territory secured; <see cref="GoalRecord.Target"/> is <c>roofed</c> or <c>walled</c>.</summary>
    Territory,

    /// <summary>A game ending; <see cref="GoalRecord.Target"/> is the ending.</summary>
    Ending,

    /// <summary>A caravan sent.</summary>
    Caravan,
}

public enum Importance
{
    Low,
    Normal,
    High,
}

public enum RiskProfile
{
    Cautious,
    Normal,
    Bold,
}

public enum Pace
{
    Wellbeing,
    Balanced,
    Development,
}

public enum ForbiddenMethod
{
    HumanButchery,
    OrganHarvest,
    Drugs,
    Slavery,
    InsectMeat,
    HumanLeather,
}

public enum DietOverride
{
    Cannibal,
    InsectMeat,
    Vegetarian,
}

public enum ApparelPreference
{
    Dressed,
    Nude,
    ArmorOnly,
}

public enum DrugPolicyPreference
{
    None,
    SocialOnly,
    Medical,
}

/// <summary>
/// One goal the player set. <see cref="Importance"/> and <see cref="DeadlineDay"/> are the goal's own; inside a
/// theme, <see cref="ImportanceOverride"/> and <see cref="DeadlineOverride"/> replace the theme's for this member.
/// </summary>
public sealed record GoalRecord
{
    /// <summary>The longest <see cref="Target"/> or <see cref="Qualifier"/>.</summary>
    public const int MaxText = 128;

    public int Id { get; init; }

    public GoalKind Kind { get; init; }

    /// <summary>The def name, project, role, precept or meme def, pawn id or quest id the kind names; empty when it names none.</summary>
    public string Target { get; init; } = "";

    /// <summary>The second name a kind needs (stuff, skill, recipe); empty otherwise.</summary>
    public string Qualifier { get; init; } = "";

    public int Count { get; init; }

    public Importance Importance { get; init; } = Importance.Normal;

    /// <summary>The game day the goal is due by; null for none.</summary>
    public int? DeadlineDay { get; init; }

    /// <summary>The theme this goal is a member of; null for a standalone goal.</summary>
    public int? Theme { get; init; }

    public Importance? ImportanceOverride { get; init; }

    public int? DeadlineOverride { get; init; }

    /// <summary>The map a site-bound goal belongs to; null for the colony.</summary>
    public MapId? SiteMap { get; init; }

    public void Validate()
    {
        DomainException.Require(Id > 0, "invalid goal id");
        DomainException.Require(Wire.IsDefined(Kind) && Wire.IsDefined(Importance), "invalid goal kind or importance");
        DomainException.Require(Target.Length <= MaxText && Qualifier.Length <= MaxText, "goal text too long");
        DomainException.Require(Count >= 0, "negative goal count");
        DomainException.Require(DeadlineDay is null or > 0, "goal deadline must be positive");
        DomainException.Require(DeadlineOverride is null or > 0, "goal deadline override must be positive");
        DomainException.Require(ImportanceOverride is not { } o || Wire.IsDefined(o), "invalid goal importance override");
        DomainException.Require(SiteMap is not { } m || m.Value >= 0, "invalid goal map");
    }
}

/// <summary>A named group of goals. A preset is referenced by id, never copied; the player's removals are kept as keys.</summary>
public sealed record ThemeRecord
{
    public int Id { get; init; }

    public string Name { get; init; } = "";

    /// <summary>The preset this theme follows; empty for none.</summary>
    public string Preset { get; init; } = "";

    public Importance Importance { get; init; } = Importance.Normal;

    public int? DeadlineDay { get; init; }

    /// <summary>Goal ids, ascending.</summary>
    public IReadOnlyList<int> Members { get; init; } = [];

    /// <summary>Preset member keys the player removed, ordinal ascending.</summary>
    public IReadOnlyList<string> RemovedPresetMembers { get; init; } = [];

    public void Validate()
    {
        DomainException.Require(Id > 0, "invalid theme id");
        DomainException.Require(Name.Length is > 0 and <= GoalRecord.MaxText && Preset.Length <= GoalRecord.MaxText, "invalid theme name or preset");
        DomainException.Require(Wire.IsDefined(Importance), "invalid theme importance");
        DomainException.Require(DeadlineDay is null or > 0, "theme deadline must be positive");
        DomainException.Require(Members.Zip(Members.Skip(1)).All(p => p.First < p.Second), "theme members not ascending");
        DomainException.Require(PreferencesRecord.OrdinalAscending(RemovedPresetMembers), "invalid removed preset members");
    }
}

/// <summary>The player's standing preferences; owning policies read them as facts.</summary>
public sealed record PreferencesRecord
{
    public RiskProfile RiskProfile { get; init; } = RiskProfile.Normal;

    public Pace Pace { get; init; } = Pace.Balanced;

    /// <summary>The colony wealth the player does not want to exceed; null for none.</summary>
    public int? WealthCap { get; init; }

    /// <summary>Methods the colony does not use, ascending by value.</summary>
    public IReadOnlyList<ForbiddenMethod> Forbidden { get; init; } = [];

    /// <summary>Thing ids left alone, ordinal ascending.</summary>
    public IReadOnlyList<string> ProtectedThings { get; init; } = [];

    /// <summary>Pawn ids left alone, ordinal ascending.</summary>
    public IReadOnlyList<string> ProtectedPawns { get; init; } = [];

    /// <summary>Diet overrides, ascending by value.</summary>
    public IReadOnlyList<DietOverride> Diet { get; init; } = [];

    public ApparelPreference? Apparel { get; init; }

    public DrugPolicyPreference? DrugPolicy { get; init; }

    /// <summary>Whether the colony does not use <paramref name="method"/>.</summary>
    public bool Forbids(ForbiddenMethod method) => Forbidden.Contains(method);

    /// <summary>Whether <paramref name="thingId"/> is a thing the player left alone.</summary>
    public bool Protects(string thingId) => ProtectedThings.Contains(thingId, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="pawnId"/> is a pawn the player left alone.</summary>
    public bool ProtectsPawn(string pawnId) => ProtectedPawns.Contains(pawnId, StringComparer.Ordinal);

    /// <summary>Whether every preference is at its default.</summary>
    public bool IsDefault =>
        RiskProfile == RiskProfile.Normal && Pace == Pace.Balanced && WealthCap is null && Apparel is null && DrugPolicy is null
        && Forbidden.Count == 0 && ProtectedThings.Count == 0 && ProtectedPawns.Count == 0 && Diet.Count == 0;

    public void Validate()
    {
        DomainException.Require(Wire.IsDefined(RiskProfile) && Wire.IsDefined(Pace), "invalid risk profile or pace");
        DomainException.Require(WealthCap is null or >= 0, "negative wealth cap");
        DomainException.Require(Forbidden.All(Wire.IsDefined) && Ascending(Forbidden.Select(f => (int)f)), "invalid forbidden methods");
        DomainException.Require(Diet.All(Wire.IsDefined) && Ascending(Diet.Select(d => (int)d)), "invalid diet overrides");
        DomainException.Require(Apparel is not { } a || Wire.IsDefined(a), "invalid apparel preference");
        DomainException.Require(DrugPolicy is not { } d || Wire.IsDefined(d), "invalid drug policy");
        DomainException.Require(OrdinalAscending(ProtectedThings) && OrdinalAscending(ProtectedPawns), "invalid protected ids");
    }

    private static bool Ascending(IEnumerable<int> values)
    {
        var list = values.ToList();
        return list.Zip(list.Skip(1)).All(p => p.First < p.Second);
    }

    internal static bool OrdinalAscending(IReadOnlyList<string> ids) =>
        ids.All(i => i.Length is > 0 and <= GoalRecord.MaxText) && ids.Zip(ids.Skip(1)).All(p => string.CompareOrdinal(p.First, p.Second) < 0);
}
