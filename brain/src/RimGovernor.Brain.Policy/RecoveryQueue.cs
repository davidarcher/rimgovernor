using RimGovernor.Brain.Domain;

#pragma warning disable CA1720, CA1711

namespace RimGovernor.Brain.Policy;

// The recovery queue: one ranking over every ruin, foreign obstruction and loot-capable stack on the map,
// whatever its distance from Home. Rank is tier, then distance: tier 1 a room obstruction, tier 2 anything
// yielding at least one currently short resource, tier 3 the rest; within a tier nearest first. Safety holds
// apply per thing; demand never holds one. Each clearance method admits one bounded recovery batch. The review
// journals the queue (Rounds.RecoveryQueue); the clearance planner executes its removals as a roof-first batch
// (PlanRecoveryBatch) and loot is released by FilterLootReach under the same hold vocabulary.

/// <summary>Orders the queue: lower is first.</summary>
public enum RecoveryTier
{
    RoomObstruction = 1,
    ShortYield = 2,
    Rest = 3,
}

/// <summary>What the queue does with a thing this review.</summary>
public enum RecoveryStatus
{
    None = 0,

    /// <summary>The first ready thing in the bounded batch.</summary>
    [WireName("admitted")]
    Admitted,

    /// <summary>Follows the admitted thing in queue order.</summary>
    [WireName("queued")]
    Queued,

    /// <summary>Waits on labor or storage and stays in the queue.</summary>
    [WireName("deferred")]
    Deferred,

    /// <summary>Refused by a safety hold (RemoteHoldReason words).</summary>
    [WireName("held")]
    Held,
}

/// <summary>
/// One candidate. Hold is a thing-level refusal already in the RemoteHoldReason vocabulary (not_deconstructible,
/// ancient_danger, casket, roof_support_risk, ...); RouteSafe is native's route verdict. A thing with no yield
/// evidence has Evidenced false and is held "salvage_unknown".
/// </summary>
public sealed record RecoveryThing
{
    public string ID { get; init; } = "";

    public string Def { get; init; } = "";

    public RemoteWorkKind Kind { get; init; }

    public Cell Cell { get; init; }

    public bool RoomObstruction { get; init; }

    public bool Evidenced { get; init; }

    /// <summary>Native ran out of salvage time budget before computing this row; held "salvage_skipped", distinct from "salvage_unknown".</summary>
    public bool Skipped { get; init; }

    public SupplyCandidate Candidate { get; init; } = new();

    public string Hold { get; init; } = "";

    public Fact<bool> RouteSafe { get; init; }
}

/// <summary>
/// The review's input. Short are the resources currently below demand (unknown demand leaves none short:
/// nothing is held for it). Origins are the cells distance is measured from (the nearest wins); Center stands
/// in when there are none.
/// </summary>
public sealed record RecoveryRequest
{
    public IReadOnlyList<RecoveryThing> Things { get; init; } = [];

    public IReadOnlySet<Resource> Short { get; init; } = new HashSet<Resource>();

    public IReadOnlyList<Cell> Origins { get; init; } = [];

    public Cell Center { get; init; }

    public Fact<bool> Threat { get; init; }

    public Fact<IReadOnlyList<OutdoorHazard>> Hazard { get; init; }

    /// <summary>A planned dash window is open, which lifts the extreme cold hold (and only that hazard).</summary>
    public bool DashOpen { get; init; }

    public Fact<bool> Urgent { get; init; }
}

/// <summary>One ranked thing as journaled.</summary>
public sealed record RecoveryEntry
{
    public string ID { get; init; } = "";

    public string Def { get; init; } = "";

    public RemoteWorkKind Kind { get; init; }

    public RecoveryTier Tier { get; init; }

    public double Distance { get; init; }

    public RecoveryStatus Status { get; init; }

    public string Reason { get; init; } = "";

    public EquatableArray<Resource> Short { get; init; }
}

