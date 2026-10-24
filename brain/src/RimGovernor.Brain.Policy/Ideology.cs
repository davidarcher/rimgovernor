namespace RimGovernor.Brain.Policy;

// The Ideology facts. Static defs are a view over the definition catalog's mirrored rows (IdeologyDefs,
// fixed for a load); the primary ideoligion's current state comes from the frame's ideology section
// (IdeoligionFacts). Both are read from the game's defs: no def name is listed here. A frame always states
// its mode: Facts.Ideology is NotInstalled without the expansion and Installed with its primary ideoligion.

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

    /// <summary>The chance of an unwilling effect's refusal. A comp without a chance refuses outright (<see cref="Certain"/>).</summary>
    public double Chance { get; init; } = Certain;

    /// <summary>The chance of a refusal that always holds.</summary>
    public const double Certain = 1;

    /// <summary>Reports a thought effect with a stage that costs mood.</summary>
    public bool Penalises() => StageMoods.Any(mood => mood < 0);

    /// <summary>Reports a thought effect with a stage that gives mood.</summary>
    public bool Approves() => StageMoods.Any(mood => mood > 0);

    /// <summary>Reports an effect that makes members refuse an action.</summary>
    public bool Forbids() => Kind == PreceptEffectKind.Unwilling;
}

/// <summary>
/// One PreceptDef other than a role's, reduced to what planners read: its comps as effects, the issue it is a degree of,
/// the memes it needs (RequiredMemes) and the memes that rule it out (ConflictingMemes); the def name is its key in IdeologyDefs.Precepts.
/// </summary>
public sealed record PreceptDef(IReadOnlyList<PreceptEffect> Effects)
{
    public string Issue { get; init; } = string.Empty;

    public IReadOnlyList<string> RequiredMemes { get; init; } = [];

    public IReadOnlyList<string> ConflictingMemes { get; init; } = [];
}

/// <summary>
/// One MemeDef: the precept groups it requires (one precept of each group must be held, MemeDef.requireOne) and the
/// precepts that conflict with it (those naming it in ConflictingMemes), sorted.
/// </summary>
public sealed record MemeDef(IReadOnlyList<IReadOnlyList<string>> RequireOne, IReadOnlyList<string> ForbiddenPrecepts);

/// <summary>The memes that unlock or rule out the precepts of an issue; each list is sorted and distinct.</summary>
public sealed record MemeGate(IReadOnlyList<string> Required, IReadOnlyList<string> Conflicting);

/// <summary>A role requirement's skill minimum.</summary>
public readonly record struct SkillRequirement(string Skill, int MinLevel);

/// <summary>One condition on a role's holder: for a skill requirement, the skills of which one must meet its minimum.</summary>
public sealed record RoleRequirement(IReadOnlyList<SkillRequirement> Skills);

/// <summary>
/// One role precept def: how many pawns hold it, what it asks of them and the work types it disables for its holder
/// (those whose work tags meet the def's roleDisabledWorkTags, as the game's Precept_Role.DisabledWorkTypes reads them).
/// </summary>
public sealed record RoleDef(int MaxCount, IReadOnlyList<RoleRequirement> Requirements)
{
    public IReadOnlyList<WorkType> DisabledWork { get; init; } = [];
}

/// <summary>A ritual behavior's role slot.</summary>
public sealed record RitualRoleSlot(string Precept, int MaxCount, bool Required);

/// <summary>
/// One ritual pattern: its cadence in days, whether it may start freely, the buildings its obligation target
/// filter accepts and its role slots.
/// </summary>
public sealed record RitualDef
{
    public double IntervalDaysMin { get; init; }

    public IReadOnlyList<string> RequiredBuildings { get; init; } = [];

    public IReadOnlyList<RitualRoleSlot> Roles { get; init; } = [];

    public bool CanStartAnytime { get; init; }

    public bool AlwaysStartAnytime { get; init; }

    /// <summary>The pattern's outcome effect (#3808); null when it has none.</summary>
    public RitualOutcomeModel? Outcome { get; init; }
}

