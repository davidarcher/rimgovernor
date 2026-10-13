namespace RimGovernor.Brain.Policy;

/// <summary>
/// What ranking and electives need beyond the care read: each pawn's profile for role weights (absent weighs 1)
/// and whether a medical bed stands (HospitalBedReady).
/// </summary>
public sealed record SurgeryContext
{
    public IReadOnlyList<PawnProfile> Profiles { get; init; } = [];

    public bool HospitalBed { get; init; }

    /// <summary>Gates electives by each colonist's remaining personal share; the default value is ungated.</summary>
    public ElectiveShare Elective { get; init; } = new();
}

/// <summary>
/// Prices electives against personal shares. Of is the colonist's share
/// (observation.ColonyProjection.PersonalShareOf); null gates nothing. Items price the installed part; a part
/// with no price is not affordable while Of is set, as an unknown share is not.
/// </summary>
public sealed record ElectiveShare
{
    public ItemFacts Items { get; init; } = new();

    public Func<PawnId, PersonalShare>? Of { get; init; }

    /// <summary>Whether the pawn's remaining share covers the elective's installed part, within ElectiveShareSlack.</summary>
    internal bool Affordable(PawnId pawn, SurgeryOperation op)
    {
        if (Of is null)
        {
            return true;
        }
        double price;
        try
        {
            price = Items.MarketValue(op.Item);
        }
        catch (DomainException)
        {
            return false;
        }
        if (op.Item.IsEmpty)
        {
            return false;
        }
        return Of(pawn).Allows(price / (1 + PolicyFunctions.ElectiveShareSlack));
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

    public SurgeryKind Kind { get; init; }

    /// <summary>An upgrade on a healthy part.</summary>
    public bool Elective { get; init; }

    public double Value { get; init; }
}

/// <summary>An operation a patient needs that cannot be queued yet; part-short wants feed bill and trade demand.</summary>
public sealed record SurgeryWant
{
    public PawnId Pawn { get; init; }

    public int Part { get; init; }

    /// <summary>The best recipe wanted.</summary>
    public string Recipe { get; init; } = "";

    public SurgeryWantReason Reason { get; init; }

    /// <summary>Every candidate recipe for the part, best first; Value is the best one's rank, the same scale as SurgeryChoice.Value.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>The part items of the options that install one, in order.</summary>
    public IReadOnlyList<Resource> Items { get; init; } = [];

    public double Value { get; init; }
}

/// <summary>One review's queue (at most one per patient, best value first) and its wants.</summary>
public sealed record SurgerySelection
{
    public IReadOnlyList<SurgeryChoice> Queue { get; init; } = [];

    public IReadOnlyList<SurgeryWant> Wants { get; init; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>The medical-operation goal. It restores missing or destroyed parts; later kinds (cure, amputation, electives) add their own candidates to SelectSurgery.</summary>
    public static readonly ConcernId MaintainSurgery = new("MaintainSurgery");

    /// <summary>Keeps MaintainSurgery out of development arbitration: a queued bill is doctor work native assigns, not a reserved labor slot.</summary>
    internal const int SurgeryPriority = 2;

    /// <summary>The highest native failure chance a restore or a chronic cure may carry for the best eligible doctor.</summary>
    public const double RestoreFailureCap = 0.20;

    /// <summary>
    /// The highest failure chance an elective upgrade (a better part on a healthy one) may carry; electives also
    /// need a hospital bed and no restore or cure pending colony-wide.
    /// </summary>
    public const double ElectiveFailureCap = 0.05;

    /// <summary>
    /// The stateless hysteresis of the elective gate: an elective is affordable while its part's market value is
    /// within (1+slack) x the colonist's remaining share. Electives are only offered once the part is on the map,
    /// so the part is already bought or fabricated and wealth jitter around the price must not strand it.
    /// </summary>
    public const double ElectiveShareSlack = 0.1;

    /// <summary>No living colonist has an operation the planner serves. A queued bill does not settle it; the health change does.</summary>
    public static Fact<bool> SurgeryRecovered(Fact<IReadOnlyList<CarePawn>> pawns)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        foreach (var pawn in rows)
        {
            if (pawn.Dead.TryGetValue(out var dead) && dead)
            {
                continue;
            }
            if (!pawn.Operations.TryGetValue(out var ops))
            {
                return Unknown<bool>();
            }
            foreach (var op in ops)
            {
                var (served, known) = ServedSurgery(pawn, op);
                if (!known)
                {
                    return Unknown<bool>();
                }
                if (served > 0)
                {
                    return Known(false);
                }
            }
        }
        return Known(true);
    }

