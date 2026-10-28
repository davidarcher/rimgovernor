using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What a joiner's value is priced from beside <see cref="ForecastCoupledContext.ModelBasis"/> (#3805): the candidate roots an
/// outcome chooses among (productive alternatives and incumbents contribute unchosen work to its shortfall;
/// service-only completions do not establish productive demand), the defs' work map and
/// the catalog's food work. Set by the review beside the model basis; null before tier 2 ran.
/// </summary>
public sealed record JoinerBasis
{
    public EquatableArray<ForecastCommitment> Roots { get; init; }

    public WorkSpeedFacts Work { get; init; } = new();

    public FoodWorkFacts Food { get; init; } = new();
}

/// <summary>
/// The joiner's model (#3805, epic #3650 D6 revised): a pawn not yet a colonist (a prisoner; wanderers, quest joiners and
/// creepjoiners later) as it would be once it joins. Its mood is the colony's situational thoughts plus its own trait thoughts
/// and memories; its needs are its own falls served as the colonists' are; its value is <see cref="PawnValueRules.Of"/> of the
/// work an outcome leaves short.
/// </summary>
public static partial class JoinerMoodRules
{
    /// <summary>
    /// Whether the colony's situation gives a colonist the thought (the food, room and recreation thoughts, from the audit table
    /// thought_triggers.tsv): its owner is a food or rooms Need or shelter/sleeping; its grant category is ingesting or sleeping;
    /// or it depends on the food or joy need, the room, the room's role or a room stat. A def with no row is none.
    /// </summary>
    public static bool ColonyThought(string def)
    {
        var (row, ok) = ThoughtFactsRules.ThoughtAuditRow(def);
        if (!ok) return false;
        if (row.Owner.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(o => o.StartsWith("food/", StringComparison.Ordinal)
            || o.StartsWith("rooms/", StringComparison.Ordinal) || o == "shelter/sleeping"))
            return true;
        var grant = row.Grant.Split(' ', 2)[0].Split('+');
        if (grant.Contains("ingest") || grant.Contains("sleep")) return true;
        return row.Dependency.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(d => d is "need:food" or "need:joy" or "room"
            || d.StartsWith("room_role:", StringComparison.Ordinal) || d.StartsWith("room_stat:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The colony's situational thoughts a joiner takes on (#3805): per <see cref="ColonyThought"/> def any colonist of the needs
    /// read has, the median over every colonist of its observed offset (<see cref="ForecastCoupledContext.ColonistThoughts"/>, the
    /// def's rows summed; 0 for a colonist without it). A colonist whose thoughts were not read throws
    /// <see cref="RequiredFactException"/>. Cached per context.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ColonyThoughts(ForecastCoupledContext context) => Colony.GetValue(context, c =>
    {
        var rows = new List<Dictionary<string, double>>();
        foreach (var pawn in c.Needs.Pawns.Select(p => p.Pawn).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var thoughts = c.ColonistThoughts.TryGetValue(pawn, out var read) ? read : throw new RequiredFactException("observed thoughts:" + pawn);
            rows.Add(thoughts.Where(t => ColonyThought(t.Def)).GroupBy(t => t.Def, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.Offset), StringComparer.Ordinal));
        }

        return rows.SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .ToDictionary(d => d, d => Median([.. rows.Select(r => r.GetValueOrDefault(d))]), StringComparer.Ordinal);
    });

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, IReadOnlyDictionary<string, double>> Colony = new();

    /// <summary>
    /// The joiner's mood as a colonist (#3805, #3899): its base level plus, over 100, the colony's thoughts, its trait thoughts,
    /// its own-bedroom trait thoughts served as the colonists are (<see cref="RoomTraits"/>), its selected trait exposure
    /// (<paramref name="situationOffset"/> from SelectedSituationTraits), its memories and the
    /// colonistMoodOffset (unclamped, as <see cref="PawnMoodRead.Target"/>), at its own break threshold; null without a mood (it
    /// never breaks). A colony thought that is one of the joiner's trait thoughts counts once, at the joiner's own offset: its
    /// traits decide it, not the colonists'.
    /// </summary>
    public static PawnMoodRead? MoodOf(JoinerMoodRead? mood, IReadOnlyDictionary<string, double> colony, IReadOnlyList<NeedsForecastPawn> colonists, double? situationOffset = null)
    {
        if (mood is null) return null;
        var own = mood.Traits.Select(t => t.Thought).Concat(mood.RoomTraits.Select(r => r.Thought))
            .Concat(mood.SituationStages.Select(r => r.Thought)).ToHashSet(StringComparer.Ordinal);
        var shared = colony.Where(c => !own.Contains(c.Key)).Sum(c => c.Value);
        var room = mood.RoomTraits.Count == 0 ? 0 : RoomTraits(mood.RoomTraits, colonists);
        var situation = mood.SituationStages.Count == 0 ? 0 : situationOffset ?? throw new RequiredFactException("selected joiner trait exposure");
        return new(mood.BaseLevel + ((shared + mood.Traits.Sum(t => t.Offset) + room + situation + mood.Memories.Sum(m => m.Offset) + mood.ColonistOffset) / 100), mood.Threshold);
    }

    private static double SituationOffset(JoinerSituationThought row, JoinerTraitService pawn)
    {
        var situation = pawn.TraitSituation;
        var stage = row.Kind switch
        {
            JoinerSituationKind.Nude => !situation.Clothed ? 0 : -1,
            JoinerSituationKind.HumanLeather => situation.HumanLeather > 0 ? Math.Min(4, situation.HumanLeather - 1) : -1,
            JoinerSituationKind.RangedWeapon => situation.RangedWeapon ? 0 : -1,
            JoinerSituationKind.IncendiaryWeapon => situation.IncendiaryWeapon ? 0 : -1,
            JoinerSituationKind.Flames => situation.Fires > 0 ? 0 : -1,
            JoinerSituationKind.Day => pawn.Awake && (int)pawn.LocalHour is >= 11 and <= 17 ? 0 : -1,
            JoinerSituationKind.Night => pawn.Awake && ((int)pawn.LocalHour >= 23 || (int)pawn.LocalHour <= 5) ? 0 : -1,
            JoinerSituationKind.Indoors => pawn.Awake && situation.Indoor && !situation.NaturalRoof ? 0 : -1,
            JoinerSituationKind.Underground => pawn.Awake && situation.Indoor && situation.NaturalRoof ? 0 : -1,
            JoinerSituationKind.Outdoors => pawn.Awake && !situation.Indoor ? 0 : -1,
            var invalid => throw new ArgumentOutOfRangeException(nameof(row), invalid, "Invalid joiner trait situation."),
        };
        if (!row.CanGet) stage = -1;
        if (row.Invert) stage = stage >= 0 ? -1 : 0;
        if (stage < 0) return 0;
        if (row.Stages.Count == 0) throw new RequiredFactException("joiner trait stages:" + row.Thought);
        return row.Stages[Math.Min(stage, row.Stages.Count - 1)]
            * (row.Kind == JoinerSituationKind.Flames ? Math.Min(situation.Fires, row.StackLimit) : 1);
    }

    /// <summary>
    /// The joiner's own-bedroom trait thoughts as a colonist (#3899): the median over <paramref name="colonists"/> of their sum
    /// with the joiner in each one's room (<see cref="NeedsForecastPawn.OwnedRoom"/>), as the workers stage them: Greedy
    /// (ThoughtWorker_Greedy) at the room's Impressiveness stage + 1, stage 0 without a room; Ascetic (ThoughtWorker_Ascetic) at
    /// the room's stage, none without one; Jealous (ThoughtWorker_BedroomJealous) held when a room another pawn owns
    /// (<see cref="NeedsForecastPawn.RivalRooms"/>) beats the room's Impressiveness (0 without one) by
    /// <see cref="ForecastHousing.JealousMargin"/> of it. A missing stage row or no colonist throws <see cref="RequiredFactException"/>.
    /// </summary>
    public static double RoomTraits(IReadOnlyList<NeedThoughtStage> rows, IReadOnlyList<NeedsForecastPawn> colonists)
    {
        var pawns = colonists.DistinctBy(p => p.Pawn, StringComparer.Ordinal).ToArray();
        if (pawns.Length == 0) throw new RequiredFactException("colony pawns");
        double Row(string def, string role, int stage) => rows.FirstOrDefault(r => r.Thought == def && r.Role == role && r.Stage == stage)?.Offset
            ?? throw new RequiredFactException(role + " thought stage:" + def);
        var sums = new List<double>();
        foreach (var pawn in pawns)
        {
            var room = pawn.OwnedRoom;
            double sum = 0;
            foreach (var (def, role) in rows.Select(r => (r.Thought, r.Role)).Distinct())
                sum += role switch
                {
                    ForecastHousing.GreedyRole => Row(def, role, room is null ? 0 : room.Stage + 1),
                    "ascetic" => room is null ? 0 : Row(def, role, room.Stage),
                    ForecastHousing.JealousRole => pawn.RivalRooms.Any(r => r - (room?.Impressiveness ?? 0) >= Math.Abs((room?.Impressiveness ?? 0) * ForecastHousing.JealousMargin))
                        ? Row(def, role, 0) : 0,
                    _ => throw new RequiredFactException("room trait role:" + role),
                };
            sums.Add(sum);
        }

        return Median(sums);
    }

    /// <summary>
    /// The joiner served as <paramref name="colonist"/> is (#3805), as the pawn cycle reads it under <paramref name="id"/>: each of
    /// its needs the colonist's service of that need, at the joiner's own fall and threshold. Rest at the colonist's bed
    /// effectiveness (its rest gain over its GainPerTickPerEffectiveness, <paramref name="colonistRates"/>) times the joiner's, at
    /// least the joiner's unserved rest; joy at the colonist's sheltered gain, the joiner's own walk where the weather allows it;
    /// food at the colonist's meal, service and approach. A need the colonist does not have is the joiner's unserved one.
    /// </summary>
    public static ModelPawn ServedAs(string id, JoinerRead joiner, ModelPawn colonist, IReadOnlyList<NeedRate> colonistRates)
    {
        var needs = new List<ModelNeed>();
        foreach (var need in joiner.Needs)
        {
            var own = new ModelNeed { Kind = need.Kind, Fall = need.FallPerTick, Gain = need.UnservedGainPerTick, Threshold = need.Threshold, OutdoorShares = need.OutdoorShareByTwelfth, Service = new ModelService.NoneObserved() };
            var served = colonist.Needs.FirstOrDefault(n => n.Kind == need.Kind) is not { } theirs ? own : need.Kind switch
            {
                "rest" => theirs with
                {
                    Fall = need.FallPerTick, Threshold = need.Threshold, OutdoorShares = need.OutdoorShareByTwelfth, Gain = Math.Max(need.UnservedGainPerTick,
                        colonistRates.FirstOrDefault(r => r.Kind == "rest") is { GainPerTickPerEffectiveness: > 0 } rate ? theirs.Gain / rate.GainPerTickPerEffectiveness * need.GainPerTickPerEffectiveness : 0),
                },
                "joy" => theirs with
                {
                    Fall = need.FallPerTick, Threshold = need.Threshold, OutdoorShares = need.OutdoorShareByTwelfth, Gain = Math.Max(need.UnservedGainPerTick, theirs.ShelteredGain),
                },
                _ => theirs with { Fall = need.FallPerTick, Threshold = need.Threshold },
            };
            needs.Add(served);
        }

        // Only its needs are read (the free hours of its day); the rest of the row is the colonist's.
        return colonist with { Pawn = id, Needs = EquatableArray.From(needs) };
    }

    /// <summary>
    /// The joiner's value (#3825's <see cref="PawnValueRules.Of"/>) against <paramref name="shortfall"/>: its read speeds, incapable
    /// work and food, its mood as a colonist (<see cref="MoodOf"/>) and its free hours over the investment horizon, the median over
    /// the colonists of its hours served as each is (<see cref="ServedAs"/>, <see cref="PawnCycle.FreeHoursByPawn"/>), on the colony's
    /// food work, break curve, bed and planned food walks. A colonist the joiner served as it has no free hours, no colonist, or a
    /// context without the model basis, the joiner basis or the layout's food walks, throws <see cref="RequiredFactException"/>.
    /// </summary>
    public static PawnValue ValueOf(string pawn, JoinerRead joiner, WorkShortfall shortfall, ForecastCoupledContext context,
        ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, double horizon)
    {
        var basis = context.ModelBasis ?? throw new RequiredFactException("colony model basis");
        var joiners = context.Joiners ?? throw new RequiredFactException("joiner basis");
        var haul = context.FoodHaul ?? throw new RequiredFactException(PawnValueRules.AwaitingLayoutPlan);
        var rates = context.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<NeedRate>)[.. g.First().Needs], StringComparer.Ordinal);
        var colonists = new List<string>();
        var hours = PawnCycle.FreeHoursByPawn(basis.Input, context.InvestmentHorizonDays, modelled =>
        {
            colonists.AddRange(modelled.Select(c => c.Pawn));
            return modelled.Select(c => ServedAs(pawn + "@" + c.Pawn, joiner, c, rates.GetValueOrDefault(c.Pawn) ?? []));
        });
        if (colonists.Count == 0) throw new RequiredFactException("colony model pawns");
        var free = Median([.. colonists.Select(c => hours.TryGetValue(pawn + "@" + c, out var h) ? h : throw new RequiredFactException("free hours of " + pawn + " served as " + c))]);
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var situation = SelectedSituationTraits(joiner.Mood?.SituationStages ?? [], outcome, commitments, context, horizon, findings);
        var value = PawnValueRules.Of(new PawnValueFacts(pawn)
        {
            Speeds = joiner.Upkeep.Speeds.ToDictionary(s => s.Stat, s => s.Value, StringComparer.Ordinal), Incapable = joiner.Upkeep.IncapableWork,
            FoodPerDay = joiner.Upkeep.FoodPerDay, Mood = MoodOf(joiner.Mood, ColonyThoughts(context), context.Needs.Pawns, situation), FreeTicksPerDay = free * GameTime.TicksPerHour,
        }, new PawnValueColony { Shortfall = shortfall, Food = joiners.Food, Breaks = context.MoodBreaks, Bed = context.Bed, Haul = haul, HorizonDays = context.InvestmentHorizonDays });
        return value with { NotModelled = [.. value.NotModelled, .. findings] };
    }

    /// <summary>
    /// The work <paramref name="outcome"/> leaves short (#3805, <see cref="WorkShortfallRules.Of"/>): the joiner basis's roots, the
    /// outcome's chosen <paramref name="commitments"/> and allocations over the tier-1 <paramref name="horizon"/>, at the colonists'
    /// read speeds. Computed once per outcome (<see cref="PerOutcome{T}"/>). A context without the joiner basis throws
    /// <see cref="RequiredFactException"/>.
    /// </summary>
    public static WorkShortfall ShortfallOf(ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        Shortfalls.Of(outcome, commitments, context, horizon);

    private static readonly PerOutcome<WorkShortfall> Shortfalls = new((outcome, commitments, context, horizon) =>
    {
        var joiners = context.Joiners ?? throw new RequiredFactException("joiner basis");
        var speeds = context.Needs.Pawns.ToDictionary(p => p.Pawn,
            p => (IReadOnlyDictionary<string, double>)p.Upkeep.Speeds.ToDictionary(s => s.Stat, s => s.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        return WorkShortfallRules.Of(context, joiners.Roots, commitments.Select(c => c.Id).ToHashSet(StringComparer.Ordinal),
            outcome.Plan.Allocations, horizon, joiners.Work, speeds);
    });

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
