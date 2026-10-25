using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using System.Globalization;
using Gender = RimGovernor.Brain.GameFacts.Defs.Gender;

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
    /// <summary>A royal title's and an ideoligion role's unmet apparel requirement thoughts (#3836); see <see cref="NeedApparel.Requirements"/>.</summary>
    public const string TitleApparel = "title_apparel_unmet";
    public const string RoleApparel = "role_apparel_unmet";
    /// <summary>A royal title's minimum apparel quality thought (#3841); see <see cref="NeedApparel.MinimumQuality"/>.</summary>
    public const string TitleApparelQuality = "title_apparel_quality_unmet";


    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    {
        Damaged, Naked, DeadMans, GroinUncovered, GroinOrChestUncovered, GroinChestOrHairUncovered, GroinChestHairOrFaceUncovered,
        AnyCovered, AnyButGroinCovered, AnyButHairOrFaceCovered, TitleApparel, RoleApparel, TitleApparelQuality,
    };

    // BodyPartGroupDefOf's defNames, as the catalog's apparel rows carry them (GearRules.GearCoreGroups reads the same strings).
    private const string Torso = "Torso", Legs = "Legs", UpperHead = "UpperHead", FullHead = "FullHead", Eyes = "Eyes";

    /// <summary>The Investment key a delivered garment credits its wearer under.</summary>
    public static string CreditKey(string pawn, string garment) => "apparel/" + pawn + "/" + garment;

    /// <summary>
    /// The summed offset of the apparel thoughts <paramref name="pawn"/> has wearing <paramref name="outfit"/> (its primary weapon
    /// aside), each thought's stage as its worker picks it. A thought without rows does not apply to the pawn (the native read
    /// leaves out a precept the ideoligion does not hold, and Naked under Ideology). Null when the pawn's apparel facts are
    /// unread or an active stage within the thought's stages has no row. A crafted garment's quality is a distribution (#3850),
    /// so this is the mood expected over <see cref="MoodOutcomes"/>.
    /// </summary>
    public static double? MoodOf(NeedsForecastPawn pawn, IEnumerable<GearOption> outfit) =>
        MoodOutcomes(pawn, outfit) is { } outcomes ? outcomes.Sum(o => o.Mood * o.Chance) : null;

    /// <summary>
    /// The apparel moods <paramref name="pawn"/> can have wearing <paramref name="outfit"/>, each with its chance (#3850): one, or two
    /// when a crafted garment's quality may leave it below the pawn's title minimum (the minimum-quality thought active, and not).
    /// Null as for <see cref="MoodOf"/>.
    /// </summary>
    public static (double Mood, double Chance)[]? MoodOutcomes(NeedsForecastPawn pawn, IEnumerable<GearOption> outfit)
    {
        if (pawn.Apparel is not { } facts) return null;
        var worn = outfit.Where(o => o.Slot != GearSlot.Primary).ToArray();
        double sum = 0, quality = 0;
        var below = BelowMinimumChance(facts, worn);
        foreach (var thought in pawn.ThoughtStages.Where(r => Roles.Contains(r.Role)).GroupBy(r => (r.Thought, r.Role)))
        {
            if (thought.Key.Role == TitleApparelQuality)
            {
                // ThoughtWorker_RoyalTitleApparelMinQualityNotMet: active while a worn garment with a quality (TryGetQuality) is below
                // a title in effect's minimum, a quality-less garment counting neither way; its one stage.
                if (below <= 0) continue;
                if (thought.FirstOrDefault(r => r.Stage == 0) is not { } first) return null;
                quality += first.Offset;
                continue;
            }
            var stage = StageOf(thought.Key.Role, facts, worn);
            if (stage < 0) continue;
            // Past the last defined stage the game clamps to it (ThoughtState.StageIndexFor): five tainted garments are the fourth stage.
            var last = thought.MaxBy(r => r.Stage)!;
            if ((stage > last.Stage ? last : thought.FirstOrDefault(r => r.Stage == stage)) is not { } row) return null;
            sum += row.Offset;
        }
        if (quality == 0 || below >= 1) return [(sum + quality, 1)];
        return [(sum + quality, below), (sum, 1 - below)];
    }

    /// <summary>The chance some worn garment with a quality is below the pawn's title minimum: each garment's quality is drawn on its own.</summary>
    private static double BelowMinimumChance(NeedApparel facts, GearOption[] worn)
    {
        var met = 1.0;
        foreach (var o in worn.Where(o => !o.Qualityless))
            met *= AtLeastChance(o, (int)facts.MinimumQuality);
        return 1 - met;
    }

    /// <summary>The apparel mood <paramref name="garments"/> overlaid on <paramref name="worn"/> adds; null when either side's is unknown.</summary>
    public static double? MoodDelta(NeedsForecastPawn pawn, IEnumerable<GearOption> worn, EquatableArray<GearOption> garments)
    {
        var outfit = worn.ToArray();
        return MoodOf(pawn, new ForecastOutfitChange(pawn.Pawn, garments) { Overlay = true }.Apply(outfit)) - MoodOf(pawn, outfit);
    }

    /// <summary>
    /// Whether overlaying <paramref name="garments"/> on <paramref name="worn"/> changes anything the apparel workers read of the
    /// outfit (the groups covered, the clothing and its groups, the damaged garments' condition, the tainted count, the lowest
    /// quality of the garments that have one): when it does not, its <see cref="MoodDelta"/> is zero for every pawn it meets no
    /// apparel requirement of (a pawn's own fact; the policy's required defs keep such a garment a candidate).
    /// </summary>
    public static bool ChangesThoughts(IEnumerable<GearOption> worn, EquatableArray<GearOption> garments)
    {
        var before = worn.ToArray();
        return Signature(before) != Signature(new ForecastOutfitChange("", garments) { Overlay = true }.Apply(before));
    }

    /// <summary>
    /// What the apparel workers read of <paramref name="outfit"/>; with <paramref name="facts"/>, also which of the pawn's apparel
    /// requirements it meets (a requirement names its own defs, so only a pawn's facts tell whether a garment meets one).
    /// </summary>
    private static string Signature(IEnumerable<GearOption> outfit, NeedApparel? facts = null)
    {
        var worn = outfit.Where(o => o.Slot != GearSlot.Primary).ToArray();
        var least = worn.Where(o => o.CareIfDamaged && !o.Locked).Select(o => o.Condition).DefaultIfEmpty(999).Min();
        return string.Join(",", worn.SelectMany(o => o.Groups).Distinct().Order(StringComparer.Ordinal)) + ";"
            + string.Join(",", worn.Where(o => o.CountsAsClothingForNudity).Select(o => o.Definition.Value + ":" + string.Join("+", o.Groups)).Order(StringComparer.Ordinal))
            + ";" + (least < 0.2 ? 1 : least < 0.5 ? 0 : -1) + ";" + worn.Count(o => o.Tainted) + ";" + (worn.Length > 0)
            + ";" + string.Join(",", Enumerable.Range(1, CraftedQualityRules.Categories - 1)
                .Select(m => worn.Where(o => !o.Qualityless).Aggregate(1.0, (met, o) => met * AtLeastChance(o, m)).ToString("R", CultureInfo.InvariantCulture)))
            + ";" + string.Concat((facts?.Requirements ?? []).Select(r => Met(r, worn) ? '1' : '0'));
    }

    /// <summary>The chance <paramref name="o"/> is at <paramref name="quality"/> or better.</summary>
    private static double AtLeastChance(GearOption o, int quality)
    {
        var chance = 0.0;
        for (var q = Math.Max(quality, 0); q < CraftedQualityRules.Categories; q++) chance += GearLoadoutRules.QualityChance(o, q);
        return chance;
    }

    /// <summary>ApparelRequirement.IsMet: some worn garment's def meets <paramref name="requirement"/>.</summary>
    private static bool Met(NeedApparelRequirement requirement, GearOption[] worn) => worn.Any(o => requirement.Meets.Contains(o.Definition.Value));

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
            case TitleApparel or RoleApparel:
                // ThoughtWorker_RoyalTitleApparelRequirementNotMet and Thought_IdeoRoleApparelRequirementNotMet: active while any of
                // the pawn's active requirements of the source is unmet, IsMet by any worn garment.
                return facts.Requirements.Any(r => r.Thought == role && !Met(r, worn)) ? 0 : -1;
            default:
                return -1;
        }
    }

    /// <summary>
    /// Per pawn, the distinct nonzero apparel mood shifts (delta / 100) the garments <paramref name="alternatives"/> overlay can
    /// bring the worn outfit to, in any order and combination (the outfits reachable by overlaying them, up to
    /// <see cref="ReachableCap"/>): the shifts the pawn's mood timeline integrates breaks at, so <see cref="Credits"/> can price
    /// each delivery's break days. A combination past the cap has no shift, and its credit throws (#3835).
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
            var seen = new HashSet<string>(StringComparer.Ordinal) { Signature(worn, pawn.Apparel) };
            var queue = new Queue<EquatableArray<GearOption>>([worn]);
            var shifts = new SortedSet<double>();
            while (queue.Count > 0 && seen.Count < ReachableCap)
            {
                var outfit = queue.Dequeue();
                foreach (var items in changes)
                {
                    var next = new ForecastOutfitChange(group.Key, items) { Overlay = true }.Apply(outfit);
                    if (!seen.Add(Signature(next, pawn.Apparel))) continue;
                    queue.Enqueue(next);
                    foreach (var (mood, _) in MoodOutcomes(pawn, next) ?? [])
                        if (mood != before) shifts.Add((mood - before) / 100);
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
    /// thoughts they change together (a female pawn's shirt and pants together end her nakedness). A pawn without an apparel
    /// tracker has no apparel thought: its garments earn nothing. Throws <see cref="RequiredFactException"/> (#3835) when the
    /// pawn's census row, worn outfit, an active thought stage's row, a change tick or the mood is unread.
    /// </summary>
    internal static void Credits(ForecastCoupledOutcome outcome, ForecastCoupledContext context, IEnumerable<ForecastCommitment>? commitments,
        double horizon, List<ForecastInvestmentContribution> rows)
    {
        var days = context.InvestmentHorizonDays;
        if (commitments is null || days <= 0) return;
        var changes = commitments.SelectMany(c => c.OutfitChanges.Where(o => o.Overlay).Select(o => (Change: o, At: HarmLedger.ChangeTick(c, outcome.OutfitTicks))));
        foreach (var group in changes.GroupBy(c => c.Change.Pawn, StringComparer.Ordinal))
        {
            var pawn = context.Needs.Pawns.FirstOrDefault(p => p.Pawn == group.Key) ?? throw new RequiredFactException("needs forecast pawn:" + group.Key);
            if (pawn.Apparel is null) continue;
            var current = context.Outfits.TryGetValue(group.Key, out var worn) ? worn : throw new RequiredFactException("worn outfit:" + group.Key);
            var baseline = MoodOf(pawn, current) ?? throw new RequiredFactException("apparel thought stage:" + group.Key);
            double previous = 0;
            foreach (var (change, tick) in group.Select(c => (c.Change, At: c.At ?? throw new RequiredFactException("outfit change tick:" + group.Key))).OrderBy(c => c.At))
            {
                current = change.Apply(current);
                // Expected over the outfit's moods (a crafted garment's quality, #3850), each priced at its own break shift.
                var moods = MoodOutcomes(pawn, current) ?? throw new RequiredFactException("apparel thought stage:" + group.Key);
                var total = moods.Sum(m => m.Chance * PlannedRoomCredits.MoodGain(m.Mood - baseline, group.Key, outcome, context, horizon));
                var units = RingClosure.CreditDays(days, tick);
                if (total != previous && units > 0)
                    rows.Add(new("", group.Key, CreditKey(group.Key, string.Join("+", change.Items.Select(i => i.Definition.Value))), units, -(total - previous) * units));
                previous = total;
            }
        }
    }
}