/// <summary>A quality input of a ritual outcome: the participant count or a room stat of the ritual's room.</summary>
public enum RitualQualityInput
{
    Participants,
    Impressiveness,
    Cleanliness,
}

/// <summary>A RitualOutcomeComp_Quality the brain can know before the ritual: its input and the curve of its quality offset.</summary>
public sealed record RitualQualityComp(RitualQualityInput Input, IReadOnlyList<CurvePoint> Curve);

/// <summary>
/// One RitualOutcomePossibility: its base chance, whether it is positive (positivityIndex &gt;= 0, the chance scaled by
/// quality) and the memory each attendee gains, as its stage-0 mood over its duration (0 without a memory).
/// </summary>
public sealed record RitualOutcomeChance(double Chance, bool Positive, double Mood, double DurationDays);

/// <summary>
/// A RitualOutcomeEffectDef whose worker is RitualOutcomeEffectWorker_FromQuality (#3808): the quality is
/// startingQuality plus each known comp's curve at its input, clamped to [MinQuality, MaxQuality]; the outcome is drawn
/// with weight chance * quality for a positive outcome and chance otherwise. Comps whose input is not knowable before
/// the ritual are left out.
/// </summary>
public sealed record RitualOutcomeModel
{
    public double StartingQuality { get; init; }

    public double MinQuality { get; init; }

    public double MaxQuality { get; init; }

    public IReadOnlyList<RitualQualityComp> Comps { get; init; } = [];

    public IReadOnlyList<RitualOutcomeChance> Chances { get; init; } = [];

    /// <summary>The worker class of an outcome effect that does not draw its outcome from quality; "" when it does.</summary>
    public string Unread { get; init; } = string.Empty;
}

/// <summary>The stage-0 mood of the game's IdeoBuildingMissing and IdeoBuildingDisrespected thoughts (#3808).</summary>
public sealed record BuildingDemandMoods(double Missing, double Disrespected);

/// <summary>The Ideology defs of one load, by def name: a view over the catalog's mirrored rows.</summary>
public sealed record IdeologyDefs
{
    public IReadOnlyDictionary<string, PreceptDef> Precepts { get; init; } = new Dictionary<string, PreceptDef>();

    public IReadOnlyDictionary<string, RoleDef> Roles { get; init; } = new Dictionary<string, RoleDef>();

    public IReadOnlyDictionary<string, RitualDef> Rituals { get; init; } = new Dictionary<string, RitualDef>();

    public IReadOnlyDictionary<string, MemeDef> Memes { get; init; } = new Dictionary<string, MemeDef>();

    /// <summary>The moods of a building precept's presence demand; null when the catalog lacks either thought.</summary>
    public BuildingDemandMoods? BuildingMoods { get; init; }

