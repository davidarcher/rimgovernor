using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>One colonist ordered to drop the weapon it holds.</summary>
public sealed record WeaponDrop(PawnId Pawn);

/// <summary>
/// What the game's creepjoiner downside defs add to the colonist who has one: the trait and hediff def names,
/// over every downside def the catalog lists. A pawn showing one of them has a visible downside; which downside
/// a particular creepjoiner has is hidden information and is never read. Empty without Anomaly.
/// </summary>
public sealed record CreepJoinerDownsides
{
    public IReadOnlySet<string> Traits { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> Hediffs { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Measures whether a creepjoiner's downside has shown: the game fired it (the tracker's triggered flag),
    /// or the pawn carries a trait or a visible hediff a downside def adds. A pawn that is no creepjoiner has none.
    /// </summary>
    public bool Revealed(O.PawnState p) =>
        p.Anomaly is PawnAnomalyState.Active { Facts.Creepjoiner: { } joiner }
        && (joiner.DownsideTriggered || p.Biography.Traits.Any(trait => Traits.Contains(trait.DefName))
            || p.Health.Hediffs.Any(hediff => hediff.Visible && Hediffs.Contains(hediff.DefName)));

    /// <summary>
    /// Whether the pawn is a creepjoiner whose downside has not shown. A pawn that is no creepjoiner is not: the
    /// gear census flags only a creepjoiner (GearRoleInput.UnrevealedCreepjoiner).
    /// </summary>
    public bool Unrevealed(O.PawnState p) => p.Anomaly is PawnAnomalyState.Active { Facts.Creepjoiner: not null } && !Revealed(p);

    /// <summary>
    /// The plain-English reason a colonist must not hold a weapon yet, empty when nothing holds it back: a
    /// creepjoiner whose downside has not shown. A pawn that is no creepjoiner is never held.
    /// </summary>
    public string ArmsHold(O.PawnState p) =>
        Unrevealed(p) ? "it is a creepjoiner whose downside is not revealed yet, so it is not armed" : "";

    /// <summary>
    /// The drops the colony owes: every available colonist held back from arms who holds a weapon, sorted by
    /// pawn id. A drop is owed exactly when the list is not empty.
    /// </summary>
    public IReadOnlyList<WeaponDrop> WeaponDrops(IReadOnlyList<CreepJoinerHand> hands) =>
        [.. hands.Where(h => ArmsHold(h.Observed).Length != 0 && h.Weapon is not null && h.Available).Select(h => new WeaponDrop(h.Pawn)).OrderBy(d => d.Pawn)];

    /// <summary>
    /// Whether the colony holds the creepjoiner apart: its downside is known not to have shown and the record
    /// holds no finished inspection of it.
    /// </summary>
    internal bool HoldsApart(CreepJoinerHand h, CreepJoinerRecord record) =>
        record.Inspections.GetValueOrDefault(h.Pawn) != InspectionStage.Done && Unrevealed(h.Observed);

    /// <summary>The creepjoiners the colony holds apart, sorted by pawn id.</summary>
    public EquatableArray<PawnId> Isolated(IReadOnlyList<CreepJoinerHand> hands, CreepJoinerRecord record) =>
        EquatableArray.From(hands.Where(h => HoldsApart(h, record)).Select(h => h.Pawn).Order());

    /// <summary>
    /// The allowed-area changes the colony owes its creepjoiners, sorted by pawn id. <paramref name="area"/> is
    /// the Isolation area's load id (empty if the map has none); <paramref name="ready"/> is whether the
    /// isolation room stands with its bed. A creepjoiner held apart, available and not hungry is moved into the
    /// area once ready; one in the area that is no longer held apart, or is hungry, is released.
    /// </summary>
    public IReadOnlyList<IsolationMove> IsolationMoves(IReadOnlyList<CreepJoinerHand> hands, CreepJoinerRecord record, string area, bool ready)
    {
        if (area.Length == 0)
        {
            return [];
        }
        var result = new List<IsolationMove>();
        foreach (var h in hands)
        {
            if (h.Observed.Anomaly is not PawnAnomalyState.Active { Facts.Creepjoiner: not null })
            {
                continue;
            }
            var apart = HoldsApart(h, record);
            if (h.Area == area && (!apart || h.Hungry))
            {
                result.Add(new IsolationMove(h.Pawn, ""));
            }
            else if (h.Area != area && ready && apart && !h.Hungry && h.Available)
            {
                result.Add(new IsolationMove(h.Pawn, area));
            }
        }
        return result.OrderBy(m => m.Pawn).ToList();
    }

    /// <summary>
    /// Finds what the colony owes its creepjoiners. A creepjoiner needs an inspection while its downside is
    /// unrevealed and the record holds none for it. An available colonist with the inspection operation on
    /// offer, a doctor able to perform it and its ingredients on the map is ordered one; the operation is the
    /// offered one whose recipe is in recipes, on its lowest part index. A colonist whose inspection operation
    /// cannot be ordered is not (and says why in Waiting). An
    /// ordered inspection ends when no inspection bill is queued on the pawn any more.
    /// </summary>
    public Inspections Inspections(IReadOnlyList<CreepJoinerHand> hands, IReadOnlySet<string> recipes, CreepJoinerRecord record)
    {
        var inspections = new Dictionary<PawnId, InspectionStage>();
        var present = new HashSet<PawnId>(hands.Select(h => h.Pawn));
        foreach (var (pawn, stage) in record.Inspections)
        {
            if (present.Contains(pawn))
            {
                inspections[pawn] = stage;
            }
        }
        var orders = new List<InspectionOrder>();
        var waiting = new List<string>();
        foreach (var h in hands)
        {
            if (h.Observed.Anomaly is not PawnAnomalyState.Active { Facts.Creepjoiner: not null })
            {
                continue;
            }
            var stage = record.Inspections.GetValueOrDefault(h.Pawn);
            if (stage == InspectionStage.Ordered)
            {
                if (h.InspectionEnded(recipes))
                {
                    inspections[h.Pawn] = InspectionStage.Done;
                }
                continue;
            }
            if (stage != InspectionStage.None)
            {
                continue;
            }
            if (Revealed(h.Observed))
            {
                continue;
            }
            var (order, why) = h.InspectionOrder(recipes);
            if (why.Length != 0)
            {
                waiting.Add($"creepjoiner {h.Pawn} cannot be inspected: {why}");
                continue;
            }
            orders.Add(order!);
            inspections[h.Pawn] = InspectionStage.Ordered;
        }
        return new Inspections
        {
            Orders = EquatableArray.From(orders.OrderBy(o => o.Pawn)),
            Record = new CreepJoinerRecord { Inspections = inspections },
            Waiting = EquatableArray.From(waiting),
        };
    }
}

/// <summary>One colonist's canonical native row for weapon drops, isolation and inspection.</summary>
public sealed record CreepJoinerHand
{
    public required O.PawnState Observed { get; init; }

