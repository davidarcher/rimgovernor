using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsDefenseLayoutPlanner
{
    internal const int MaxDefenseCoverBatch = 8;
    internal const string DefenseCoverPrefix = "defense-cover-";

    internal async Task<(int Defenders, Fact<double> MinRange, bool Ok)> DefenderRangeAsync(ControlState state, RoundsReading read, CancellationToken call)
    {
        if (!read.Emergency.ColonistsComplete.TryGetValue(out var complete) || !complete || read.Emergency.Colonists.Count == 0)
        {
            return (0, Unknown<double>(), false);
        }
        var ids = read.Emergency.Colonists.Select(p => p.Id.Value).ToList();
        var (reply, _) = await _native.ReadCombatPawnsAsync(Boundary.Identity(state.Snapshot), ids, call).ConfigureAwait(false);
        var observed = reply.Outcome?.Observed;
        if (observed is null || observed.Pawns.Count != ids.Count)
        {
            throw DefenseControlErr(248);
        }
        SameTick(observed.Context, state, read.Projection.Identity.Tick);
        var arms = await ReadArmamentAsync(_native, Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        var (defenders, minRange) = DefenderRangeOf(observed.Pawns, arms);
        return (defenders, minRange, defenders > 0);
    }

    internal async Task<(RoundsDefenseLayoutResult Result, bool Done)> ClearCoverAsync(StandardState goal, RoundsReview review, ControlState state, RoundsReading read, DefenseLayoutRecord record, CancellationToken call, CancellationToken epoch)
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
        var byCell = new Dictionary<Cell, BridgeDefenseCell>();
        var requestCells = new List<DefenseCell>();
        var entrances = new List<Cell>();
        foreach (var cell in site.Cells)
        {
            byCell[cell.Cell] = cell;
            requestCells.Add(DefenseCellFacts(cell));
            if (!cell.Fogged && cell.Door && cell.PlayerOwned)
            {
                entrances.Add(cell.Cell);
            }
        }
        request = request with { Cells = [.. requestCells], Entrances = [.. entrances] };
        const double defenseCoverFillThreshold = 0.0;
        request = request with { Arrivals = [.. DefenseArrivals(projection.Raids, region)] };
        request = request with { CoverThreshold = Known(defenseCoverFillThreshold) };
        var (defenders, minRange, haveRange) = await DefenderRangeAsync(state, read, call).ConfigureAwait(false);
        if (!haveRange)
        {
            return (new RoundsDefenseLayoutResult(), false);
        }
        request = request with { Defenders = defenders };
        request = request with { MinRange = minRange };
        var layout = DefenseRecordLayout(record, killbox);
        RecordLayoutSnapshot(state.Snapshot, tick, new SnapshotLayout { Point = SnapshotApi.LayoutCover, Request = request, Record = record, Site = site.Cells }, call);
        DefenseApproaches approaches;
        try
        {
            approaches = PolicyApi.DefenseApproachesFor(request, layout);
        }
        catch (DomainException ex)
        {
            DefenseAction("defense-layout", TelemetryLevel.Info, "refused", "cover_census", "cover", new Dictionary<string, object?> { ["error"] = ex }, call);
            return (new RoundsDefenseLayoutResult(), false);
        }
        if (approaches.Hold != "")
        {
            DefenseAction("defense-layout", TelemetryLevel.Info, "waiting", "cover_held", "cover", new Dictionary<string, object?> { ["detail"] = approaches.Hold }, call);
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
        var (verdict, admitted) = AdmitStandardSubject(p.Journal, goal, DefenseCoverPrefix, state.Snapshot, call);
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
        p.Journal.CommitMethodReason(goal.Standard.Id, goal.Revision, method, "", plan, call);
        DefenseAction("defense-layout", TelemetryLevel.Info, "applied", "cover_clearance", method.Value, new Dictionary<string, object?> { ["things"] = clearances.Count }, call);
        return (new RoundsDefenseLayoutResult { Verdict = BuildingReasonAdmitted, Plan = id }, true);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The policy layout of a stored defense record: its firing positions, trap lane and the tier buildings.</summary>
    internal static DefenseLayout DefenseRecordLayout(DefenseLayoutRecord record, DefenseKillbox killbox)
    {
        var firing = new List<FiringPosition>();
        for (var i = 0; i < record.Firing.Count; i++)
        {
            var cell = record.Firing[i];
            var retreat = record.Retreat.Count == record.Firing.Count ? record.Retreat[i] : cell;
            firing.Add(new FiringPosition(cell, cell, retreat));
        }
        var tiers = new List<DefenseTier>();
        foreach (var tier in record.Tiers)
        {
            var buildings = new List<Building>();
            foreach (var b in tier.Buildings)
            {
                buildings.Add(new Building(b.Definition, b.Cell, b.Rotation, b.Stuff));
            }
            tiers.Add(new DefenseTier { Name = tier.Name, Reserved = [.. tier.Reserved], Buildings = [.. buildings] });
        }
        return new DefenseLayout
        {
            Toward = killbox.Toward,
            Width = (int)killbox.Width,
            Entry = killbox.Entry,
            TrapLane = [.. record.TrapLane],
            Firing = [.. firing],
            Tiers = [.. tiers],
        };
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
                output.Add(new DefenseArrival(raid.LordId, crossing, raid.SpawnTick));
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
            else if (cell.Cover.Designation is not { } designation)
            {
                Hold("unknown_kind");
            }
            else
            {
                clearances.Add(new CoverClearance(cell.Cover.ThingId, cell.Cover.DefName, designation, cover.Cell));
            }
        }
        return (clearances, held);
    }
}
