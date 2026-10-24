using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const int MaxDefenseCoverBatch = 8;
    internal const string DefenseCoverPrefix = "defense-cover-";

    /// <summary>The ranged defenders and their shortest reach; not Ok when the colonists are not all seen or none holds a ranged weapon.</summary>
    internal async Task<(int Defenders, double MinRange, bool Ok)> DefenderRangeAsync(ControlState state, RoundsReading read, CancellationToken call)
    {
        if (!read.Emergency.AllSeen || read.Emergency.Colonists.Count == 0)
        {
            return (0, 0, false);
        }
        var ids = read.Emergency.Colonists.Select(p => p.Id.Value).ToList();
        var reply = await _native.ReadPawnsAsync(Boundary.Identity(state.Snapshot), ids, call).ConfigureAwait(false);
        var observed = reply;
        if (observed.Pawns.Count != ids.Count)
        {
            throw DefenseControlErr(248);
        }
        SameTick(observed.Context, state, read.Projection.Identity.Tick);
        var arms = await Armament.ReadArmamentAsync(_native, Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        var (defenders, minRange) = DefenderRangeOf(observed.Pawns, arms);
        return (defenders, minRange, defenders > 0);
    }

    internal async Task<(RoundsDefenseLayoutResult Result, bool Done)> ClearCoverAsync(NeedOwner goal, RoundsReview review, ControlState state, RoundsReading read, DefenseLayoutRecord record, LayoutPlan layoutPlan, CancellationToken call, CancellationToken epoch)
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
        var site = await DefenseSiteAsync(identity, projection, region, call).ConfigureAwait(false);
        var request = DefenseTurretRequest(read);
        request = request with { Bounds = projection.Bounds };
        request = request with { Home = home };
        request = request with { Tick = tick };
        request = request with { Killbox = killbox };
        request = request with { Region = new Rectangle(region.Min.X, region.Min.Z, region.Max.X - region.Min.X + 1, region.Max.Z - region.Min.Z + 1) };
        var byCell = new Dictionary<Cell, O.DefenseCell.Seen>();
        var (requestCells, fogged) = await DefenseSiteCellsAsync(identity, request.Definitions, site, call).ConfigureAwait(false);
        var entrances = new List<Cell>();
        foreach (var cell in site)
        {
            if (cell is not O.DefenseCell.Seen seen)
            {
                continue;
            }
            byCell[cell.Cell] = seen;
            if (seen.Edifice is { Door: true, PlayerOwned: true })
            {
                entrances.Add(cell.Cell);
            }
        }
        request = request with { Cells = [.. requestCells], Fogged = [.. fogged], Entrances = [.. entrances] };
        request = request with { Arrivals = [.. DefenseArrivals(projection.Raids, region)] };
        var (defenders, minRange, haveRange) = await DefenderRangeAsync(state, read, call).ConfigureAwait(false);
        if (!haveRange)
        {
            return (new RoundsDefenseLayoutResult(), false);
        }
        request = request with { Defenders = defenders };
        request = request with { MinRange = minRange };
        var layout = DefenseRecordLayout(record, layoutPlan, killbox);
        DefenseRecorder.RecordLayoutSnapshot(state.Snapshot, tick, new SnapshotLayout { Point = SnapshotLayoutReplay.LayoutCover, Request = request, Layout = layout, Site = site }, call);
        DefenseApproaches approaches;
        try
        {
            approaches = DefenseArrivalRoutes.DefenseApproachesFor(request, layout);
        }
        catch (DomainException ex)
        {
            DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "refused", "cover_census", "cover", new Dictionary<string, object?> { ["error"] = ex }, call);
            return (new RoundsDefenseLayoutResult(), false);
        }
        if (approaches.Hold != "")
        {
            DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "waiting", "cover_held", "cover", new Dictionary<string, object?> { ["detail"] = approaches.Hold }, call);
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
        var (verdict, admitted) = RoundsRefusalBudget.AdmitOwnerSubject(p.Journal, goal, DefenseCoverPrefix, state.Snapshot, call);
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
        var commit = p.Journal.CommitOwnerWork(goal, method, "", plan, call);
        if (commit.Outcome != Sto.ClaimOutcome.Admitted)
        {
            return (new RoundsDefenseLayoutResult { Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(commit) }, true);
        }
        DefensePipeline.DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "cover_clearance", method.Value, new Dictionary<string, object?> { ["things"] = clearances.Count }, call);
        return (new RoundsDefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id }, true);
    }
}

partial class RoundsDefenseLayoutPlanner
{
    /// <summary>
    /// The policy layout of a stored defense record: the tier buildings with the reserved cells of the plan's tier nodes
    /// (#3584), and the firing positions and the trap lane of the plan's DefFiring and DefLane nodes (#3583).
    /// </summary>
    internal static DefenseLayout DefenseRecordLayout(DefenseLayoutRecord record, LayoutPlan plan, DefenseKillbox killbox)
    {
        var geometry = plan.DefenseTierGeometry();
        var planned = plan.PlannedDefenseTiers();
        var tiers = new List<DefenseTier>();
        foreach (var tier in record.Tiers)
        {
            var buildings = new List<Building>();
            foreach (var b in tier.Buildings)
            {
                buildings.Add(new Building(b.Definition, b.Cell, b.Rotation, b.Stuff));
            }
            var reserved = planned.TryGetValue(tier.Name, out var nodes) ? nodes.Reserved : [];
            tiers.Add(new DefenseTier { Name = tier.Name, Reserved = reserved, Buildings = [.. buildings] });
        }
        return new DefenseLayout
        {
            Toward = killbox.Toward,
            Entry = killbox.Entry,
            TrapLane = geometry.TrapLane,
            Firing = geometry.Firing,
            Tiers = [.. tiers],
        };
    }

    /// <summary>The ground raids whose spawn or trail enters the region, with the crossing cell.</summary>
    internal static List<DefenseArrival> DefenseArrivals(IReadOnlyList<O.RaidTrack> raids, BridgeCellRect region)
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
                output.Add(new DefenseArrival(raid.LordId, crossing, raid.SpawnTick));
            }
        }
        return output;
    }

    /// <summary>The cover to clear and the counts of the cover held back by reason.</summary>
    internal static (List<CoverClearance> Clearances, Dictionary<string, int> Held) DefenseCoverSelection(DefenseApproaches approaches, IReadOnlyDictionary<Cell, O.DefenseCell.Seen> byCell)
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
            else if (cell.Edifice is { PlayerOwned: true })
            {
                Hold("structure");
            }
            else
            {
                clearances.Add(new CoverClearance(cell.Cover.ThingId, cell.Cover.DefName, cell.Cover.Designation(), cover.Cell));
            }
        }
        return (clearances, held);
    }
}