    public PawnId Pawn => new(Observed.Pawn.Id);

    public bool Available => !Observed.Dead && !Observed.Downed && !Observed.Drafted && Observed.MentalState is null;

    /// <summary>The equipped primary's thing id; null is the observed unarmed state.</summary>
    public string? Weapon => Observed.Equipment.Armed
        ? Observed.Equipment.PrimaryId ?? throw new RequiredFactException($"armed colonist {Pawn} primary id")
        : null;

    /// <summary>A dead pawn may have no surgery block; a living pawn must have one.</summary>
    public O.PawnSurgery? Surgery => Observed.Health.Surgery
        ?? (Observed.Dead ? null : throw new RequiredFactException($"living colonist {Pawn} surgery facts"));

    public EquatableArray<SurgeryOperation> Operations => Surgery is { } surgery ? surgery.Operations : [];

    /// <summary>An unset native area means unrestricted.</summary>
    public string Area => Observed.Settings.AllowedAreaId ?? "";

    /// <summary>A pawn without a food need is never hungry.</summary>
    public bool Hungry => Observed.Needs.Food is FoodNeed.Has food
        && food.Hunger != RimGovernor.Contracts.Wire.Defs.HungerCategory.Fed;

    /// <summary>Whether the pawn has no inspection bill queued.</summary>
    internal bool InspectionEnded(IReadOnlySet<string> recipes) =>
        Surgery is not { } surgery || !surgery.SurgeryBills.Any(bill => recipes.Contains(bill.Recipe));

    /// <summary>Picks the inspection operation to queue, or says in plain English why none can be.</summary>
    internal (InspectionOrder? Order, string Why) InspectionOrder(IReadOnlySet<string> recipes)
    {
        if (recipes.Count == 0)
        {
            return (null, "the game defines no surgical inspection recipe");
        }
        if (!Available)
        {
            return (null, "it cannot take an order now");
        }
        if (!InspectionEnded(recipes))
        {
            return (null, "an inspection bill is already queued");
        }
        SurgeryOperation? best = null;
        var bestPart = 0;
        foreach (var op in Operations)
        {
            if (!recipes.Contains(op.Recipe))
            {
                continue;
            }
            var part = SurgeryRules.PartOf(op);
            if (best is null || part < bestPart)
            {
                best = op;
                bestPart = part;
            }
        }
        if (best is null)
        {
            return (null, "the game offers no surgical inspection on it");
        }
        if (best.EligibleDoctors == 0)
        {
            return (null, "no eligible doctor");
        }
        if (!best.IngredientsOnMap)
        {
            return (null, "the inspection's ingredients are not on the map");
        }
        return (new InspectionOrder(Pawn, best.Recipe, bestPart), "");
    }
}
