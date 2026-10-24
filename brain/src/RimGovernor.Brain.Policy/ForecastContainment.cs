namespace RimGovernor.Brain.Policy;

// The containment cell credit and the escape term (#3811, epic #3650 P1/P7). Confirmed with ilspycmd on Assembly-CSharp.dll:
// - Escape: CompHoldingPlatformTarget.CaptivityTick rolls Rand.MTBEventOccurs(ContainmentUtility.InitiateEscapeMtbDays(pawn),
//   60000, 2500) for an entity held on a platform. The MTB in days is the comp's baseEscapeIntervalMtbDays, times
//   1 / Clamp(Moving, 0.01, 1), times DaysInCaptivityEscapeFactorCurve at the days held, times
//   EscapeIntervalMultiplierFromContainmentStrengthCurve at the platform's ContainmentStrength less the entity's
//   MinimumContainmentStrength, times ColdEscapeFactorCurve at its ambient temperature when the comp gets the cold bonus.
//   CanParticipateInEscape is false for a downed entity, one not held, or a base MTB of 0 or less (InitiateEscapeMtbDays -1).
// - Study: CompStudiable.AdjustedAnomalyKnowledgePerStudy is AnomalyKnowledge times
//   StudyKnowledgeMultiplierFromContainmentStrengthCurve at the same strength margin, over 5; a study happens at most once
//   per Props.frequencyTicks (CurrentlyStudiable) and StudyManager.StudyAnomaly gives its points to the comp's
//   KnowledgeCategory, which #3843 prices per point.

/// <summary>
/// An entity's escape facts as native reads them (#3811), for an entity with a CompHoldingPlatformTarget: the comp's
/// baseEscapeIntervalMtbDays; the movement factor 1 / Clamp(Moving, 0.01, 1); the days it has been held
/// (mindState.entityTicksInCaptivity); the cold factor ColdEscapeFactorCurve gives it where it stands (1 for an entity
/// without the cold bonus); its kind's combatPower, the strength of the fight an escape starts; and
/// ContainmentUtility.CanParticipateInEscape (held, not downed, a positive base MTB).
/// </summary>
public sealed record EntityEscape(double BaseMtbDays, double MovingFactor, double DaysInCaptivity, double ColdFactor, double CombatPower, bool CanEscape);

/// <summary>An entity's study facts as native reads them (#3811): CompStudiable.AnomalyKnowledge, its KnowledgeCategory def ("" none) and Props.frequencyTicks.</summary>
public sealed record StudyKnowledge(double Knowledge, string Category, int FrequencyTicks);

/// <summary>The platform an entity is held on (#3811): its cell and its CompEntityHolder.ContainmentStrength now.</summary>
public sealed record EntityPlatform(Cell Cell, double Strength);

/// <summary>
/// A living entity the colony holds on a platform, or one the game lets it capture that no platform holds yet (#3811): its
/// id, MinimumContainmentStrength, escape facts, study facts (null for one that gives no knowledge) and platform (null while
/// it waits for capture).
/// </summary>
public sealed record ContainedEntity(string Id, double Need, EntityEscape Escape, StudyKnowledge? Study, EntityPlatform? Platform);

/// <summary>
/// The game's containment curves (#3811), the private statics of Verse.ContainmentUtility the catalog reads by name:
/// EscapeIntervalMultiplierFromContainmentStrengthCurve, StudyKnowledgeMultiplierFromContainmentStrengthCurve and
/// DaysInCaptivityEscapeFactorCurve. Empty without Anomaly; <see cref="Reason"/> says why a read failed.
/// </summary>
public sealed record ContainmentCurves
{
    public EquatableArray<CurvePoint> Escape { get; init; }

    public EquatableArray<CurvePoint> Study { get; init; }

    public EquatableArray<CurvePoint> Captivity { get; init; }

    public string Reason { get; init; } = "";

    /// <summary>Whether every curve was read.</summary>
    public bool Read => Escape.Count != 0 && Study.Count != 0 && Captivity.Count != 0;
}

