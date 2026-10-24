using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// What ranking and electives need beyond the care read: each pawn's profile for role weights (absent weighs 1),
/// whether a medical bed stands (HospitalBedReady) and the operations the player's health goals ask for.
/// </summary>
public sealed record SurgeryContext
{
    public IReadOnlyList<PawnProfile> Profiles { get; init; } = [];

    public bool HospitalBed { get; init; }

    /// <summary>The unmet health goals (#3508): each recipe is its pawn's first elective, an implant included.</summary>
    public IReadOnlyList<SurgeryGoal> Goals { get; init; } = [];
}

public static class RoundsFactsSurgeryExtensions
{
    /// <summary>The surgery ranking context over f: the hospital bed, the work profiles and the health goals.</summary>
    public static SurgeryContext SurgeryContextOf(this RoundsFacts f)
    {
        f.WorkProfiles.TryGetValue(out var profiles);
        return new SurgeryContext
        {
            Profiles = profiles,
            HospitalBed = RoundsRules.Positive(SurgeryRules.HospitalBedReady(f.Sleeping)),
            Goals = f.GoalSurgeries.Items,
        };
    }
}

/// <summary>Says why a patient's operation was not queued.</summary>
public enum SurgeryWantReason
{
    None,

    /// <summary>No candidate recipe has its ingredients (the part and medicine) on the map.</summary>
    [WireName("surgery_part_short")]
    PartShort,

    /// <summary>A stocked recipe exists but no eligible doctor clears the failure cap.</summary>
    [WireName("surgery_no_doctor")]
    NoDoctor,

    /// <summary>
    /// An eligible doctor clears the cap with an ideal bed and room but not with the colony's best medical bed;
    /// the hospital planner builds a Bed ward for it (SurgeryBedShortPatients).
    /// </summary>
    [WireName("surgery_bed_short")]
    BedShort,
}

/// <summary>One operation to queue as a medical ProductionBillIntent.</summary>
public sealed record SurgeryChoice
{
    public PawnId Pawn { get; init; }

    public string Recipe { get; init; } = "";

    /// <summary>The part item the recipe installs (empty when it installs none).</summary>
    public Resource Item { get; init; }

    public int Part { get; init; }

    /// <summary>An upgrade on a healthy part.</summary>
    public bool Elective { get; init; }

    public double Value { get; init; }
}

/// <summary>An operation a patient needs that cannot be queued yet; part-short wants feed bill and trade demand.</summary>
public sealed record SurgeryWant
{
    public PawnId Pawn { get; init; }

    public int Part { get; init; }

    public SurgeryWantReason Reason { get; init; }

    /// <summary>The part items of the part's candidate recipes that install one, best first.</summary>
    public IReadOnlyList<Resource> Items { get; init; } = [];

    /// <summary>The best candidate's rank, the same scale as SurgeryChoice.Value.</summary>
    public double Value { get; init; }
}

/// <summary>One review's queue (at most one per patient, best value first) and its wants.</summary>
public sealed record SurgerySelection
{
    public IReadOnlyList<SurgeryChoice> Queue { get; init; } = [];

    public IReadOnlyList<SurgeryWant> Wants { get; init; } = [];
}

public static partial class SurgeryRules
{
    /// <summary>The highest native failure chance a restore or a chronic cure may carry for the best eligible doctor.</summary>
    public const double RestoreFailureCap = 0.20;

    /// <summary>
    /// The highest failure chance an elective upgrade (a better part on a healthy one) may carry; electives also
    /// need a hospital bed and no restore or cure pending colony-wide.
    /// </summary>
    public const double ElectiveFailureCap = 0.05;