    /// <summary>
    /// The capacity weight an operation gives back, zero when the planner does not serve it: a restore weighs its
    /// part; a cure recipe or a replacement part on a part carrying a chronic condition weighs the condition.
    /// Known is false when a cure's conditions are unread.
    /// </summary>
    private static (double Weight, bool Known) ServedSurgery(CarePawn pawn, SurgeryOperation op)
    {
        op.PartDefName.TryGetValue(out var name);
        switch (op.Kind)
        {
            case SurgeryKind.Restore:
                return (PartWeight(name ?? ""), true);
            case SurgeryKind.Cure:
                break;
            case SurgeryKind.Install:
                if (op.Tier == 0)
                {
                    return (0, true); // an implant, not a replacement part
                }
                break;
            default:
                return (0, true);
        }
        if (!pawn.Conditions.TryGetValue(out var conditions))
        {
            return (0, false);
        }
        var partKnown = op.PartIndex.TryGetValue(out var part);
        var weight = 0.0;
        foreach (var c in conditions)
        {
            c.DefName.TryGetValue(out var def);
            var atKnown = c.PartIndex.TryGetValue(out var at);
            if (atKnown == partKnown && (!atKnown || at == part))
            {
                weight = Math.Max(weight, ChronicWeight.GetValueOrDefault(def ?? "", 0));
            }
        }
        return (weight, true);
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

    /// <summary>A medical bed stands and no living colonist has a surgery bill queued or a served operation (restore, cure or chronic replacement) offered.</summary>
    private static bool ElectivesAllowed(IReadOnlyList<CarePawn> rows, bool hospital)
    {
        if (!hospital)
        {
            return false;
        }
        foreach (var pawn in rows)
        {
            if (pawn.Dead.TryGetValue(out var dead) && dead)
            {
                continue;
            }
            // The stock read is a bool: one surgery bill anywhere holds electives, or a later review books the
            // same part twice.
            if (!pawn.QueuedSurgeries.TryGetValue(out var queued) || queued > 0)
            {
                return false;
            }
            if (!pawn.Operations.TryGetValue(out var ops))
            {
                return false;
            }
            foreach (var op in ops)
            {
                var (served, known) = ServedSurgery(pawn, op);
                if (!known || served > 0)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>An install on a healthy part whose part beats a natural one (bionic, archotech); implants and lesser parts are never electives.</summary>
    private static bool ElectiveUpgrade(SurgeryOperation op) =>
        op.Kind == SurgeryKind.Install && op.Recipe.IsKnown && op.PartIndex.IsKnown && op.Tier > 1;

    /// <summary>
    /// An elective upgrade could be queued now (stocked, within ElectiveFailureCap, a hospital bed, nothing
    /// served pending, the part within the colonist's remaining share) on a living colonist without a queued
    /// bill. It keeps MaintainSurgery open; blocked or unaffordable electives never do.
    ///
    /// A chosen elective whose part is missing (ChosenElective) also holds it open while a usable bench can
    /// fabricate the part: the part bill is the work. fabricable is FabricableParts; null fabricates nothing.
    /// </summary>
    public static Fact<bool> ElectiveSurgeryOwed(Fact<IReadOnlyList<CarePawn>> pawns, SurgeryContext ctx, IReadOnlySet<Resource>? fabricable)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        if (!ElectivesAllowed(rows, ctx.HospitalBed))
        {
            return Known(false);
        }
        var gate = ctx.Elective;
        var (want, ok) = ChosenElective(pawns, ctx);
        if (ok && FabricableItems(want.Items, fabricable).Count > 0)
        {
            return Known(true);
        }
        foreach (var pawn in rows)
        {
            if (!pawn.Dead.TryGetValue(out var dead) || dead)
            {
                continue;
            }
            if (!pawn.QueuedSurgeries.TryGetValue(out var queued) || queued > 0)
            {
                continue;
            }
            pawn.Operations.TryGetValue(out var ops);
            foreach (var op in ops ?? [])
            {
                if (ElectiveUpgrade(op)
                    && op.IngredientsOnMap.TryGetValue(out var stocked)
                    && stocked
                    && SurgeryAcceptable(op, ElectiveFailureCap)
                    && gate.Affordable(pawn.Id, op))
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
            if (Positive(bed.Medical) && Positive(bed.Humanlike) && !Positive(bed.Prisoners))
            {
                return Known(true);
            }
        }
        return Known(false);
    }

    /// <summary>
    /// Whether a living colonist or prisoner has a queued medical bill. A native doctor job carries the bill, so
    /// it is clock work until the operation completes or the bill ends; an unknown count is not work.
    /// </summary>
    public static bool SurgeryBillsQueued(Fact<IReadOnlyList<CarePawn>> pawns, Fact<IReadOnlyList<PrisonerFacts>> prisoners)
    {
        static bool Queued(Fact<bool> dead, Fact<int> count) =>
            dead.TryGetValue(out var d) && !d && count.TryGetValue(out var n) && n > 0;
        pawns.TryGetValue(out var rows);
        foreach (var pawn in rows ?? [])
        {
            if (Queued(pawn.Dead, pawn.QueuedSurgeries))
            {
                return true;
            }
        }
        prisoners.TryGetValue(out var held);
        foreach (var row in held ?? [])
        {
            if (Queued(row.Dead, row.QueuedSurgeries))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Ranks every living patient's served operations. A patient with a queued bill or an open surgery action
    /// (inFlight) gets nothing new. Each part picks its best stocked recipe (bionic, then prosthetic, then peg)
    /// that some eligible doctor performs within the kind's failure cap; the patient queues its most valuable
    /// part, weighted by its role (UpgradeRoleWeight). When electives are allowed, healthy parts whose installed
    /// part fits the colonist's remaining share (ctx.Elective) compete with their gain over the natural part, one
    /// elective per review colony-wide. Served operations never pass the share gate.
    /// </summary>
    public static SurgerySelection SelectSurgery(Fact<IReadOnlyList<CarePawn>> pawns, IReadOnlySet<PawnId>? inFlight, SurgeryContext ctx)
    {
        var wants = new List<SurgeryWant>();
        var queue = new List<SurgeryChoice>();
        pawns.TryGetValue(out var rowList);
        var rows = rowList ?? [];
        var electives = ElectivesAllowed(rows, ctx.HospitalBed);
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
            if (!pawn.Dead.TryGetValue(out var dead) || dead || (inFlight?.Contains(pawn.Id) ?? false))
            {
                continue;
            }
            if (!pawn.QueuedSurgeries.TryGetValue(out var queued) || queued > 0)
            {
                continue;
            }
            var profiled = profiles.TryGetValue(pawn.Id, out var profile);
            pawn.Operations.TryGetValue(out var ops);
            var parts = new Dictionary<int, List<SurgeryOperation>>();
            var weights = new Dictionary<int, double>();
            var elective = new Dictionary<int, bool>();
            var order = new List<int>();
            foreach (var op in ops ?? [])
            {
                var pk = op.PartIndex.TryGetValue(out var part);
                if (!pk)
                {
                    part = -1; // whole-body cure
                }
                var (weight, _) = ServedSurgery(pawn, op);
                op.PartDefName.TryGetValue(out var name);
                if (weight <= 0 && electives && ElectiveUpgrade(op) && ctx.Elective.Affordable(pawn.Id, op))
                {
                    weight = PartWeight(name ?? "");
                    elective[part] = true;
                }
                if (!op.Recipe.IsKnown || weight <= 0 || (!pk && op.Kind != SurgeryKind.Cure))
                {
                    continue;
                }
                if (profiled)
                {
                    weight *= UpgradeRoleWeight(profile!, name ?? "");
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
                else if (best is null || choice.Value > best.Value)
                {
                    best = choice;
                }
            }
            if (best is not null)
            {
                queue.Add(best);
            }
        }
        queue = [.. queue.OrderByDescending(c => c.Value).ThenBy(c => c.Pawn)];
        // The stock read is a bool, not a count: one elective per review, to the pawn whose role gains most
        // (ties by pawn id), so two bills never race for one part.
        var kept = new List<SurgeryChoice>();
        var seenElective = false;
        foreach (var choice in queue)
        {
            if (choice.Elective)
            {
                if (seenElective)
                {
                    continue;
                }
                seenElective = true;
            }
            kept.Add(choice);
        }
        return new SurgerySelection { Queue = kept, Wants = wants };
    }

    /// <summary>An elective takes ElectiveFailureCap and is valued by its gain over the natural part, so any served operation outranks it.</summary>
    private static (SurgeryChoice? Choice, SurgeryWant Want) SelectPartSurgery(PawnId pawn, int part, double weight, List<SurgeryOperation> ops, bool elective)
    {
        var sorted = ops.OrderByDescending(OpTier).ToList();
        sorted[0].Recipe.TryGetValue(out var top);
        sorted[0].PartDefName.TryGetValue(out var topPart);
        var options = new List<string>();
        var items = new List<Resource>();
        foreach (var op in sorted)
        {
            op.Recipe.TryGetValue(out var recipe);
            options.Add(recipe ?? "");
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
            if (!op.IngredientsOnMap.TryGetValue(out var stocked) || !stocked)
            {
                continue;
            }
            if (!SurgeryAcceptable(op, failureCap))
            {
                if (reason != SurgeryWantReason.BedShort)
                {
                    reason = SurgeryBedBlocked(op, failureCap) ? SurgeryWantReason.BedShort : SurgeryWantReason.NoDoctor;
                }
                continue;
            }
            op.Recipe.TryGetValue(out var recipe);
            return (
                new SurgeryChoice
                {
                    Pawn = pawn,
                    Recipe = recipe ?? "",
                    Item = op.Item,
                    Part = part,
                    Kind = op.Kind,
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
            Recipe = top ?? "",
            Reason = reason,
            Value = sorted[0].Tier * PartWeight(topPart ?? ""),
            Options = options,
            Items = items,
        };
    }

    /// <summary>A cure leaves the natural part; a part recipe gives its tier.</summary>
    private static double OpTier(SurgeryOperation op) => op.Kind == SurgeryKind.Cure ? 1 : op.Tier;

    /// <summary>Some eligible doctor performs it within the failure cap, and it is neither a violation nor lethal. Unknown facts refuse.</summary>
    private static bool SurgeryAcceptable(SurgeryOperation op, double failureCap)
    {
        var dk = op.EligibleDoctors.TryGetValue(out var doctors);
        var ck = op.SuccessChance.TryGetValue(out var chance);
        var vk = op.Violation.TryGetValue(out var violation);
        var lk = op.Lethal.TryGetValue(out var lethal);
        return dk && ck && vk && lk && doctors > 0 && 1 - chance <= failureCap + 1e-9 && !violation && !lethal;
    }

    /// <summary>The operation fails the cap as observed but passes it with the doctor's ideal-bed chance, so the bed or room is the blocker.</summary>
    private static bool SurgeryBedBlocked(SurgeryOperation op, double failureCap)
    {
        if (!op.DoctorSuccessChance.TryGetValue(out var ideal))
        {
            return false;
        }
        return SurgeryAcceptable(op with { SuccessChance = Known(ideal) }, failureCap);
    }

    /// <summary>The living colonists whose served operation waits only on a better bed or room: the hospital planner's surgical demand, which needs a Bed, not a sleeping spot.</summary>
    public static IReadOnlyList<PawnId> SurgeryBedShortPatients(Fact<IReadOnlyList<CarePawn>> pawns)
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
