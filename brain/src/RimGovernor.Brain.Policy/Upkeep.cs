namespace RimGovernor.Brain.Policy;

// Go: upkeep.go.

/// <summary>Each fact is a complete native section. An unavailable section cannot prove that its old targets disappeared, while a known empty section can.</summary>
public sealed record UpkeepObservation
{
    public Fact<EquatableArray<ClearanceTarget>> Clearance { get; init; }

    /// <summary>The same read's rock and slag stacks in Home; a pending one (no store will take it) is a clearance deficit until a dump exists.</summary>
    public Fact<EquatableArray<ClearanceChunk>> Chunks { get; init; }

    /// <summary>The planned-ground clearance work of the same read (PlannedGroundWork): unplanned buildings and floors on the ground of planned rooms not yet standing.</summary>
    public Fact<EquatableArray<string>> Ground { get; init; }

    /// <summary>The clearance rows standing on the ground of a planned room (RoomObstructionIDs): tier 1 of the recovery queue.</summary>
    public IReadOnlySet<string> RoomObstructions { get; init; } = new HashSet<string>();

    /// <summary>The ancient shrine census ClearAncientShrine measures; unknown under a native without the read.</summary>
    public Fact<EquatableArray<AncientShrine>> Shrines { get; init; }

    /// <summary>The operator's casket stance, filled by the review from RoundsPolicy the way CleaningContext fills the cleaning inputs, so every ReviewUpkeep caller measures the same targets.</summary>
    public ShrinePolicy ShrinePolicy { get; init; } = new();

    public Fact<EquatableArray<UpkeepStructure>> Structures { get; init; }

    public Fact<EquatableArray<UpkeepFire>> Fires { get; init; }

    public Fact<EquatableArray<UpkeepFilth>> Filth { get; init; }

    /// <summary>The measured room census of the same read.</summary>
    public Fact<RoomObservation> Rooms { get; init; }

    /// <summary>The measured work-cell illumination census MaintainLighting reviews; unknown when native could not read it.</summary>
    public Fact<LightingObservation> Lighting { get; init; }

    /// <summary>The measured room terrain census MaintainFlooring reviews; unknown when native could not read it.</summary>
    public Fact<FlooringObservation> Flooring { get; init; }

    /// <summary>The measured facility reachability and traffic census MaintainRoutes reviews.</summary>
    public Fact<RoutesObservation> Routes { get; init; }
}

public sealed record UpkeepStructure
{
    public string Id { get; init; } = "";

    /// <summary>The building's def name; empty when the row carries none.</summary>
    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public bool Home { get; init; }

    public long HitPoints { get; init; }

    public long MaxHitPoints { get; init; }

    public int Priority { get; init; }
}

public sealed record UpkeepFire
{
    public string Id { get; init; } = "";

    public bool Home { get; init; }

    public Fact<double> Size { get; init; }
}

public sealed record UpkeepFilth
{
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public bool Home { get; init; }

    /// <summary>The RoomRoleDef name.</summary>
    public string Room { get; init; } = "";

    /// <summary>The room census identity the filth lies in (unknown outdoors or before the native field existed).</summary>
    public Fact<string> RoomId { get; init; }

    public uint Thickness { get; init; }
}

public sealed record UpkeepHistory
{
    public bool Clearance { get; init; }

    public bool Shrine { get; init; }

    public bool Fire { get; init; }

    public bool Repairs { get; init; }
}

public sealed record UpkeepNeed(ConcernId Concern, int Priority, bool Active, Fact<EquatableArray<string>> Targets, Fact<double> Metric, bool Unsafe);

public sealed record UpkeepReview
{
    public UpkeepHistory History { get; init; } = new();

    public IReadOnlyList<UpkeepNeed> Needs { get; init; } = [];
}

public static partial class PolicyFunctions
{
    public static readonly ConcernId MaintainFireSafety = new("MaintainFireSafety");

    public static readonly ConcernId MaintainEssentialRepairs = new("MaintainEssentialRepairs");

