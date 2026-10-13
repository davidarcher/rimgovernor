namespace RimGovernor.Brain.Policy;

/// <summary>One colonist ordered to drop the weapon it holds.</summary>
public sealed record WeaponDrop(PawnId Pawn, string Weapon);

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
    /// or the pawn carries a trait or a visible hediff a downside def adds. Unknown while nothing shows and a
    /// fact the answer rests on is unread. The pawn must be a creepjoiner.
    /// </summary>
    public Fact<bool> Revealed(CreepJoinerPawn p)
    {
        p.CreepJoiner.TryGetValue(out var joiner);
        if (joiner is null)
        {
            return Unknown<bool>();
        }
        if (joiner.DownsideTriggered.TryGetValue(out var triggered) && triggered)
        {
            return Known(true);
        }
        var tk = p.Traits.TryGetValue(out var traits);
        var hk = p.Hediffs.TryGetValue(out var hediffs);
        if ((tk && traits.Any(Traits.Contains)) || (hk && hediffs.Any(Hediffs.Contains)))
        {
            return Known(true);
        }
        if (!joiner.DownsideTriggered.IsKnown || !tk || !hk)
        {
            return Unknown<bool>();
        }
        return Known(false);
    }

    /// <summary>
    /// Whether the pawn is a creepjoiner whose downside has not shown (or cannot be read to have shown). A
    /// pawn that is no creepjoiner, or whose creepjoiner status is unread, is not: the gear census flags only a
    /// known creepjoiner (GearRoleInput.UnrevealedCreepjoiner).
    /// </summary>
    public bool Unrevealed(CreepJoinerPawn p)
    {
        if (!p.CreepJoiner.TryGetValue(out var joiner) || joiner is null)
        {
            return false;
        }
        return !Revealed(p).TryGetValue(out var revealed) || !revealed;
    }

    /// <summary>
    /// The plain-English reason a colonist must not hold a weapon yet, empty when nothing holds it back: a
    /// creepjoiner whose downside has not shown, or one whose downside cannot be read. A pawn known to be no
    /// creepjoiner is never held.
    /// </summary>
    public string ArmsHold(CreepJoinerPawn p)
    {
        if (!p.CreepJoiner.TryGetValue(out var joiner))
        {
            return "its creepjoiner status could not be read, so it is not armed";
        }
        if (joiner is null)
        {
            return "";
        }
        if (!Revealed(p).TryGetValue(out var revealed))
        {
            return "it is a creepjoiner whose downside could not be read, so it is not armed";
        }
        if (!revealed)
        {
            return "it is a creepjoiner whose downside is not revealed yet, so it is not armed";
        }
        return "";
    }

    /// <summary>
    /// The drops the colony owes: every available colonist held back from arms who holds a weapon, sorted by
    /// pawn id. Owed is known true with a drop, known false when none is owed and every held colonist was
    /// read, and unknown when none is owed but a held colonist's weapon or availability is unread.
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
            var wk = h.Weapon.TryGetValue(out var weapon);
            var ak = h.Available.TryGetValue(out var available);
            weapon ??= "";
            if (!wk || (!ak && weapon.Length != 0))
            {
                unread = true;
            }
            else if (weapon.Length != 0 && available)
            {
                drops.Add(new WeaponDrop(h.Pawn, weapon));
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
    /// holds no finished inspection of it. Unknown while its downside cannot be read.
    /// </summary>
    internal Fact<bool> HoldsApart(CreepJoinerHand h, CreepJoinerRecord record)
    {
        if (record.Inspections.GetValueOrDefault(h.Pawn) == InspectionStage.Done)
        {
            return Known(false);
        }
        if (!Revealed(h.Facts).TryGetValue(out var revealed))
        {
            return Unknown<bool>();
        }
        return Known(!revealed);
    }

    /// <summary>
    /// The creepjoiners the colony holds apart, sorted by pawn id. Unknown while a creepjoiner's downside
    /// cannot be read, or one's creepjoiner status cannot.
    /// </summary>
    public Fact<EquatableArray<PawnId>> Isolated(IReadOnlyList<CreepJoinerHand> hands, CreepJoinerRecord record)
    {
        var result = new List<PawnId>();
        foreach (var h in hands)
        {
            if (!h.Facts.CreepJoiner.TryGetValue(out var joiner))
            {
                return Unknown<EquatableArray<PawnId>>();
            }
            if (joiner is null)
            {
                continue;
            }
            if (!HoldsApart(h, record).TryGetValue(out var apart))
            {
                return Unknown<EquatableArray<PawnId>>();
            }
            if (apart)
            {
                result.Add(h.Pawn);
            }
        }
        result.Sort();
        return Known(EquatableArray.From(result));
    }

    /// <summary>
    /// The allowed-area changes the colony owes its creepjoiners, sorted by pawn id. <paramref name="area"/> is
    /// the Isolation area's load id (empty if the map has none); <paramref name="ready"/> is whether the
    /// isolation room stands with its bed. A creepjoiner held apart, available and not hungry is moved into the
    /// area once ready; one in the area that is no longer held apart, or is hungry, is released. A pawn whose
    /// area, availability or hunger is unread is left as it is.
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
            if (!h.Facts.CreepJoiner.TryGetValue(out var joiner) || joiner is null)
            {
                continue;
            }
            if (!h.Area.TryGetValue(out var current))
            {
                continue;
            }
            var ak = HoldsApart(h, record).TryGetValue(out var apart);
            var hk = h.Hungry.TryGetValue(out var hungry);
            var vk = h.Available.TryGetValue(out var available);
            if (current == area && ak && (!apart || (hk && hungry)))
            {
                result.Add(new IsolationMove(h.Pawn, ""));
            }
            else if (current != area && ready && ak && apart && hk && !hungry && vk && available)
            {
                result.Add(new IsolationMove(h.Pawn, area));
            }
        }
        return result.OrderBy(m => m.Pawn).ToList();
    }

    /// <summary>
    /// Finds what the colony owes its creepjoiners. A creepjoiner needs an inspection while its downside is
    /// unrevealed (Revealed known false) and the record holds none for it. An available colonist with the
    /// inspection operation on offer, a doctor able to perform it and its ingredients on the map is ordered
    /// one; the operation is the offered one whose recipe is in recipes, on its lowest part index. A colonist
    /// whose downside or inspection operation cannot be read is not ordered (and says why in Waiting). An
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
            if (!h.Facts.CreepJoiner.TryGetValue(out var joiner) || joiner is null)
            {
                continue;
            }
            var stage = record.Inspections.GetValueOrDefault(h.Pawn);
            if (stage == InspectionStage.Ordered)
            {
                var (ended, known) = h.InspectionEnded(recipes);
                if (known && ended)
                {
                    inspections[h.Pawn] = InspectionStage.Done;
                }
                continue;
            }
            if (stage != InspectionStage.None)
            {
                continue;
            }
            if (!Revealed(h.Facts).TryGetValue(out var revealed))
            {
                waiting.Add($"creepjoiner {h.Pawn} cannot be inspected: its downside could not be read");
                continue;
            }
            if (revealed)
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
/// One colonist's facts for the downside check: its creepjoiner tracker facts and the def names of its traits
/// and of its visible hediffs.
/// </summary>
public sealed record CreepJoinerPawn
{
    public Fact<CreepJoiner?> CreepJoiner { get; init; }