    /// <summary>No living colonist has an operation the planner serves. A queued bill does not settle it; the health change does.</summary>
    public static Fact<bool> SurgeryRecovered(Fact<EquatableArray<CarePawn>> pawns)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        return Known(!rows.Any(pawn => !pawn.Dead && pawn.Operations.Any(op => ServedSurgery(pawn, op) > 0)));
    }

    /// <summary>The operation's part index; NoSurgeryPart for a whole-body recipe.</summary>
    internal static int PartOf(SurgeryOperation op) => op.Part?.Index ?? SurgeryConstants.NoSurgeryPart;

    /// <summary>
    /// The capacity weight an operation gives back, zero when the planner does not serve it: a restore weighs its
    /// part; a cure recipe or a replacement part on a part carrying a chronic condition weighs the condition.
    /// </summary>
    internal static double ServedSurgery(CarePawn pawn, CareOperation care)
    {
        var op = care.Operation;
        if (op.Kind == SurgeryKind.Restore)
        {
            return PartWeight(op.Part?.DefName ?? "");
        }
        // An install of a part with no tier is an implant, not a replacement part.
        if (op.Kind != SurgeryKind.Cure && !(op.Kind == SurgeryKind.Install && care.Tier != 0))
        {
            return 0;
        }
        var part = op.Part?.Index;
        var weight = 0.0;
        foreach (var c in pawn.Conditions)
        {
            if (c.Part?.Index == part)
            {
                weight = Math.Max(weight, ChronicWeight.GetValueOrDefault(c.DefName, 0));
            }
        }
        return weight;
    }

    /// <summary>The capacity a chronic condition costs, which its cure or replacement gives back.</summary>
    private static readonly IReadOnlyDictionary<string, double> ChronicWeight = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["Cataract"] = 0.6,
        ["Blindness"] = 0.6,
        ["HearingLoss"] = 0.3,
        ["BadBack"] = 0.8,
        ["Frail"] = 0.8,
        ["Asthma"] = 0.8,
        ["ChemicalDamageModerate"] = 0.8,
        ["Dementia"] = 1,
        ["Alzheimers"] = 1,
        ["HeartArteryBlockage"] = 1,
        ["Cirrhosis"] = 1,
        ["Carcinoma"] = 1,
        ["ChemicalDamageSevere"] = 1,
    };

    /// <summary>An install on a healthy part whose part beats a natural one (bionic, archotech); implants and lesser parts are never electives unless a health goal names them.</summary>
    private static bool ElectiveUpgrade(CareOperation care) =>
        care.Operation.Kind == SurgeryKind.Install && care.Operation.Part is not null && care.Tier > 1;

    /// <summary>The rank of a health goal's operation (#3508): above every elective, so its part item is claimed first.</summary>
    private const double GoalSurgeryValue = 10;

    /// <summary>The recipes the health goals ask for on <paramref name="pawn"/>.</summary>
    private static HashSet<string> GoalRecipes(SurgeryContext ctx, PawnId pawn) =>
        ctx.Goals.Where(g => g.Pawn == pawn).Select(g => g.Recipe).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a health goal asks for the operation: its recipe is one of <paramref name="recipes"/>.</summary>
    private static bool GoalOperation(CareOperation care, HashSet<string> recipes) =>
        recipes.Count > 0 && recipes.Contains(care.Operation.Recipe);

    /// <summary>The goal operations the pawn could take within ElectiveFailureCap, by part.</summary>
    private static List<CareOperation> GoalOperations(IEnumerable<CareOperation> ops, HashSet<string> recipes) =>
        [.. ops.Where(op => GoalOperation(op, recipes) && SurgeryAcceptable(op.Operation, ElectiveFailureCap)).OrderBy(op => PartOf(op.Operation))];

    /// <summary>The first stocked goal operation as an elective choice; null when none is stocked.</summary>
    private static SurgeryChoice? GoalChoice(PawnId pawn, IEnumerable<CareOperation> ops, HashSet<string> recipes)
    {
        foreach (var care in GoalOperations(ops, recipes))
        {
            if (care.Operation.IngredientsOnMap)
            {
                return new SurgeryChoice
                {
                    Pawn = pawn,
                    Recipe = care.Operation.Recipe,
                    Item = care.Item,
                    Part = PartOf(care.Operation),
                    Elective = true,
                    Value = GoalSurgeryValue,
                };
            }
        }
        return null;
    }

    /// <summary>
    /// An elective upgrade could be queued now (stocked, within ElectiveFailureCap, a hospital bed) on a living colonist without a queued bill. It keeps the medical/surgery Need unmet; a blocked elective never does.
    ///
    /// A chosen elective whose part is missing (ChosenElectives) also holds it open while a usable bench can
    /// fabricate the part: the part bill is the work. fabricable is FabricableParts; null fabricates nothing.
    /// </summary>
    public static Fact<bool> ElectiveSurgeryOwed(Fact<EquatableArray<CarePawn>> pawns, SurgeryContext ctx, IReadOnlySet<Resource>? fabricable)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        if (!ctx.HospitalBed)
        {
            return Known(false);
        }
        foreach (var want in ChosenElectives(pawns, ctx))
        {
            if (FabricableItems(want.Items, fabricable).Count > 0)
            {
                return Known(true);
            }
        }
        foreach (var pawn in rows)
        {
            if (pawn.Dead || pawn.QueuedSurgeries > 0)
            {
                continue;
            }
            var goals = GoalRecipes(ctx, pawn.Id);
            foreach (var op in pawn.Operations)
            {
                if ((ElectiveUpgrade(op) || GoalOperation(op, goals)) && op.Operation.IngredientsOnMap && SurgeryAcceptable(op.Operation, ElectiveFailureCap))
                {
                    return Known(true);
                }
            }
        }
        return Known(false);
    }

    /// <summary>Some humanlike, non-prisoner bed is flagged medical.</summary>
    public static Fact<bool> HospitalBedReady(Fact<SleepingObservation> sleeping)
    {
        if (!sleeping.TryGetValue(out var obs))
        {
            return Unknown<bool>();
        }
        foreach (var bed in obs.Beds)
        {
            if (bed.Medical && bed.Humanlike && !bed.Prisoners)
            {
                return Known(true);
            }
        }
        return Known(false);
    }

    /// <summary>
    /// Ranks every living patient's served operations. A patient with a queued bill or an open surgery action
    /// (inFlight) gets nothing new. Each part picks its best stocked recipe (bionic, then prosthetic, then peg)
    /// that some eligible doctor performs within the kind's failure cap; the patient queues its most valuable
    /// part, weighted by its role (UpgradeRoleWeight). When electives are allowed, healthy parts compete with
    /// their gain over the natural part. A patient with a served operation queues that first; two patients never queue the same part item in one review.
    /// </summary>
    public static SurgerySelection SelectSurgery(Fact<EquatableArray<CarePawn>> pawns, IReadOnlySet<PawnId>? inFlight, SurgeryContext ctx)
    {
        var wants = new List<SurgeryWant>();
        var queue = new List<SurgeryChoice>();
        pawns.TryGetValue(out var rowList);
        var rows = rowList;
        var electives = ctx.HospitalBed;
        var profiles = new Dictionary<PawnId, PawnProfile>();
        foreach (var p in ctx.Profiles)
        {
            profiles[p.Id] = p;
        }
        foreach (var pawn in rows)
        {
            if (pawn.QuestProtected)
            {
                continue;
            }
            if (pawn.Dead || (inFlight?.Contains(pawn.Id) ?? false) || pawn.QueuedSurgeries > 0)
            {
                continue;
            }
            var profiled = profiles.TryGetValue(pawn.Id, out var profile);
            var ops = pawn.Operations;
            var parts = new Dictionary<int, List<CareOperation>>();
            var weights = new Dictionary<int, double>();
            var elective = new Dictionary<int, bool>();
            var order = new List<int>();
            foreach (var op in ops)
            {
                var wholeBody = op.Operation.Part is null;
                var part = PartOf(op.Operation);
                var weight = ServedSurgery(pawn, op);
                var name = op.Operation.Part?.DefName ?? "";
                if (weight <= 0 && electives && ElectiveUpgrade(op))
                {
                    weight = PartWeight(name);
                    elective[part] = true;
                }
                if (weight <= 0 || (wholeBody && op.Operation.Kind != SurgeryKind.Cure))
                {
                    continue;
                }
                if (profiled)
                {
                    weight *= PawnRules.UpgradeRoleWeight(profile!, name);
                }
                if (!parts.TryGetValue(part, out var group))
                {
                    order.Add(part);
                    group = [];
                    parts[part] = group;
                }
                group.Add(op);
                weights[part] = Math.Max(weights.GetValueOrDefault(part), weight);
            }
            order.Sort();
            SurgeryChoice? best = null;
            SurgeryChoice? bestElective = null;
            foreach (var part in order)
            {
                var isElective = elective.GetValueOrDefault(part);
                var (choice, want) = SelectPartSurgery(pawn.Id, part, weights[part], parts[part], isElective);
                if (choice is null)
                {
                    if (!isElective)
                    {
                        wants.Add(want);
                    }
                }
                else if (isElective)
                {
                    if (bestElective is null || choice.Value > bestElective.Value)
                    {
                        bestElective = choice;
                    }
                }
                else if (best is null || choice.Value > best.Value)
                {
                    best = choice;
                }
            }
            // A health goal's stocked operation is the patient's elective (#3508); a served operation outranks it.
            if (electives && GoalChoice(pawn.Id, ops, GoalRecipes(ctx, pawn.Id)) is { } goal)
            {
                bestElective = goal;
            }
            best ??= bestElective;
            if (best is not null)
            {
                queue.Add(best);
            }
        }
        queue = [.. queue.OrderByDescending(c => c.Value).ThenBy(c => c.Pawn)];
        // The stock read is a bool, not a count: an item goes to the pawn whose role gains most (ties by pawn id)
        // in a review, so two bills never race for one part.
        var kept = new List<SurgeryChoice>();
        var claimed = new HashSet<Resource>();
        foreach (var choice in queue)
        {
            if (choice.Elective && !claimed.Add(choice.Item))
            {
                continue;
            }
            kept.Add(choice);
        }
        return new SurgerySelection { Queue = kept, Wants = wants };
    }

    /// <summary>An elective takes ElectiveFailureCap and is valued by its gain over the natural part, so any served operation outranks it.</summary>
    private static (SurgeryChoice? Choice, SurgeryWant Want) SelectPartSurgery(PawnId pawn, int part, double weight, List<CareOperation> ops, bool elective)
    {
        var sorted = ops.OrderByDescending(OpTier).ToList();
        var topPart = sorted[0].Operation.Part?.DefName ?? "";
        var items = new List<Resource>();
        foreach (var op in sorted)
        {
            if (!op.Item.IsEmpty)
            {
                items.Add(op.Item);
            }
        }
        var reason = SurgeryWantReason.PartShort;
        var failureCap = RestoreFailureCap;
        var natural = 0.0;
        if (elective)
        {
            failureCap = ElectiveFailureCap;
            natural = 1;
        }
        foreach (var op in sorted)
        {
            if (!op.Operation.IngredientsOnMap)
            {
                continue;
            }
            if (!SurgeryAcceptable(op.Operation, failureCap))
            {
                if (reason != SurgeryWantReason.BedShort)
                {
                    reason = SurgeryBedBlocked(op.Operation, failureCap) ? SurgeryWantReason.BedShort : SurgeryWantReason.NoDoctor;
                }
                continue;
            }
            return (
                new SurgeryChoice
                {
                    Pawn = pawn,
                    Recipe = op.Operation.Recipe,
                    Item = op.Item,
                    Part = part,
                    Elective = elective,
                    Value = (OpTier(op) - natural) * weight,
                },
                BuildWant()
            );
        }
        return (null, BuildWant());

        SurgeryWant BuildWant() => new()
        {
            Pawn = pawn,
            Part = part,
            Reason = reason,
            Value = sorted[0].Tier * PartWeight(topPart),
            Items = items,
        };
    }

    /// <summary>A cure leaves the natural part; a part recipe gives its tier.</summary>
    private static double OpTier(CareOperation op) => op.Operation.Kind == SurgeryKind.Cure ? 1 : op.Tier;

    /// <summary>Some eligible doctor performs it within the failure cap, and it is neither a violation nor lethal. An unrated chance refuses.</summary>
    internal static bool SurgeryAcceptable(SurgeryOperation op, double failureCap) =>
        op.Chance is SurgeryChance.Rated rated && Acceptable(op, rated.Success, failureCap);

    private static bool Acceptable(SurgeryOperation op, double chance, double failureCap) =>
        SurgeryAcceptable(op.EligibleDoctors, chance, op.Violation, op.Lethal, failureCap);

    private static bool SurgeryAcceptable(int doctors, double chance, bool violation, bool lethal, double failureCap) =>
        doctors > 0 && 1 - chance <= failureCap + 1e-9 && !violation && !lethal;

    /// <summary>
    /// The replacement the autopilot would queue on a part an illness or a raid destroys (#3830, #3868), as <see cref="SelectPartSurgery"/>
    /// picks a restore: the candidates by tier, best first, the first whose part and medicine are on the map within
    /// <see cref="RestoreFailureCap"/>; else the first within the cap whose missing part or medicine <paramref name="stock"/> brings
    /// within <paramref name="days"/> of the loss, with what it waits on. None (with why) when no candidate's stock arrives
    /// (PartShort) or none clears the cap (BedShort when the ideal bed would clear it, else NoDoctor).
    /// </summary>
    internal static (OrganReplacementRead? Pick, SurgeryWantReason Reason, StockWait? Wait) RestorePick(IEnumerable<(OrganReplacementRead Read, double Tier)> candidates,
        StockArrival stock, double days)
    {
        var reason = SurgeryWantReason.PartShort;
        var ordered = candidates.OrderByDescending(c => c.Tier).Select(c => c.Read).ToArray();
        foreach (var read in ordered.Where(r => r.PartOnMap && r.MedicineOnMap))
        {
            if (Pickable(read, ref reason))
            {
                return (read, SurgeryWantReason.None, new StockWait(stock, []));
            }
        }

        foreach (var read in ordered.Where(r => !(r.PartOnMap && r.MedicineOnMap)))
        {
            var needs = Needs(read, stock);
            if (stock.Arrives(needs, days) && Pickable(read, ref reason))
            {
                return (read, SurgeryWantReason.None, new StockWait(stock, needs));
            }
        }

        return (null, reason, null);
    }

    private static bool Pickable(OrganReplacementRead read, ref SurgeryWantReason reason)
    {
        if (SurgeryAcceptable(read.EligibleDoctors, read.SuccessChance, read.Violation, read.Lethal, RestoreFailureCap))
        {
            return true;
        }

        if (reason != SurgeryWantReason.BedShort)
        {
            reason = SurgeryAcceptable(read.EligibleDoctors, read.DoctorSuccessChance, read.Violation, read.Lethal, RestoreFailureCap)
                ? SurgeryWantReason.BedShort : SurgeryWantReason.NoDoctor;
        }

        return false;
    }

    // The part a replacement installs when none is on the map, and its medicine (any kind) when the map holds too little.
    private static EquatableArray<StockNeed> Needs(OrganReplacementRead read, StockArrival stock)
    {
        var needs = new List<StockNeed>(2);
        if (!read.PartOnMap)
        {
            needs.Add(new StockNeed([new Resource(read.PartThing ?? throw new RequiredFactException("organ_replacement_part:" + read.Recipe))], 1));
        }

        if (!read.MedicineOnMap)
        {
            needs.Add(new StockNeed(stock.Medicines, read.MedicineCount));
        }

        return EquatableArray.From(needs);
    }

    /// <summary>The operation fails the cap as observed but passes it with the doctor's ideal-bed chance, so the bed or room is the blocker.</summary>
    private static bool SurgeryBedBlocked(SurgeryOperation op, double failureCap) =>
        op.Chance is SurgeryChance.Rated rated && Acceptable(op, rated.IdealSuccess, failureCap);

    /// <summary>The living colonists whose served operation waits only on a better bed or room: the hospital planner's surgical demand, which needs a Bed, not a sleeping spot.</summary>
    public static IReadOnlyList<PawnId> SurgeryBedShortPatients(Fact<EquatableArray<CarePawn>> pawns)
    {
        var ids = new List<PawnId>();
        var seen = new HashSet<PawnId>();
        foreach (var want in SelectSurgery(pawns, null, new SurgeryContext()).Wants)
        {
            if (want.Reason == SurgeryWantReason.BedShort && seen.Add(want.Pawn))
            {
                ids.Add(want.Pawn);
            }
        }
        return ids;
    }

    private static readonly (string Part, double Weight)[] PartWeights =
    [
        ("Leg", 1),
        ("Arm", 1),
        ("Shoulder", 1),
        ("Heart", 1),
        ("Lung", 1),
        ("Kidney", 1),
        ("Liver", 1),
        ("Stomach", 1),
        ("Hand", 0.8),
        ("Jaw", 0.7),
        ("Foot", 0.6),
        ("Eye", 0.6),
        ("Ear", 0.3),
        ("Nose", 0.1),
    ];

    /// <summary>How much of a colonist's capacities the part carries; the pawn's role scales it (UpgradeRoleWeight).</summary>
    private static double PartWeight(string part)
    {
        foreach (var (name, weight) in PartWeights)
        {
            if (part.Contains(name, StringComparison.Ordinal))
            {
                return weight;
            }
        }
        return 0.5;
    }
}