    /// <summary>
    /// The memes that gate the precepts of the named issues (game IssueDef names, such as the nudity, cannibalism and
    /// insect meat issues): the memes some such precept requires and the memes some such precept conflicts with.
    /// </summary>
    public MemeGate Gate(IEnumerable<string> issues)
    {
        var wanted = issues.ToHashSet(StringComparer.Ordinal);
        var precepts = Precepts.Values.Where(p => wanted.Contains(p.Issue)).ToList();
        return new MemeGate(
            [.. precepts.SelectMany(p => p.RequiredMemes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. precepts.SelectMany(p => p.ConflictingMemes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }
}

/// <summary>A precept in force; role, ritual and building precepts are held apart.</summary>
public sealed record HeldPrecept(string Id, string Def);

/// <summary>A role precept: whether enough believers make it active and the pawns holding it.</summary>
public sealed record HeldRole(string Id, string Def, bool Active, IReadOnlyList<PawnId> Pawns)
{
    /// <summary>Whether the role grants the Convert ability (the moral guide's).</summary>
    public bool Converts { get; init; }

    /// <summary>Whether a holder's Convert ability is charged: off cooldown and not already ordered.</summary>
    public bool ConvertReady { get; init; }
}

/// <summary>
/// A ritual precept. LastFinishedTick is the game's Precept_Ritual.lastFinishedTick as read; no sentinel is
/// interpreted here. Running is whether a lord job of the precept is running now.
/// </summary>
public sealed record HeldRitual
{
    public string Id { get; init; } = string.Empty;

    public string Pattern { get; init; } = string.Empty;

    public long LastFinishedTick { get; init; }

    public int ActiveObligations { get; init; }

    public bool RepeatPenaltyActive { get; init; }

    public bool Running { get; init; }
}

/// <summary>A building precept: the ThingDef it takes and its presence demand on the frame's map.</summary>
public sealed record HeldBuilding(string Building)
{
    public BuildingDemand Demand { get; init; } = new();
}

/// <summary>
/// A building precept's presence demand (#3808): whether it applies on the map (the game's AppliesTo), whether a
/// building of the precept stands (BuildingPresent) and whether one stands in a room meeting every requirement
/// (RequirementsSatisfied). The requirements: the room's least area and impressiveness (0 none), the terrain tags
/// every floor cell needs (none: any), the things it must hold, the building defs it must not hold, and the class name
/// of each requirement of a kind the plan cannot predict.
/// </summary>
public sealed record BuildingDemand
{
    public bool Applies { get; init; }

    public bool Present { get; init; }

    public bool Satisfied { get; init; }

    public int MinArea { get; init; }

    public int MinImpressiveness { get; init; }

    public IReadOnlyList<string> FloorTags { get; init; } = [];

    public IReadOnlyList<ThingCount> Counts { get; init; } = [];

    public IReadOnlyList<string> ForbiddenDefs { get; init; } = [];

    public IReadOnlyList<string> Unread { get; init; } = [];
}

/// <summary>The player faction's primary ideoligion as it stands.</summary>
public sealed record IdeoligionFacts
{
    public string IdeoId { get; init; } = string.Empty;

    public IReadOnlyList<string> Memes { get; init; } = [];

    public IReadOnlyList<HeldPrecept> Precepts { get; init; } = [];

    public IReadOnlyList<HeldRole> Roles { get; init; } = [];

    public IReadOnlyList<HeldRitual> Rituals { get; init; } = [];

    public IReadOnlyList<HeldBuilding> Buildings { get; init; } = [];

    public int Believers { get; init; }

    /// <summary>The free non-slave colonists on the frame's map holding the ideoligion, by pawn id, sorted.</summary>
    public IReadOnlyList<PawnId> BelieverPawns { get; init; } = [];

    public required IdeoDevelopment Development { get; init; }

    /// <summary>The ThingStyleCategoryDef names of the ideoligion's styles, sorted.</summary>
    public IReadOnlyList<string> StyleCategories { get; init; } = [];

    /// <summary>The ThingDef names of the venerated animals, sorted.</summary>
    public IReadOnlyList<string> VeneratedAnimals { get; init; } = [];
}

/// <summary>
/// The reform state. DevelopmentPoints and NextReformPoints (what the next reform costs) are 0 for a non-fluid
/// ideoligion, which cannot reform.
/// </summary>
public readonly record struct IdeoDevelopment(bool Fluid, bool CanReform, int ReformCount, int DevelopmentPoints, int NextReformPoints)
{
    /// <summary>A fixed ideoligion: it cannot reform, so it has no points and no reform cost.</summary>
    public static readonly IdeoDevelopment Fixed = new(false, false, 0, 0, 0);
}

/// <summary>The ideoligion with the defs its rows resolve against.</summary>
public sealed partial record Ideoligion(IdeologyDefs Defs, IdeoligionFacts Facts)
{

    /// <summary>
    /// Lists the effects of every precept in force, in precept id order. A held precept missing from the
    /// defs is skipped: the decode refuses such a row, so none reaches here.
    /// </summary>
    public IReadOnlyList<PreceptEffect> EffectsInForce()
    {
        var output = new List<PreceptEffect>();
        foreach (var precept in Facts.Precepts.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (!Defs.Precepts.TryGetValue(precept.Def, out var def))
            {
                continue;
            }
            output.AddRange(def.Effects);
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
