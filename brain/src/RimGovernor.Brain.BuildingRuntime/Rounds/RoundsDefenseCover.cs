using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const int MaxDefenseCoverBatch = 8;
    internal const string DefenseCoverPrefix = "defense-cover-";

    internal (int Defenders, Fact<double> MinRange, bool Ok) DefenderRange(CancellationToken call, ControlState state, RoundsReading read)
    {
        if (!read.Emergency.ColonistsComplete.TryGetValue(out var complete) || !complete || read.Emergency.Colonists.Count == 0)
        {
            return (0, Unknown<double>(), false);
        }
        var ids = read.Emergency.Colonists.Select(p => p.Id.Value).ToList();
        var (reply, _) = _native.ReadCombatPawns(call, Boundary.Identity(state.Snapshot), ids);
        var observed = reply.Observed;
        if (observed is null || observed.Pawns.Count != ids.Count)
        {
            throw DefenseControlErr(248);
        }
        SameTick(observed.Context, state, read.Projection.Identity.Tick);
        var arms = ReadArmament(call, _native, Boundary.Identity(state.Snapshot));
        var (defenders, minRange) = DefenderRangeOf(observed.Pawns, arms);
        return (defenders, minRange, defenders > 0);
    }

    internal (RoundsDefenseLayoutResult Result, bool Done) ClearCover(CancellationToken call, CancellationToken epoch, StandardState goal, RoundsReview review, ControlState state, RoundsReading read, DefenseLayoutRecord record)
    {
        var p = _reviewer.Player;
        var projection = read.Projection;
        var tick = projection.Identity.Tick;
        var identity = Boundary.Identity(state.Snapshot);
        var (killbox, region, home, ok) = DefenseKillbox(projection);
        if (!ok)
        {
            return (new RoundsDefenseLayoutResult(), false);
        }
        var site = DefenseSite(call, identity, projection, region);
        var request = DefenseTurretRequest(read);
        request.Bounds = projection.Bounds;
        request.Home = home;
        request.Tick = tick;
        request.Killbox = killbox;
        request.Region = new Rectangle(region.Min.X, region.Min.Z, region.Max.X - region.Min.X + 1, region.Max.Z - region.Min.Z + 1);
        var byCell = new Dictionary<Cell, BridgeDefenseCell>();
        foreach (var cell in site.Cells)
        {
            byCell[cell.Cell] = cell;
            request.Cells.Add(DefenseCellFacts(cell));
            if (!cell.Fogged && cell.Door && cell.PlayerOwned)
            {
                request.Entrances.Add(cell.Cell);
            }
        }
        const double defenseCoverFillThreshold = 0.0;
        request.Arrivals = DefenseArrivals(projection.Raids, region);
        request.CoverThreshold = Known(defenseCoverFillThreshold);
        var (defenders, minRange, haveRange) = DefenderRange(call, state, read);
        if (!haveRange)
        {
            return (new RoundsDefenseLayoutResult(), false);
        }
        request.Defenders = defenders;
        request.MinRange = minRange;
        var layout = DefenseRecordLayout(record, killbox);
        RecordLayoutSnapshot(call, state.Snapshot, tick, new SnapshotLayout { Point = SnapshotApi.LayoutCover, Request = request, Record = record, Site = site.Cells });
        DefenseApproaches approaches;
        try
        {
            approaches = PolicyApi.DefenseApproachesFor(request, layout);
        }
        catch (DomainException ex)
        {
            DefenseAction(call, "defense-layout", LogLevel.Info, "refused", "cover_census", "cover", new Dictionary<string, object?> { ["error"] = ex });
            return (new RoundsDefenseLayoutResult(), false);
        }
        if (approaches.Hold != "")
        {
            DefenseAction(call, "defense-layout", LogLevel.Info, "waiting", "cover_held", "cover", new Dictionary<string, object?> { ["detail"] = approaches.Hold });
            return (new RoundsDefenseLayoutResult(), false);
        }
        var (clearances, _) = DefenseCoverSelection(approaches, byCell);
        if (clearances.Count == 0)
        {
            return (new RoundsDefenseLayoutResult(), false);
        }
        if (clearances.Count > MaxDefenseCoverBatch)
        {
            clearances = clearances.GetRange(0, MaxDefenseCoverBatch);
        }
        var (verdict, admitted) = AdmitStandardSubject(call, p.Journal, goal, DefenseCoverPrefix, state.Snapshot);
        if (!admitted)
        {
            return (new RoundsDefenseLayoutResult { Verdict = verdict }, true);
        }
        var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"{DefenseCoverPrefix}{tick.Value}"));
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>(clearances.Count);
        for (var i = 0; i < clearances.Count; i++)
        {
            actions.Add(DomainAction.NewCoverClearanceAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), clearances[i]));
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var now = _reviewer.Clock.Now();
        if (p.Session.State() != state || now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw DefenseControlErr(360);
        }
        p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method, "", plan);
        DefenseAction(call, "defense-layout", LogLevel.Info, "applied", "cover_clearance", method.Value, new Dictionary<string, object?> { ["things"] = clearances.Count });
        return (new RoundsDefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id }, true);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The policy layout of a stored defense record: its firing positions, trap lane and the tier buildings.</summary>
    internal static DefenseLayout DefenseRecordLayout(DefenseLayoutRecord record, DefenseKillbox killbox)
    {
        var l = new DefenseLayout { Toward = killbox.Toward, Width = (int)killbox.Width, Entry = killbox.Entry, TrapLane = record.TrapLane.ToList() };
        for (var i = 0; i < record.Firing.Count; i++)
        {
            var cell = record.Firing[i];
            var retreat = record.Retreat.Count == record.Firing.Count ? record.Retreat[i] : cell;
            l.Firing.Add(new FiringPosition { Cell = cell, Cover = cell, Retreat = retreat });
        }
        foreach (var tier in record.Tiers)
        {
            var t = new DefenseTier { Name = tier.Name, Reserved = tier.Reserved.ToList() };
            foreach (var b in tier.Buildings)
            {
                t.Buildings.Add(DomainApi.NewBuilding(b.Definition, b.Cell, b.Rotation, b.Stuff));
            }
            l.Tiers.Add(t);
        }
        return l;
    }

    /// <summary>The ground raids whose spawn or trail enters the region, with the crossing cell.</summary>
    internal static List<DefenseArrival> DefenseArrivals(IReadOnlyList<RaidTrack> raids, BridgeCellRect region)
    {
        bool Inside(Cell c) => c.X >= region.Min.X && c.X <= region.Max.X && c.Z >= region.Min.Z && c.Z <= region.Max.Z;
        var output = new List<DefenseArrival>();
        foreach (var raid in raids)
        {
            if (!raid.Ground)
            {
                continue;
            }
            var crossing = raid.Spawn;
            var found = Inside(raid.Spawn);
            foreach (var c in raid.Trail)
            {
                if (found)
                {
                    break;
                }
                crossing = c;
                found = Inside(c);
            }
            if (found)
            {
                output.Add(new DefenseArrival { Id = raid.LordId, Edge = crossing, Tick = raid.SpawnTick });
            }
        }
        return output;
    }

    /// <summary>The cover to clear and the counts of the cover held back by reason.</summary>
    internal static (List<CoverClearance> Clearances, Dictionary<string, int> Held) DefenseCoverSelection(DefenseApproaches approaches, IReadOnlyDictionary<Cell, BridgeDefenseCell> byCell)
    {
        var clearances = new List<CoverClearance>();
        var held = new Dictionary<string, int>(StringComparer.Ordinal);
        void Hold(string reason) => held[reason] = held.GetValueOrDefault(reason) + 1;
        foreach (var cover in approaches.Cover)
        {
            if (cover.Hold != "")
            {
                Hold(cover.Hold);
                continue;
            }
            byCell.TryGetValue(cover.Cell, out var cell);
            if (cell?.Cover is null)
            {
                Hold("unidentified");
            }
            else if (cell.Cover.Designated)
            {
                Hold("designated");
            }
            else if (cell.PlayerOwned)
            {
                Hold("structure");
            }
            else if (cell.Cover.Designation() == "")
            {
                Hold("unknown_kind");
            }
            else
            {
                clearances.Add(DomainApi.NewCoverClearance(cell.Cover.ThingId, cell.Cover.DefName, cell.Cover.Designation(), cover.Cell));
            }
        }
        return (clearances, held);
    }
}