    public Fact<EquatableArray<string>> Traits { get; init; }

    public Fact<EquatableArray<string>> Hediffs { get; init; }
}

/// <summary>
/// One colonist's facts for the weapon drop and the inspection: the downside check's facts, whether the
/// colonist can take an order now, the thing id of the weapon in its hands (known empty for none), its
/// available surgical operations and its queued surgery bills.
/// </summary>
public sealed record CreepJoinerHand
{
    public PawnId Pawn { get; init; }

    public CreepJoinerPawn Facts { get; init; } = new();

    public Fact<bool> Available { get; init; }

    public Fact<string> Weapon { get; init; }

    /// <summary>The colonist's native surgery facts (CarePawn's).</summary>
    public Fact<EquatableArray<SurgeryOperation>> Operations { get; init; }

    public Fact<int> QueuedSurgeries { get; init; }

    /// <summary>Null when unread.</summary>
    public EquatableArray<string>? QueuedRecipes { get; init; }

    /// <summary>The load id of the colonist's allowed area (empty when unrestricted).</summary>
    public Fact<string> Area { get; init; }

    /// <summary>Whether it is below the game's fed band.</summary>
    public Fact<bool> Hungry { get; init; }

    /// <summary>Whether the pawn has no inspection bill queued; unknown while its bill stack cannot be read.</summary>
    internal (bool Ended, bool Known) InspectionEnded(IReadOnlySet<string> recipes)
    {
        if (!QueuedSurgeries.TryGetValue(out var queued))
        {
            return (false, false);
        }
        if (queued == 0)
        {
            return (true, true);
        }
        if (QueuedRecipes is not { } queuedRecipes)
        {
            return (false, false);
        }
        return (!queuedRecipes.Any(recipes.Contains), true);
    }

    /// <summary>Picks the inspection operation to queue, or says in plain English why none can be.</summary>
    internal (InspectionOrder? Order, string Why) InspectionOrder(IReadOnlySet<string> recipes)
    {
        if (recipes.Count == 0)
        {
            return (null, "the game defines no surgical inspection recipe");
        }
        if (!Available.TryGetValue(out var available))
        {
            return (null, "its availability could not be read");
        }
        if (!available)
        {
            return (null, "it cannot take an order now");
        }
        var (ended, endedKnown) = InspectionEnded(recipes);
        if (!endedKnown)
        {
            return (null, "its queued surgery bills could not be read");
        }
        if (!ended)
        {
            return (null, "an inspection bill is already queued");
        }
        if (!Operations.TryGetValue(out var ops))
        {
            return (null, "its available operations could not be read");
        }
        SurgeryOperation? best = null;
        var bestPart = 0;
        foreach (var op in ops)
        {
            if (!op.Recipe.TryGetValue(out var recipe) || !recipes.Contains(recipe))
            {
                continue;
            }
            var part = op.PartIndex.TryGetValue(out var index) ? index : DomainConstants.NoSurgeryPart;
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
        if (!best.EligibleDoctors.TryGetValue(out var doctors) || doctors == 0)
        {
            return (null, "no eligible doctor");
        }
        if (!best.IngredientsOnMap.TryGetValue(out var stocked) || !stocked)
        {
            return (null, "the inspection's ingredients are not on the map");
        }
        best.Recipe.TryGetValue(out var chosen);
        return (new InspectionOrder(Pawn, chosen ?? "", bestPart), "");
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Keeps a creepjoiner unarmed until its downside shows: the colonist drops the weapon it holds with the
    /// game's own drop job (DropEquipment), and the equipment planner arms it only once the downside is
    /// revealed (EquipCandidatePawn.NoArms). A creepjoiner's downside is hidden at arrival, so an armed one is
    /// a risk until it shows.
    /// </summary>
    public static readonly ConcernId ManageCreepJoiners = new("ManageCreepJoiners");
}
