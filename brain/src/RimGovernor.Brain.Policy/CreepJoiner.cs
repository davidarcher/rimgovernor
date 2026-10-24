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
    public bool Revealed(CreepJoinerPawn p) =>
        p.CreepJoiner is { } joiner && (joiner.DownsideTriggered || p.Traits.Any(Traits.Contains) || p.Hediffs.Any(Hediffs.Contains));

    /// <summary>
    /// Whether the pawn is a creepjoiner whose downside has not shown. A pawn that is no creepjoiner is not: the
    /// gear census flags only a creepjoiner (GearRoleInput.UnrevealedCreepjoiner).
    /// </summary>
    public bool Unrevealed(CreepJoinerPawn p) => p.CreepJoiner is not null && !Revealed(p);

    /// <summary>
    /// The plain-English reason a colonist must not hold a weapon yet, empty when nothing holds it back: a
    /// creepjoiner whose downside has not shown. A pawn that is no creepjoiner is never held.
    /// </summary>
    public string ArmsHold(CreepJoinerPawn p) =>
        Unrevealed(p) ? "it is a creepjoiner whose downside is not revealed yet, so it is not armed" : "";

    /// <summary>
    /// The drops the colony owes: every available colonist held back from arms who holds a weapon, sorted by
    /// pawn id. Owed is known true with a drop, known false when none is owed and every held colonist was
    /// read, and unknown when none is owed but a held colonist's weapon is unread.
    /// </summary>
    public (IReadOnlyList<WeaponDrop> Drops, Fact<bool> Owed) WeaponDrops(IReadOnlyList<CreepJoinerHand> hands)
    {
        var unread = false;
        var drops = new List<WeaponDrop>();
        foreach (var h in hands)
        {
            if (ArmsHold(h.Facts).Length == 0)
            {
                continue;
            }
            if (!h.Weapon.TryGetValue(out var weapon))
            {
                unread = true;
            }
            else if (weapon.Length != 0 && h.Available)
            {
                drops.Add(new WeaponDrop(h.Pawn));
            }
        }
        var sorted = drops.OrderBy(d => d.Pawn).ToList();
        if (sorted.Count > 0)
        {
            return (sorted, Known(true));
        }
        return ([], unread ? Unknown<bool>() : Known(false));
    }

    /// <summary>
    /// Whether the colony holds the creepjoiner apart: its downside is known not to have shown and the record
    /// holds no finished inspection of it.
    /// </summary>
    internal bool HoldsApart(CreepJoinerHand h, CreepJoinerRecord record) =>
        record.Inspections.GetValueOrDefault(h.Pawn) != InspectionStage.Done && Unrevealed(h.Facts);

    /// <summary>The creepjoiners the colony holds apart, sorted by pawn id.</summary>
    public EquatableArray<PawnId> Isolated(IReadOnlyList<CreepJoinerHand> hands, CreepJoinerRecord record) =>
        EquatableArray.From(hands.Where(h => HoldsApart(h, record)).Select(h => h.Pawn).Order());

    /// <summary>
    /// The allowed-area changes the colony owes its creepjoiners, sorted by pawn id. <paramref name="area"/> is
    /// the Isolation area's load id (empty if the map has none); <paramref name="ready"/> is whether the
    /// isolation room stands with its bed. A creepjoiner held apart, available and not hungry is moved into the
    /// area once ready; one in the area that is no longer held apart, or is hungry, is released. A pawn whose
    /// hunger is unread is moved in never, and released only when no longer held apart.
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
            if (h.Facts.CreepJoiner is null)
            {
                continue;
            }
            var apart = HoldsApart(h, record);
            var hk = h.Hungry.TryGetValue(out var hungry);
            if (h.Area == area && (!apart || (hk && hungry)))
            {
                result.Add(new IsolationMove(h.Pawn, ""));
            }
            else if (h.Area != area && ready && apart && hk && !hungry && h.Available)
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
            if (h.Facts.CreepJoiner is null)
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
            if (Revealed(h.Facts))
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

/// <summary>
/// One colonist's facts for the downside check: its creepjoiner tracker facts (null for a pawn that is no
/// creepjoiner) and the def names of its traits and of its visible hediffs.
/// </summary>
public sealed record CreepJoinerPawn
{
    public required CreepJoinerState? CreepJoiner { get; init; }

    public required EquatableArray<string> Traits { get; init; }

    public required EquatableArray<string> Hediffs { get; init; }
}

/// <summary>
/// One colonist's facts for the weapon drop and the inspection: the downside check's facts, whether the
/// colonist can take an order now, the thing id of the weapon in its hands (known empty for none), its
/// available surgical operations and its queued surgery bills.
/// </summary>
public sealed record CreepJoinerHand
{
    public required PawnId Pawn { get; init; }

    public required CreepJoinerPawn Facts { get; init; }

    public required bool Available { get; init; }

    public Fact<string> Weapon { get; init; }

    /// <summary>The colonist's native operations; a dead colonist has none.</summary>
    public required EquatableArray<SurgeryOperation> Operations { get; init; }

    public required int QueuedSurgeries { get; init; }

    public required EquatableArray<string> QueuedRecipes { get; init; }

    /// <summary>The load id of the colonist's allowed area (empty when unrestricted).</summary>
    public required string Area { get; init; }

    /// <summary>Whether it is below the game's fed band.</summary>
    public Fact<bool> Hungry { get; init; }

    /// <summary>Whether the pawn has no inspection bill queued.</summary>
    internal bool InspectionEnded(IReadOnlySet<string> recipes) => QueuedSurgeries == 0 || !QueuedRecipes.Any(recipes.Contains);

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