/// <summary>
/// What the escape term and the cell credit read of the colony's entities (#3811). A colony without Anomaly, or with nothing
/// held or capturable, has no entity. <see cref="Unread"/> says why native could not read an entity's facts, which either
/// reader raises as a required fact (<see cref="ForecastContainment.EntitiesOf"/>).
/// </summary>
public sealed record ContainmentForecast
{
    public EquatableArray<ContainedEntity> Entities { get; init; }

    /// <summary>Why the entities are unread; "" when they were read.</summary>
    public string Unread { get; init; } = "";

    public ContainmentCurves Curves { get; init; } = new();
}

/// <summary>
/// A planned containment cell (#3811): its room, the strength its platform predicts there once the cell is built
/// (ContainmentDefs.Predict, floored when the stock lays the containment floor) and the entity held on a standing platform
/// inside it ("" none). <see cref="Reason"/> says why the prediction could not be made.
/// </summary>
public sealed record PlannedContainmentCell(int Room, double Strength, string Held, string Reason = "");

/// <summary>
/// The ContainmentCell credit (#3811): what the planned cell changes for the entity it holds, from its service day.
/// <list type="bullet">
/// <item>An entity already held on a platform inside the cell: the escapes the cell's strength stops (the rate at the
/// platform's strength now less the rate at the cell's) times the loss of one escape, plus the study the cell's strength
/// adds (the yield at the cell's strength less the yield now).</item>
/// <item>A cell holding no one takes the next waiting entity by id, which the colony then captures: the study it gives at
/// the cell's strength, less its escapes at that strength times the loss of one. A waiting entity is taken as healed
/// (movement factor 1), new to captivity (0 days) and without the cold bonus, since the cell's temperature is not forecast.</item>
/// </list>
/// Each escape is one fight at the entity's combatPower (<see cref="ForecastValue.EscapeLoss"/>); a study yields
/// AnomalyKnowledge × the strength curve ÷ 5 once per frequencyTicks, each point priced at
/// <c>Investment[ProjectGraph.KnowledgeKey(category)]</c> (#3843). A cell with no entity earns nothing.
/// </summary>
public static class ForecastContainment
{
    /// <summary>The share of an entity's AnomalyKnowledge one study gives: CompStudiable.AdjustedAnomalyKnowledgePerStudy divides by 5, a code literal no def carries.</summary>
    public const double StudiesPerKnowledge = 5;

    /// <summary>What the credit leaves out, named on every served cell's ledger (<see cref="CellFindings"/>).</summary>
    public static readonly IReadOnlyList<string> NotModelled =
    [
        // An escape is priced as a raid of the entity's combat power: the raid exposure table and the raid's weapon bins (Q3).
        "containment_escape_raid_calibration_assumed",
        // An escape can pull out the room's other held entities (CompHoldingPlatformTarget.JoinEscapeChanceFromEscapeIntervalCurve).
        "containment_escape_join_unmodelled",
        // The days held grow over the horizon, lengthening the MTB; the factor stays at today's.
        "containment_captivity_growth_unmodelled",
        // The planned cell's temperature, which the cold bonus reads, is not forecast.
        "containment_cell_cold_unmodelled",
        // CompActivity.ActivityResearchFactor and an attached electroharvester scale a study's knowledge.
        "containment_study_activity_unmodelled",
    ];

    /// <summary>The planned cells and the entity each holds, by room (see <see cref="ForecastContainment"/>); cached per context.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, IReadOnlyDictionary<int, (PlannedContainmentCell Cell, ContainedEntity Entity)>> Cache = new();