    /// <summary>
    /// Ports the direct native upkeep contracts. Issued work is supplied by the shared journal, never inferred from a
    /// receipt or target loss. Throws on an invalid row.
    /// </summary>
    public static UpkeepReview ReviewUpkeep(UpkeepObservation v, UpkeepHistory previous, IReadOnlySet<ConcernId> issued)
    {
        var needs = new List<UpkeepNeed>();
        var history = new UpkeepHistory();
        bool Add(ConcernId goal, int priority, bool active, Fact<EquatableArray<string>> targets, Fact<double> metric, bool unsafeNeed)
        {
            if (targets.TryGetValue(out var rows))
            {
                active = rows.Count > 0;
            }
            active = active || issued.Contains(goal);
            if (!targets.IsKnown && !active)
            {
                priority = 4;
            }
            needs.Add(new UpkeepNeed(goal, priority, active, targets, metric, unsafeNeed));
            return active;
        }
        static bool Valid(HashSet<string> seen, string id)
        {
            if (!FoodId(id) || seen.Contains(id))
            {
                return false;
            }
            seen.Add(id);
            return true;
        }
        UpkeepReview Result() => new() { History = history, Needs = needs };

        var fire = ReviewFireSafety(v.Fires, previous.Fire, issued.Contains(MaintainFireSafety));
        history = history with { Fire = fire.Active };
        needs.Add(fire);
        var targets = Unknown<EquatableArray<string>>();
        var metric = Unknown<double>();
        if (v.Structures.TryGetValue(out var structures))
        {
            var seen = new HashSet<string>();
            var selected = new List<UpkeepStructure>();
            foreach (var row in structures)
            {
                if (!Valid(seen, row.Id) || row.HitPoints < 0 || row.MaxHitPoints < row.HitPoints || row.Priority < 0 || row.Priority > 2 || row.Cell.X < 0 || row.Cell.Z < 0)
                {
                    throw new DomainException("invalid upkeep structure");
                }
                if (row.Home && row.MaxHitPoints > 0 && row.HitPoints < row.MaxHitPoints)
                {
                    selected.Add(row);
                }
            }
            selected.Sort((a, b) =>
            {
                if (a.Priority != b.Priority)
                {
                    return a.Priority.CompareTo(b.Priority);
                }
                var x = (double)a.HitPoints / a.MaxHitPoints;
                var y = (double)b.HitPoints / b.MaxHitPoints;
                if (x != y)
                {
                    return x < y ? -1 : 1;
                }
                return string.CompareOrdinal(a.Id, b.Id);
            });
            var result = new List<string>();
            var total = 0.0;
            foreach (var row in selected)
            {
                result.Add(row.Id);
                total += row.MaxHitPoints - row.HitPoints;
            }
            targets = Known(EquatableArray.From(result));
            metric = Known(total);
        }
        history = history with { Repairs = Add(MaintainEssentialRepairs, 3, previous.Repairs, targets, metric, false) };
        if (v.Filth.TryGetValue(out var filth))
        {
            var seen = new HashSet<string>();
            foreach (var row in filth)
            {
                if (!Valid(seen, row.Id) || row.Cell.X < 0 || row.Cell.Z < 0)
                {
                    throw new DomainException("invalid upkeep filth");
                }
            }
        }
        foreach (var n in needs)
        {
            if (n.Metric.TryGetValue(out var x) && !double.IsFinite(x))
            {
                throw new DomainException("upkeep metric overflow");
            }
        }

        var clearanceTargets = Unknown<EquatableArray<string>>();
        if (v.Clearance.TryGetValue(out var clearance))
        {
            var selected = new List<string>();
            var seen = new HashSet<string>();
            foreach (var row in clearance)
            {
                if (!Valid(seen, row.EntityId))
                {
                    throw new DomainException("invalid clearance target");
                }
                if (ClearanceHoldReason(row).Length == 0)
                {
                    selected.Add(row.EntityId);
                }
            }
            if (v.Chunks.TryGetValue(out var chunks))
            {
                foreach (var row in chunks)
                {
                    if (!Valid(seen, row.EntityId))
                    {
                        throw new DomainException("invalid clearance chunk");
                    }

                    // A chunk a store takes still needs ordinary hauling to carry it, so only a stored or forbidden stack is off the deficit.
                    if (!row.Stored && !row.Forbidden)
                    {
                        selected.Add(row.EntityId);
                    }
                }
            }
            if (v.Ground.TryGetValue(out var ground))
            {
                foreach (var id in ground)
                {
                    if (!Valid(seen, id))
                    {
                        throw new DomainException("invalid planned ground target");
                    }
                    selected.Add(id);
                }
            }
            clearanceTargets = Known(EquatableArray.From(selected));
        }
        history = history with { Clearance = Add(ClearHomeObstructions, 3, previous.Clearance, clearanceTargets, Unknown<double>(), false) };
        var shrineTargets = Unknown<EquatableArray<string>>();
        if (v.Shrines.TryGetValue(out var shrines))
        {
            var seen = new HashSet<string>();
            foreach (var row in shrines)
            {
                if (!Valid(seen, row.Id))
                {
                    throw new DomainException("invalid shrine");
                }
            }
            shrineTargets = Known(EquatableArray.From(ShrineClearanceTargets(shrines, v.ShrinePolicy)));
        }
        history = history with { Shrine = Add(ClearAncientShrine, 3, previous.Shrine, shrineTargets, Unknown<double>(), false) };
        return Result();
    }
}
