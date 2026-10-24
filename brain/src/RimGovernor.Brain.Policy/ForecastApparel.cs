using RimGovernor.Contracts.Wire.Observations;
using Gender = RimGovernor.Contracts.Wire.Defs.Gender;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The apparel thoughts a garment changes (#3816), each stage priced by the game's own offset for the pawn
/// (<see cref="NeedsForecastPawn.ThoughtStages"/>), and the investment credit a delivered garment earns from them: the mood of
/// the outfit with the garment overlaid less the worn outfit's, as <see cref="ForecastHousing.GainPerDay"/> prices a bedroom.
/// </summary>
public static class ForecastApparel
{
    public const string Damaged = "apparel_damaged";
    public const string Naked = "naked";
    public const string DeadMans = "dead_mans_apparel";
    public const string GroinUncovered = "groin_uncovered";
    public const string GroinOrChestUncovered = "groin_or_chest_uncovered";
    public const string GroinChestOrHairUncovered = "groin_chest_or_hair_uncovered";
    public const string GroinChestHairOrFaceUncovered = "groin_chest_hair_or_face_uncovered";
    public const string AnyCovered = "any_body_part_covered";
    public const string AnyButGroinCovered = "any_body_part_but_groin_covered";
    public const string AnyButHairOrFaceCovered = "any_body_part_but_hair_or_face_covered";

    /// <summary>The finding for a pawn whose garment credit an unread fact leaves unpriced.</summary>
    public const string CreditUnassessed = "apparel_credit_unassessed:";

    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    {
        Damaged, Naked, DeadMans, GroinUncovered, GroinOrChestUncovered, GroinChestOrHairUncovered, GroinChestHairOrFaceUncovered,
        AnyCovered, AnyButGroinCovered, AnyButHairOrFaceCovered,
    };

    // BodyPartGroupDefOf's defNames, as the catalog's apparel rows carry them (GearRules.GearCoreGroups reads the same strings).
    private const string Torso = "Torso", Legs = "Legs", UpperHead = "UpperHead", FullHead = "FullHead", Eyes = "Eyes";

    /// <summary>The Investment key a delivered garment credits its wearer under.</summary>
    public static string CreditKey(string pawn, string garment) => "apparel/" + pawn + "/" + garment;

    /// <summary>
    /// The summed offset of the apparel thoughts <paramref name="pawn"/> has wearing <paramref name="outfit"/> (its primary weapon
    /// aside), each thought's stage as its worker picks it. A thought without rows does not apply to the pawn (the native read
    /// leaves out a precept the ideoligion does not hold, and Naked under Ideology). Null when the pawn's apparel facts are
    /// unread or an active stage within the thought's stages has no row.
    /// </summary>
    public static double? MoodOf(NeedsForecastPawn pawn, IEnumerable<GearOption> outfit)
    {
        if (pawn.Apparel is not { } facts) return null;
        var worn = outfit.Where(o => o.Slot != GearSlot.Primary).ToArray();
        double sum = 0;
        foreach (var thought in pawn.ThoughtStages.Where(r => Roles.Contains(r.Role)).GroupBy(r => (r.Thought, r.Role)))
        {
            var stage = StageOf(thought.Key.Role, facts, worn);
            if (stage < 0) continue;
            // Past the last defined stage the game clamps to it (ThoughtState.StageIndexFor): five tainted garments are the fourth stage.
            var last = thought.MaxBy(r => r.Stage)!;
            if ((stage > last.Stage ? last : thought.FirstOrDefault(r => r.Stage == stage)) is not { } row) return null;
            sum += row.Offset;
        }
        return sum;
    }

    /// <summary>The apparel mood <paramref name="garments"/> overlaid on <paramref name="worn"/> adds; null when either side's is unknown.</summary>
    public static double? MoodDelta(NeedsForecastPawn pawn, IEnumerable<GearOption> worn, EquatableArray<GearOption> garments)
    {
        var outfit = worn.ToArray();
        return MoodOf(pawn, new ForecastOutfitChange(pawn.Pawn, garments) { Overlay = true }.Apply(outfit)) - MoodOf(pawn, outfit);
    }

    /// <summary>
    /// Whether overlaying <paramref name="garments"/> on <paramref name="worn"/> changes anything the apparel workers read of the
    /// outfit (the groups covered, the clothing and its groups, the damaged garments' condition, the tainted count): when it does
    /// not, its <see cref="MoodDelta"/> is zero for every pawn.
    /// </summary>
    public static bool ChangesThoughts(IEnumerable<GearOption> worn, EquatableArray<GearOption> garments)
    {
        var before = worn.ToArray();
        return Signature(before) != Signature(new ForecastOutfitChange("", garments) { Overlay = true }.Apply(before));
    }

    private static string Signature(IEnumerable<GearOption> outfit)
    {
        var worn = outfit.Where(o => o.Slot != GearSlot.Primary).ToArray();
        var least = worn.Where(o => o.CareIfDamaged && !o.Locked).Select(o => o.Condition).DefaultIfEmpty(999).Min();
        return string.Join(",", worn.SelectMany(o => o.Groups).Distinct().Order(StringComparer.Ordinal)) + ";"
            + string.Join(",", worn.Where(o => o.CountsAsClothingForNudity).Select(o => o.Definition.Value + ":" + string.Join("+", o.Groups)).Order(StringComparer.Ordinal))
            + ";" + (least < 0.2 ? 1 : least < 0.5 ? 0 : -1) + ";" + worn.Count(o => o.Tainted) + ";" + (worn.Length > 0);
    }

    /// <summary>The worker's active stage for <paramref name="role"/>, -1 when inactive.</summary>
    private static int StageOf(string role, NeedApparel facts, GearOption[] worn)
    {
        bool Covers(string group) => worn.Any(o => o.Groups.Contains(group));
        bool Unrequired(GearOption o) => o.CountsAsClothingForNudity && !facts.KindRequired.Contains(o.Definition.Value);
        switch (role)
        {
            case Damaged:
                // ThoughtWorker_ApparelDamaged: the least hit-point fraction of the unlocked careIfDamaged apparel.
                var least = worn.Where(o => o.CareIfDamaged && !o.Locked).Select(o => o.Condition).DefaultIfEmpty(999).Min();
                return least < 0.2 ? 1 : least < 0.5 ? 0 : -1;
            case Naked:
                // Pawn_ApparelTracker.PsychologicallyNude: a legless pawn counts as wearing pants.
                var pants = !facts.Legs || Covers(Legs);
                var nude = facts.Gender == Gender.Male ? !pants : facts.Gender == Gender.Female && (!pants || !Covers(Torso));
                return nude ? 0 : -1;
            case DeadMans:
                // ThoughtWorker_ApparelThought over WornByCorpse.
                var tainted = worn.Count(o => o.Tainted);
                return tainted - 1;
            case GroinUncovered:
                return Covers(Legs) ? -1 : 0;
            case GroinOrChestUncovered:
                return Covers(Torso) && Covers(Legs) ? -1 : 0;
            case GroinChestOrHairUncovered:
                return Covers(Torso) && Covers(Legs) && (Covers(UpperHead) || Covers(FullHead)) ? -1 : 0;
            case GroinChestHairOrFaceUncovered:
                return Covers(Torso) && Covers(Legs) && (Covers(UpperHead) || Covers(FullHead)) && (Covers(Eyes) || Covers(FullHead)) ? -1 : 0;
            case AnyCovered:
                // ThoughtWorker_Precept_AnyBodyPartCovered: any clothing, some not required by the pawn kind
                // (PawnUtility.HasClothingNotRequiredByKind), at a safe ambient temperature.
                var unrequired = worn.Length > 0 && facts.KindRequired.Count == 0 || worn.Any(Unrequired);
                return worn.Any(o => o.CountsAsClothingForNudity) && unrequired && facts.SafeAmbient ? 0 : -1;
            case AnyButGroinCovered:
                return facts.SafeAmbient && worn.Any(o => Unrequired(o) && (o.Groups.Contains(Torso) || !o.Groups.Contains(Legs))) ? 0 : -1;
            case AnyButHairOrFaceCovered:
                return worn.Any(o => o.CountsAsClothingForNudity && (o.Groups.Contains(Torso) || !o.Groups.Contains(UpperHead) && !o.Groups.Contains(FullHead))) ? 0 : -1;
            default:
                return -1;
        }
    }

    /// <summary>
    /// Per pawn, the distinct nonzero apparel mood shifts (delta / 100) the garments <paramref name="alternatives"/> overlay can
    /// bring the worn outfit to, in any order and combination (the outfits reachable by overlaying them, up to
    /// <see cref="ReachableCap"/>): the shifts the pawn's mood timeline integrates breaks at, so <see cref="Credits"/> can price
    /// each delivery's break days. A combination past the cap has no shift, and its credit is unassessed.
    /// </summary>
    public static IReadOnlyDictionary<string, double[]> BreakShifts(NeedsForecastRead needs, IReadOnlyDictionary<string, EquatableArray<GearOption>> outfits,
        IEnumerable<ForecastCommitment> alternatives)
    {
        var pawns = needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var result = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var group in alternatives.SelectMany(c => c.OutfitChanges).Where(c => c.Overlay).GroupBy(c => c.Pawn, StringComparer.Ordinal))
        {
            if (!pawns.TryGetValue(group.Key, out var pawn) || !outfits.TryGetValue(group.Key, out var worn) || MoodOf(pawn, worn) is not { } before) continue;
            var changes = group.Select(c => c.Items).Distinct().ToArray();
            var seen = new HashSet<string>(StringComparer.Ordinal) { Signature(worn) };
            var queue = new Queue<EquatableArray<GearOption>>([worn]);
            var shifts = new SortedSet<double>();
            while (queue.Count > 0 && seen.Count < ReachableCap)
            {
                var outfit = queue.Dequeue();
                foreach (var items in changes)
                {
                    var next = new ForecastOutfitChange(group.Key, items) { Overlay = true }.Apply(outfit);
                    if (!seen.Add(Signature(next))) continue;
                    queue.Enqueue(next);
                    if (MoodOf(pawn, next) is { } mood && mood != before) shifts.Add((mood - before) / 100);
                }
            }
            if (shifts.Count > 0) result[group.Key] = [.. shifts];
        }
        return result;
    }

    /// <summary>The outfits <see cref="BreakShifts"/> explores per pawn.</summary>
    internal const int ReachableCap = 64;

    /// <summary>
    /// The garment credit (#3816): per pawn, the garments the selected commitments overlay on the worn outfit, in the order they
    /// are delivered (their change tick: the delivery, or a retained bill's completion; <see cref="HarmLedger.ChangeTick"/>), each
    /// credited the gain its delivery adds: <see cref="ForecastHousing.GainPerDay"/> of the outfit's apparel mood delta from the
    /// worn outfit's once it is on less that before it, times the days from then to tier 2's horizon, a gain (negative) under
    /// <see cref="CreditKey"/>. For one garment this is its <see cref="MoodDelta"/> priced from its delivery; several share the
    /// thoughts they change together (a female pawn's shirt and pants together end her nakedness). A pawn whose outfit, apparel
    /// facts, mood, tick or shift is unread is named <see cref="CreditUnassessed"/> and credited nothing.
    /// </summary>
    internal static void Credits(ForecastCoupledOutcome outcome, ForecastCoupledContext context, IEnumerable<ForecastCommitment>? commitments,
        double horizon, List<ForecastInvestmentContribution> rows, SortedSet<string> findings)
    {
        var days = context.InvestmentHorizonDays;
        if (commitments is null || days <= 0) return;
        var changes = commitments.SelectMany(c => c.OutfitChanges.Where(o => o.Overlay).Select(o => (Change: o, At: HarmLedger.ChangeTick(c, outcome.OutfitTicks))));
        foreach (var group in changes.GroupBy(c => c.Change.Pawn, StringComparer.Ordinal))
        {
            var pawn = context.Needs.Pawns.FirstOrDefault(p => p.Pawn == group.Key);
            var outfit = pawn is not null && context.Outfits.TryGetValue(group.Key, out var worn) ? worn : (EquatableArray<GearOption>?)null;
            var before = pawn is not null && outfit is { } o ? MoodOf(pawn, o) : null;
            if (pawn is null || outfit is not { } current || before is not { } baseline || group.Any(c => c.At is null))
            {
                if (outfit is not { } unchanged || group.Any(c => ChangesThoughts(unchanged, c.Change.Items))) findings.Add(CreditUnassessed + group.Key);
                continue;
            }
            double previous = 0;
            var pending = new List<ForecastInvestmentContribution>();
            var assessed = true;
            foreach (var (change, at) in group.OrderBy(c => c.At!.Value))
            {
                current = change.Apply(current);
                var mood = MoodOf(pawn, current);
                var gain = mood is { } m ? ForecastHousing.GainPerDay(m - baseline, outcome.Needs.GetValueOrDefault(group.Key)?.Mood, context.MoodBreaks,
                    context.Value.WellbeingWeight, horizon / GameTime.TicksPerDay) : null;
                if (gain is not { } total) { assessed = false; break; }
                var units = RingClosure.CreditDays(days, at!.Value);
                if (total != previous && units > 0)
                    pending.Add(new("", group.Key, CreditKey(group.Key, string.Join("+", change.Items.Select(i => i.Definition.Value))), units, -(total - previous) * units));
                previous = total;
            }
            if (assessed) rows.AddRange(pending);
            else findings.Add(CreditUnassessed + group.Key);
        }
    }
}