/// <summary>The ranked result: entries in rank order, held ones in place, and the admitted thing ("" when none).</summary>
public sealed record RecoveryQueue
{
    public EquatableArray<RecoveryEntry> Entries { get; init; }

    public string Admitted { get; init; } = "";
}

public static partial class PolicyFunctions
{
    private static List<Resource> RecoveryShort(SupplyCandidate c, IReadOnlySet<Resource> shortSet)
    {
        var output = new List<Resource>();
        foreach (var y in c.Yields)
        {
            if (y.StockCap.TryGetValue(out var n) && n > 0 && shortSet.Contains(y.Good.Def) && !output.Contains(y.Good.Def))
            {
                output.Add(y.Good.Def);
            }
        }
        output.Sort();
        return output;
    }

    private static double RecoveryDistance(RecoveryThing t, RecoveryRequest r)
    {
        if (t.Candidate.PathDistance.TryGetValue(out var d) && d >= 0)
        {
            return d;
        }
        IReadOnlyList<Cell> origins = r.Origins.Count == 0 ? [r.Center] : r.Origins;
        var best = double.PositiveInfinity;
        foreach (var o in origins)
        {
            best = Math.Min(best, Math.Sqrt((double)(t.Cell.X - o.X) * (t.Cell.X - o.X) + (double)(t.Cell.Z - o.Z) * (t.Cell.Z - o.Z)));
        }
        return best;
    }

    /// <summary>
    /// The per-thing safety verdict: "" when the thing is free to work. Threat explains the hold first, then
    /// the thing's own refusal, missing evidence, route safety and urgent colony work.
    /// </summary>
    private static string RecoveryHold(RecoveryThing t, RecoveryRequest r)
    {
        if (r.Threat.TryGetValue(out var threat) && threat)
        {
            return RemoteHoldThreat;
        }
        if (OutdoorHazardHeldDash(r.Hazard, r.DashOpen))
        {
            return RemoteHoldHazard;
        }
        if (t.Hold.Length != 0)
        {
            return t.Hold;
        }
        if (t.Skipped)
        {
            return "salvage_skipped";
        }
        if (!t.Evidenced)
        {
            return "salvage_unknown";
        }
        if (!t.RouteSafe.TryGetValue(out var safe) || !safe)
        {
            return RemoteHoldRouteUnsafe;
        }
        if (r.Urgent.TryGetValue(out var urgent) && urgent)
        {
            return RemoteHoldUrgentWork;
        }
        return "";
    }

