namespace RimGovernor.Brain.Policy;

// The Ideology facts. Static defs are a view over the definition catalog's mirrored rows (IdeologyDefs,
// fixed for a load); the primary ideoligion's current state comes from the frame's ideology section
// (IdeoligionFacts). Both are read from the game's defs: no def name is listed here. Unknown stays unknown:
// Facts.Ideology is unknown when the frame carries no ideology section (no Ideology, no primary ideoligion,
// or the section failed to read).

/// <summary>What a precept comp does, by the class of the comp.</summary>
public enum PreceptEffectKind
{
    SelfTookAction,
    WitnessedAction,
    Situational,
    Bed,
    Unwilling,
    Apparel,
    MentalBreak,
    DevelopmentPoints,
    GoodwillSituation,
}

/// <summary>
/// One PreceptComp of a precept def. A thought effect carries the mood each stage of its thought gives
/// (negative penalises); an event effect the history event it reacts to; an unwilling effect the event the
/// member refuses and the traits and hediffs that cancel the refusal.
/// </summary>
public sealed record PreceptEffect
{
    public PreceptEffectKind Kind { get; init; }

    public string HistoryEvent { get; init; } = string.Empty;

    public IReadOnlyList<double> StageMoods { get; init; } = [];

    public string Thought { get; init; } = string.Empty;

    public bool OnlyForNonSlaves { get; init; }

    public IReadOnlyList<string> NullifyingTraits { get; init; } = [];

    public IReadOnlyList<string> NullifyingHediffs { get; init; } = [];

    public Fact<double> Chance { get; init; }

    /// <summary>Reports a thought effect with a stage that costs mood.</summary>
    public bool Penalises() => StageMoods.Any(mood => mood < 0);

    /// <summary>Reports a thought effect with a stage that gives mood.</summary>
    public bool Approves() => StageMoods.Any(mood => mood > 0);

    /// <summary>Reports an effect that makes members refuse an action.</summary>
    public bool Forbids() => Kind == PreceptEffectKind.Unwilling;
}

/// <summary>One PreceptDef other than a role's, reduced to what planners read: its comps as effects. Name is its degree of its issue.</summary>
public sealed record PreceptDef(string Name, IReadOnlyList<PreceptEffect> Effects);

/// <summary>A role requirement's skill minimum.</summary>
public readonly record struct SkillRequirement(string Skill, int MinLevel);

/// <summary>One condition on a role's holder: for a skill requirement, the skills of which one must meet its minimum.</summary>
public sealed record RoleRequirement(IReadOnlyList<SkillRequirement> Skills);

/// <summary>One role precept def: how many pawns hold it and what it asks of them.</summary>
public sealed record RoleDef(string Name, int MaxCount, IReadOnlyList<RoleRequirement> Requirements);

/// <summary>A ritual behavior's role slot.</summary>
public sealed record RitualRoleSlot(string Id, string Precept, int MaxCount, bool Required);

/// <summary>
/// One ritual pattern: its cadence in days, whether it may start freely, the buildings its obligation target
/// filter accepts and its role slots.
/// </summary>
public sealed record RitualDef
{
    public string Name { get; init; } = string.Empty;

    public double IntervalDaysMin { get; init; }

    public IReadOnlyList<string> RequiredBuildings { get; init; } = [];

    public IReadOnlyList<RitualRoleSlot> Roles { get; init; } = [];

    public bool CanStartAnytime { get; init; }

    public bool AlwaysStartAnytime { get; init; }
}

/// <summary>The Ideology defs of one load, by def name: a view over the catalog's mirrored rows.</summary>
public sealed record IdeologyDefs
{
    public IReadOnlyDictionary<string, PreceptDef> Precepts { get; init; } = new Dictionary<string, PreceptDef>();

    public IReadOnlyDictionary<string, RoleDef> Roles { get; init; } = new Dictionary<string, RoleDef>();

    public IReadOnlyDictionary<string, RitualDef> Rituals { get; init; } = new Dictionary<string, RitualDef>();
}

/// <summary>A precept in force; role, ritual and building precepts are held apart.</summary>
public sealed record HeldPrecept(string Id, string Def);

/// <summary>A role precept: whether enough believers make it active and the pawns holding it.</summary>
public sealed record HeldRole(string Id, string Def, bool Active, IReadOnlyList<PawnId> Pawns);

/// <summary>
/// A ritual precept. LastFinishedTick is the game's Precept_Ritual.lastFinishedTick as read; no sentinel is
/// interpreted here. Running is whether a lord job of the precept is running now.
/// </summary>
public sealed record HeldRitual
{
    public string Id { get; init; } = string.Empty;

    public string Def { get; init; } = string.Empty;

    public string Pattern { get; init; } = string.Empty;

    public long LastFinishedTick { get; init; }

    public int ActiveObligations { get; init; }

    public bool RepeatPenaltyActive { get; init; }

    public bool Running { get; init; }
}

/// <summary>A building precept and the ThingDef it takes.</summary>
public sealed record HeldBuilding(string Id, string Def, string Building);

/// <summary>The player faction's primary ideoligion as it stands.</summary>
public sealed record IdeoligionFacts
{
    public string IdeoId { get; init; } = string.Empty;

    public IReadOnlyList<string> Memes { get; init; } = [];

    public IReadOnlyList<HeldPrecept> Precepts { get; init; } = [];

    public IReadOnlyList<HeldRole> Roles { get; init; } = [];

    public IReadOnlyList<HeldRitual> Rituals { get; init; } = [];

    public IReadOnlyList<HeldBuilding> Buildings { get; init; } = [];

    public bool ObligationsActive { get; init; }

    public int Believers { get; init; }

    public int MinBelievers { get; init; }

    public Fact<IdeoDevelopment> Development { get; init; }
}

public readonly record struct IdeoDevelopment(bool Fluid, bool CanReform, int Points, int ReformCount, int NextPoints);

/// <summary>The ideoligion with the defs its rows resolve against.</summary>
public sealed partial record Ideoligion(IdeologyDefs Defs, IdeoligionFacts Facts)
{
    /// <summary>
    /// Lists the effects of every precept in force, in precept id order. A held precept missing from the
    /// defs is skipped: the decode refuses such a row, so none reaches here.
    /// </summary>
    public IReadOnlyList<EffectInForce> EffectsInForce()
    {
        var output = new List<EffectInForce>();
        foreach (var precept in Facts.Precepts.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (!Defs.Precepts.TryGetValue(precept.Def, out var def))
            {
                continue;
            }
            foreach (var effect in def.Effects)
            {
                output.Add(new EffectInForce(def, effect));
            }
        }
        return output;
    }

    /// <summary>
    /// The ThingDefs the ideoligion needs built, sorted: the ThingDef each building precept took and the
    /// buildings the obligation target filter of each held ritual's pattern accepts.
    /// </summary>
    public IReadOnlyList<string> RequiredBuildings()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var building in Facts.Buildings)
        {
            if (building.Building != string.Empty)
            {
                seen.Add(building.Building);
            }
        }
        foreach (var ritual in Facts.Rituals)
        {
            if (Defs.Rituals.TryGetValue(ritual.Pattern, out var pattern))
            {
                foreach (var building in pattern.RequiredBuildings)
                {
                    seen.Add(building);
                }
            }
        }
        return [.. seen.Order(StringComparer.Ordinal)];
    }
}

/// <summary>One effect of a precept the ideoligion holds.</summary>
public sealed record EffectInForce(PreceptDef Precept, PreceptEffect Effect);