    /// <summary>The cell's credit (<see cref="RoomCredit"/>): one row under the entity it holds, none for a cell with no entity.</summary>
    public static IReadOnlyList<RoomGain> CellCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (!Cache.GetValue(context, Assign).TryGetValue(room.Room, out var held)) return [];
        var (cell, entity) = held;
        var curves = Curves(context);
        var loss = ForecastValue.EscapeLoss(outcome, context, horizon, entity.Escape.CombatPower) ?? 0;
        double gain;
        if (entity.Platform is { } platform)
        {
            gain = (EscapesPerDay(entity, platform.Strength, curves) - EscapesPerDay(entity, cell.Strength, curves)) * loss
                + StudyPerDay(entity, cell.Strength, context, curves) - StudyPerDay(entity, platform.Strength, context, curves);
        }
        else
        {
            var waiting = Captured(entity);
            gain = StudyPerDay(waiting, cell.Strength, context, curves) - EscapesPerDay(waiting, cell.Strength, curves) * loss;
        }
        return gain == 0 ? [] : [new(entity.Id, "", gain)];
    }

    /// <summary>
    /// The served cell's named findings (<see cref="PlannedRoomCredits.Unmodelled"/>): <see cref="NotModelled"/> for a cell
    /// that holds an entity, <c>escape_unassessed:&lt;entity&gt;</c> when one escape's fight cannot be priced (the credit then
    /// counts no escape) and <c>knowledge_unpriced:&lt;category&gt;</c> when tier 2 gave the category no price (no study).
    /// </summary>
    public static IEnumerable<string> CellFindings(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (!Cache.GetValue(context, Assign).TryGetValue(room.Room, out var held)) return [];
        var entity = held.Entity;
        var findings = new List<string>(NotModelled);
        if (ForecastValue.EscapeLoss(outcome, context, horizon, entity.Escape.CombatPower) is null) findings.Add(EscapeUnassessed(entity.Id));
        if (entity.Study is { Category.Length: > 0 } study && !context.Investment.ContainsKey(ProjectGraph.KnowledgeKey(study.Category)))
            findings.Add("knowledge_unpriced:" + study.Category);
        return findings;
    }

    /// <summary>The finding of an entity whose escape fight cannot be priced.</summary>
    public static string EscapeUnassessed(string entity) => "escape_unassessed:" + entity;

    /// <summary>
    /// The escapes a day of <paramref name="entity"/> held at <paramref name="strength"/>: 1 / its InitiateEscapeMtbDays (see the
    /// file header); none for one that cannot escape.
    /// </summary>
    public static double EscapesPerDay(ContainedEntity entity, double strength, ContainmentCurves curves)
    {
        var e = entity.Escape;
        if (!e.CanEscape || !(e.BaseMtbDays > 0)) return 0;
        var mtb = e.BaseMtbDays * e.MovingFactor * LayoutFilthRules.EvaluateCurve(curves.Captivity, e.DaysInCaptivity)
            * LayoutFilthRules.EvaluateCurve(curves.Escape, strength - entity.Need) * e.ColdFactor;
        return mtb > 0 ? 1 / mtb : throw new DomainException(FormattableString.Invariant($"entity {entity.Id} has an escape MTB of {mtb} days"));
    }

    /// <summary>
    /// The capability-days a day <paramref name="entity"/>'s study gives held at <paramref name="strength"/>: AnomalyKnowledge ×
    /// the study curve ÷ <see cref="StudiesPerKnowledge"/> per study, a study per frequencyTicks, each point at its category's
    /// price. None for an entity that gives no knowledge, has no study interval or whose category tier 2 did not price.
    /// </summary>
    public static double StudyPerDay(ContainedEntity entity, double strength, ForecastCoupledContext context, ContainmentCurves curves)
    {
        if (entity.Study is not { Knowledge: > 0, FrequencyTicks: > 0, Category.Length: > 0 } study
            || !context.Investment.TryGetValue(ProjectGraph.KnowledgeKey(study.Category), out var price)) return 0;
        var perStudy = study.Knowledge * LayoutFilthRules.EvaluateCurve(curves.Study, strength - entity.Need) / StudiesPerKnowledge;
        return perStudy * GameTime.TicksPerDay / study.FrequencyTicks * price;
    }

    /// <summary>A waiting entity as the cell would hold it: healed, new to captivity, no cold bonus, able to escape when its base MTB allows.</summary>
    internal static ContainedEntity Captured(ContainedEntity entity) =>
        entity with { Escape = entity.Escape with { MovingFactor = 1, DaysInCaptivity = 0, ColdFactor = 1, CanEscape = entity.Escape.BaseMtbDays > 0 } };

    /// <summary>The colony's held and waiting entities, raised as a required fact when native could not read them.</summary>
    internal static EquatableArray<ContainedEntity> EntitiesOf(ContainmentForecast forecast) =>
        forecast.Unread.Length == 0 ? forecast.Entities : throw new RequiredFactException("containment entities: " + forecast.Unread);

    /// <summary>The curves, raised as a required fact when an entity needs them and they were not read.</summary>
    internal static ContainmentCurves Curves(ForecastCoupledContext context) =>
        context.Harm.Containment.Curves is { Read: true } curves ? curves
            : throw new RequiredFactException("containment curves" + (context.Harm.Containment.Curves.Reason.Length != 0 ? ": " + context.Harm.Containment.Curves.Reason : ""));

    /// <summary>
    /// Each planned cell's entity: a cell with a held entity on a platform inside it holds that one; the cells holding no one,
    /// by room id, take the waiting entities by id, one each. A cell whose prediction failed, while it holds an entity, raises
    /// the prediction as a required fact; unread entities raise the containment census.
    /// </summary>
    private static IReadOnlyDictionary<int, (PlannedContainmentCell, ContainedEntity)> Assign(ForecastCoupledContext context)
    {
        var output = new Dictionary<int, (PlannedContainmentCell, ContainedEntity)>();
        var cells = context.ContainmentCells.Where(c => context.PlannedRoomRoles.GetValueOrDefault(c.Room) == PlannedRole.ContainmentCell).OrderBy(c => c.Room).ToArray();
        if (cells.Length == 0) return output;
        var entities = EntitiesOf(context.Harm.Containment);
        if (entities.Count == 0) return output;
        var byId = entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var waiting = new Queue<ContainedEntity>(entities.Where(e => e.Platform is null).OrderBy(e => e.Id, StringComparer.Ordinal));
        foreach (var cell in cells)
        {
            ContainedEntity? entity = cell.Held.Length != 0 && byId.TryGetValue(cell.Held, out var held) ? held
                : cell.Held.Length == 0 && waiting.Count != 0 ? waiting.Dequeue() : null;
            if (entity is null) continue;
            if (cell.Reason.Length != 0) throw new RequiredFactException(FormattableString.Invariant($"containment cell {cell.Room} prediction: {cell.Reason}"));
            output[cell.Room] = (cell, entity);
        }
        return output;
    }

    /// <summary>
    /// The layout plan's containment cells (#3811, <see cref="ContainmentCellRules.PlannedCells"/>): each one's predicted strength
    /// (<see cref="ContainmentCellRules.PlannedCellStrength"/>) and the entity on a standing platform inside its region.
    /// </summary>
    public static EquatableArray<PlannedContainmentCell> CellsOf(ContainmentPlanning containment, FlooringFacts floors)
    {
        var output = new List<PlannedContainmentCell>();
        foreach (var room in ContainmentCellRules.PlannedCells(floors).Where(r => r.Id > 0).DistinctBy(r => r.Id).OrderBy(r => r.Id))
        {
            var cells = room.Region.Cells().ToHashSet();
            var held = containment.Forecast.Entities.Where(e => e.Platform is { } p && cells.Contains(p.Cell)).Select(e => e.Id).Order(StringComparer.Ordinal).FirstOrDefault() ?? "";
            var (strength, reason) = ContainmentCellRules.PlannedCellStrength(containment, floors, room);
            output.Add(new(room.Id, strength, held, reason));
        }
        return EquatableArray.From(output);
    }
}