    /// <summary>A thing with yields none of which has known storage headroom. It throttles; it never holds, and no storage is planned for it.</summary>
    private static bool RecoveryStorageMissing(SupplyCandidate c)
    {
        if (c.Yields.Count == 0)
        {
            return false;
        }
        foreach (var y in c.Yields)
        {
            if (y.Headroom.TryGetValue(out var n) && n > 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Ranks and admits the request's things.</summary>
    public static RecoveryQueue RankRecovery(RecoveryRequest r)
    {
        var entries = new RecoveryEntry[r.Things.Count];
        var ready = new bool[r.Things.Count];
        var throttled = new bool[r.Things.Count];
        for (var i = 0; i < r.Things.Count; i++)
        {
            var t = r.Things[i];
            var shortRows = RecoveryShort(t.Candidate, r.Short);
            var tier = RecoveryTier.Rest;
            if (t.RoomObstruction)
            {
                tier = RecoveryTier.RoomObstruction;
            }
            else if (shortRows.Count > 0)
            {
                tier = RecoveryTier.ShortYield;
            }
            var e = new RecoveryEntry
            {
                ID = t.ID,
                Def = t.Def,
                Kind = t.Kind,
                Tier = tier,
                Distance = RecoveryDistance(t, r),
                Short = EquatableArray.From(shortRows),
            };
            var reason = RecoveryHold(t, r);
            if (reason.Length != 0)
            {
                e = e with { Status = RecoveryStatus.Held, Reason = reason };
            }
            else
            {
                ready[i] = true;
                throttled[i] = RecoveryStorageMissing(t.Candidate);
            }
            entries[i] = e;
        }
        var order = Enumerable.Range(0, entries.Length)
            .OrderBy(i => entries[i].Tier)
            .ThenBy(i => entries[i].Distance)
            .ThenBy(i => entries[i].ID, StringComparer.Ordinal)
            .ToList();
        var output = new List<RecoveryEntry>(entries.Length);
        var admitted = "";
        foreach (var i in order)
        {
            var e = entries[i];
            if (!ready[i])
            {
            }
            else if (throttled[i])
            {
                e = e with { Status = RecoveryStatus.Deferred, Reason = RemoteHoldMissingStorage };
            }
            else if (admitted.Length == 0)
            {
                admitted = e.ID;
                e = e with { Status = RecoveryStatus.Admitted };
            }
            else
            {
                e = e with { Status = RecoveryStatus.Queued };
            }
            output.Add(e);
        }
        return new RecoveryQueue { Entries = EquatableArray.From(output), Admitted = admitted };
    }

    /// <summary>
    /// Prices one census row. Player buildings are the colony's own and are no recovery thing (Ok false). The
    /// row's native verdicts (ancient, casket, not deconstructible) fold into Hold. Its native per-building
    /// roof verdict does not: the mirror's joint roof check supersedes it, so a ruin holding up a roof is
    /// queued and PlanRecoveryBatch takes the thin roofs down before it; what the mirror check still refuses
    /// after that is held roof_support_risk there. Native re-checks at admission.
    /// </summary>
    public static (RecoveryThing Thing, bool Ok) RecoveryClearanceThing(ClearanceTarget row, bool roomObstruction)
    {
        if (row.Player)
        {
            return (new RecoveryThing(), false);
        }
        var own = row with { RoofBlocker = "" };
        var t = new RecoveryThing
        {
            ID = row.EntityID,
            Def = row.DefName,
            Kind = RemoteWorkKind.Salvage,
            RoomObstruction = roomObstruction,
            Cell = new Cell((row.Minimum.X + row.Maximum.X) / 2, (row.Minimum.Z + row.Maximum.Z) / 2),
            Hold = RemoteHoldReason(RemoteWorkKind.Salvage, ClearanceHoldReason(own)),
            RouteSafe = Unknown<bool>(),
            Skipped = row.SalvageSkipped,
        };
        if (row.Salvage is not null)
        {
            t = t with
            {
                Candidate = SalvagePriced(row.EntityID, row.Salvage).Candidate,
                Evidenced = true,
                RouteSafe = row.Salvage.Safe,
            };
        }
        return (t, true);
    }

    /// <summary>A forbidden safe-to-haul stack's candidate. A stack a native spawner forbids on purpose is never recovery (Ok false).</summary>
    public static (RecoveryThing Thing, bool Ok) RecoveryLootThing(LootItem row)
    {
        if (row.SpawnForbidden || !row.Forbidden)
        {
            return (new RecoveryThing(), false);
        }
        var t = new RecoveryThing
        {
            ID = row.Supply.Thing,
            Def = row.Supply.Definition,
            Kind = RemoteWorkKind.Loot,
            Cell = row.Supply.Cell,
            Evidenced = true,
            Candidate = LootCandidate(row),
            RouteSafe = row.SafetyKnown ? Known(row.SafeToHaul) : Unknown<bool>(),
        };
        return (t, true);
    }

    /// <summary>
    /// Tier 1's source: the census ids of the foreign rows standing on the ground of a room the plan has not
    /// built yet (PlannedGround). The room owners' own claim of a ring ruin is not consulted.
    /// </summary>
    public static HashSet<string> RoomObstructionIDs(IReadOnlyList<ClearanceTarget> rows, IReadOnlyList<Rectangle> ground)
    {
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var g in ground)
            {
                if (row.Minimum.X < g.X + g.Width && row.Maximum.X >= g.X && row.Minimum.Z < g.Z + g.Height && row.Maximum.Z >= g.Z)
                {
                    output.Add(row.EntityID);
                    break;
                }
            }
        }
        return output;
    }

    /// <summary>
    /// The resources below their effective stock target (the targets carry the derived resource needs): the
    /// short set tier 2 ranks by. An unknown demand leaves none short. Throws on an invalid demand.
    /// </summary>
    private static HashSet<Resource> RecoveryShortResources(RoundsPolicy p, RoundsFacts f)
    {
        var targets = new List<ResourceDemand>();
        if (f.ResourceNeeds is not null)
        {
            foreach (var (resource, count) in f.ResourceNeeds)
            {
                if (count > 0)
                {
                    targets.Add(new ResourceDemand { Key = new ResourceKey { Def = resource }, Count = count, Priority = 2 });
                }
            }
        }
        var input = new ResourceDemandInput { EconomicFloors = new Dictionary<string, long>(), Targets = EquatableArray.From(targets) };
        if (f.Resources.TryGetValue(out var stock))
        {
            var rows = new List<ResourceQuantity>();
            foreach (var amount in stock)
            {
                if (amount.Count > 0)
                {
                    rows.Add(new ResourceQuantity { Key = new ResourceKey { Def = amount.Resource }, Count = amount.Count });
                }
            }
            input = input with { Stock = Known(EquatableArray.From(rows)) };
        }
        var demand = BuildResourceDemand(input);
        var output = new HashSet<Resource>();
        if (demand.TryGetValue(out var demandRows))
        {
            foreach (var d in demandRows)
            {
                if (d.Count > 0)
                {
                    output.Add(d.Key.Def);
                }
            }
        }
        return output;
    }

    /// <summary>
    /// Assembles the review's request from the clearance census, the loot census and the review facts: demand
    /// from the effective targets, distance from the colony's facility cells (stockpile zones among them), the
    /// nearest of which is the origin. roomObstructions names the census ids a planned room waits on (tier 1).
    /// The caller sets Slot. Throws on an invalid extent or demand.
    /// </summary>
    public static RecoveryRequest ReviewRecoveryRequest(RoundsPolicy p, RoundsFacts f, IReadOnlyList<ClearanceTarget> rows, IReadOnlySet<string> roomObstructions)
    {
        var extent = DeriveColonyExtent(new ColonyExtentRequest { Bounds = f.MapBounds, Construction = f.CurrentConstruction, Claims = f.ConstructionClaims, Home = f.HomeCoverage });
        var shortSet = RecoveryShortResources(p, f);
        var threat = Unknown<bool>();
        if (f.Hostiles.TryGetValue(out var hostiles))
        {
            threat = Known(hostiles > 0);
        }
        var origins = new List<Cell>();
        var center = new Cell();
        if (extent.TryGetValue(out var e))
        {
            long sx = 0, sz = 0, n = 0;
            foreach (var region in e.Regions)
            {
                foreach (var c in region.Cells)
                {
                    sx += c.Cell.X;
                    sz += c.Cell.Z;
                    n++;
                    if (c.Provenance.Any(o => o.Origin == ExtentOrigin.Facility))
                    {
                        origins.Add(c.Cell);
                    }
                }
            }
            if (n > 0)
            {
                center = new Cell((int)(sx / n), (int)(sz / n));
            }
        }
        var things = new List<RecoveryThing>();
        foreach (var row in rows)
        {
            var (t, ok) = RecoveryClearanceThing(row, roomObstructions.Contains(row.EntityID));
            if (ok)
            {
                things.Add(t);
            }
        }
        if (f.EventLoot.TryGetValue(out var loot))
        {
            foreach (var row in loot)
            {
                var (t, ok) = RecoveryLootThing(row);
                if (ok)
                {
                    things.Add(t);
                }
            }
        }
        return new RecoveryRequest
        {
            Short = shortSet,
            Urgent = UrgentWorkCompeting(f),
            Threat = threat,
            Hazard = OutdoorHazardsOf(f),
            DashOpen = f.DashOpen(),
            Origins = origins,
            Center = center,
            Things = things,
        };
    }
}
