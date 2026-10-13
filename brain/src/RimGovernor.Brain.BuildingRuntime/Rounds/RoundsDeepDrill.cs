using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native reads of the deep drill planner: the building census and the research read on top of the building source.</summary>
internal interface IDeepDrillBuildingSource : IRoundsBuildingSource
{
    (EntityRows<O.BuildingState> Rows, BridgeResult Result) ReadBuildings(C.Identity identity, CancellationToken ct);

    (ResearchRead Read, BridgeResult Result) ReadResearch(C.Identity identity, CancellationToken ct);
}

/// <summary>One deep drill site the colony could build on: the preview and the plan that would place it.</summary>
internal sealed class DeepDrillPlacement
{
    public string Id = "";
    public DeepResourceLump Site = null!;
    public double Distance;
    public GenerationSnapshot Snapshot = null!;
    public PlanSpec Plan = null!;
    public BuildingPreview Preview = null!;
}

/// <summary>What one deep drill review read: the sites worth drilling, whether a drill stands and the placeable sites.</summary>
internal sealed class DeepDrillReading
{
    public IDeepDrillBuildingSource Native = null!;
    public ColonyProjection F = null!;
    public List<DeepResourceLump> Sites = [];
    public bool Built;
    public List<DeepDrillPlacement> Placeable = [];

    public bool Place(string id, out DeepDrillPlacement placement)
    {
        foreach (var p in Placeable)
        {
            if (p.Id == id)
            {
                placement = p;
                return true;
            }
        }
        placement = null!;
        return false;
    }
}

public sealed partial class RoundsResourcePlanner
{
    /// <summary>Deconstructs the first exhausted, undesignated deep drill that has no method yet.</summary>
    internal (RoundsResourceResult Result, bool Handled) RemoveExhaustedDrill(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, ColonyProjection f, DateTimeOffset started)
    {
        if (!f.DeepResources.TryGetValue(out var deep))
        {
            return (new RoundsResourceResult(), false);
        }
        foreach (var drill in ExhaustedDrills(deep))
        {
            var prefix = $"deconstruct-drill-{drill.Id}-";
            var (_, ok) = AdmitSubject(call, _reviewer.Player.Journal, prefix, StandardMethodPlans(goal.History, goal.Standard.Episode), state.Snapshot);
            if (!ok)
            {
                continue;
            }
            var method = NextMethodId(prefix, HistoryMethodIds(goal.History, goal.Standard.Episode));
            var planId = PlanIds.MintPlanId();
            var action = DomainAction.NewDeconstructionAction(new ActionId(planId.Value + "-0"), new Deconstruction(drill.Id, drill.Definition, drill.Position));
            var spec = new PlanSpec(planId, new PlanRevision(1), [action]);
            var player = _reviewer.Player;
            player.Current(call, epoch);
            var elapsed = _reviewer.Clock.Now() - started;
            if (player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
            {
                throw new ControlException("removeExhaustedDrill: player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
            }
            player.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, spec, call);
            return (new RoundsResourceResult { Verdict = BuildingReasonAdmitted, Plan = planId }, true);
        }
        return (new RoundsResourceResult(), false);
    }

    /// <summary>Reads the deep drill facts of a review; null when no deep drilling research is owed or the native cannot read buildings.</summary>
    internal DeepDrillReading? ReadDeepDrill(CancellationToken call, ControlState state, RoundsReview review)
    {
        var items = _reviewer.ItemFacts(call, state.Snapshot);
        if (PolicyApi.DeepDrillingResearch(null, review.ResourceRunwayState(), items).Count == 0)
        {
            return null;
        }
        if (_native is not IDeepDrillBuildingSource native)
        {
            return null;
        }
        var id = native.Identity(call).Identity;
        Identity expected;
        try
        {
            expected = ObservationApi.DecodeIdentity(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var reading = _reviewer.ObserveColony(call, native, expected, ["DeepDrill"]);
        var f = reading.Projection;
        var research = native.ReadResearch(Boundary.Identity(state.Snapshot), call).Read;
        try
        {
            Boundary.Context(research.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("deepDrillReading: stale research context");
        }
        if (!RoundsCachedFresh(BridgeApi.FactResearch, new Tick(research.Context.Tick), f.Identity.Tick))
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsCachedFresh(bridge.FactResearch, domain.Tick(research.Context.GetTick()), f.Identity");
        }
        var finished = new ResearchFacts();
        foreach (var name in research.Finished)
        {
            finished.Finished.Add(new ResearchProjectId(name));
        }
        f.Facts.Research = Known(finished);
        RecordStepRead("deepdrill", PolicyApi.MaintainResource, state.Snapshot, f);
        var output = new DeepDrillReading { Native = native, F = f };
        output.Sites = DeepDrillSites(f, review.ResourceRunwayState(), items);
        if (output.Sites.Count == 0)
        {
            return output;
        }
        var buildings = native.ReadBuildings(Boundary.Identity(state.Snapshot), call).Rows;
        try
        {
            Boundary.Context(buildings.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("deepDrillReading: stale buildings context");
        }
        if (!RoundsCachedFresh(BridgeApi.FactColony, new Tick(buildings.AsOf()), f.Identity.Tick))
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsCachedFresh(bridge.FactColony, domain.Tick(buildings.AsOf()), f.Identity.Tick)");
        }
        foreach (var row in buildings.Rows.Values)
        {
            if (row.Building?.DefName == "DeepDrill" || row.BuildDefName == "DeepDrill")
            {
                output.Built = true;
                return output;
            }
        }
        f.Center().TryGetValue(out var center);
        foreach (var site in output.Sites)
        {
            var distance = Math.Sqrt(Math.Pow(site.Centre.X - center.X, 2) + Math.Pow(site.Centre.Z - center.Z, 2));
            var (placement, ok) = ReadDeepDrillPlacement(call, native, state, f, site, distance);
            if (ok)
            {
                output.Placeable.Add(placement!);
            }
        }
        return output;
    }

    internal (DeepDrillPlacement? Placement, bool Ok) ReadDeepDrillPlacement(CancellationToken call, IDeepDrillBuildingSource native, ControlState state, ColonyProjection f, DeepResourceLump site, double distance)
    {
        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };
        var building = new Building("DeepDrill", site.Centre, Rotation.North, "");
        var action = DomainAction.NewBuildingAction(new ActionId(planId.Value + "-0"), building, PolicyApi.PlannerTier(PolicyApi.MaintainResource, ""));
        var preview = native.PreviewBuilding(action, snapshot, call).Preview;
        var p = preview.Preview;
        var fk = p.Footprint.TryGetValue(out var footprint);
        var lk = p.CanPlace.TryGetValue(out var legal);
        var sk = p.SafeToPlace.TryGetValue(out var safe);
        var rk = p.WatchCellsAccessible.TryGetValue(out var reachable);
        if (!fk || !lk || !legal || !sk || !safe || !rk || !reachable || !DeepDrillFootprint(f.Cells, footprint!, site.Centre))
        {
            return (null, false);
        }
        var spec = new PlanSpec(planId, new PlanRevision(1), [action]);
        return (new DeepDrillPlacement
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"deep-drill-{site.Definition}-{site.Centre.X}-{site.Centre.Z}"),
            Site = site,
            Distance = distance,
            Snapshot = snapshot,
            Plan = spec,
            Preview = preview,
        }, true);
    }

    /// <summary>Admits the deep drill placement the supply plan opens, once per site; handled when the planner settled the step.</summary>
    internal (RoundsResourceResult Result, bool Handled) DeepDrill(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, RoundsReview review, DateTimeOffset started)
    {
        var items = _reviewer.ItemFacts(call, state.Snapshot);
        if (PolicyApi.DeepDrillingResearch(null, review.ResourceRunwayState(), items).Count == 0)
        {
            return (new RoundsResourceResult(), false);
        }
        var supply = _reviewer.ResourceSupply(call, state, review, goal);
        var drill = supply.Drill;
        if (drill is null)
        {
            return (new RoundsResourceResult(), false);
        }
        var (removed, handled) = RemoveExhaustedDrill(call, epoch, state, goal, drill.F, started);
        if (handled)
        {
            return (removed, handled);
        }
        if (drill.Sites.Count == 0)
        {
            return (new RoundsResourceResult(), false);
        }
        if (drill.Built)
        {
            return (new RoundsResourceResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = StockWaitTicks }, true);
        }
        foreach (var e in supply.Plan.Plan.Portfolio)
        {
            if (e.Candidate.Kind != PolicyApi.CandidateDeepDrill || e.Decision != PolicyApi.SupplyOpen)
            {
                continue;
            }
            if (!drill.Place(e.Candidate.Id, out var place))
            {
                continue;
            }
            try
            {
                _reviewer.Player.Journal.LoadMethod(goal.Standard.Id, goal.Standard.Episode, new MethodId(place.Id), call);
                return (new RoundsResourceResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "deep_drill_method") }, true);
            }
            catch (NotFoundException)
            {
            }
            var player = _reviewer.Player;
            player.Current(call, epoch);
            var elapsed = _reviewer.Clock.Now() - started;
            if (player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
            {
                throw new ControlException("deepDrill: player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
            }
            var decision = AdmitMethod(call, player.Journal, new BuildingMethodRequest
            {
                Owner = goal,
                Method = new MethodId(place.Id),
                Plan = place.Plan,
                Current = place.Snapshot,
                Tick = drill.F.Identity.Tick,
                Bounds = Known(drill.F.Bounds),
                Stock = place.Preview.Stock,
                Previews = [place.Preview.Preview],
                Purpose = PolicyApi.Rounds,
            });
            if (!decision.Admitted)
            {
                return (new RoundsResourceResult { Verdict = AdmissionRefused(decision) }, true);
            }
            return (new RoundsResourceResult { Verdict = BuildingReasonAdmitted, Plan = place.Plan.Id }, true);
        }
        return (new RoundsResourceResult(), false);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The deep resource lumps of the needed resources, nearest the colony centre first; empty without a scanner, the research or the drill.</summary>
    internal static List<DeepResourceLump> DeepDrillSites(ColonyProjection f, IReadOnlyList<ResourceRunway> runways, ItemFacts items)
    {
        var known = f.DeepResources.TryGetValue(out var deep);
        var ak = f.DefinitionAvailable("DeepDrill").TryGetValue(out var available);
        var planned = f.Center().TryGetValue(out var center);
        if (!planned || !known || PolicyApi.ResearchGate(["DeepDrilling", "GroundPenetratingScanner"], f.Facts.Research) != "" || !ak || !available)
        {
            return [];
        }
        var scanner = false;
        foreach (var row in deep!.GroundScanners)
        {
            scanner = scanner || (row.Definition == "GroundPenetratingScanner" && row.Built.TryGetValue(out var built) && built);
        }
        if (!scanner)
        {
            return [];
        }
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in runways)
        {
            var dk = row.Deficit.TryGetValue(out var deficit);
            var sk = f.ResourceStock(row.Resource).TryGetValue(out var stock);
            if (dk && deficit && sk && stock < row.Target && items.IsDeepResource(row.Resource))
            {
                needed.Add(row.Resource.Value);
            }
        }
        var sites = new List<DeepResourceLump>();
        foreach (var lump in deep.Lumps)
        {
            if (needed.Contains(lump.Definition) && lump.Count > 0)
            {
                sites.Add(lump);
            }
        }
        long Distance(Cell c)
        {
            long x = (long)c.X - center.X;
            long z = (long)c.Z - center.Z;
            return (x * x) + (z * z);
        }

        sites.Sort((a, b) =>
        {
            var da = Distance(a.Centre);
            var db = Distance(b.Centre);
            if (da != db)
            {
                return da.CompareTo(db);
            }
            if (a.Centre.X != b.Centre.X)
            {
                return a.Centre.X.CompareTo(b.Centre.X);
            }
            return a.Centre.Z.CompareTo(b.Centre.Z);
        });
        return sites;
    }

    internal static List<DeepDrill> ExhaustedDrills(DeepResources deep)
    {
        var output = new List<DeepDrill>();
        foreach (var drill in deep.Drills)
        {
            if (drill.Depleted.TryGetValue(out var depleted) && depleted && drill.Designated.TryGetValue(out var designated) && !designated)
            {
                output.Add(drill);
            }
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return output;
    }

    /// <summary>Whether the drill footprint is all open, unroofed, walkable ground that includes the lump's anchor cell.</summary>
    internal static bool DeepDrillFootprint(IReadOnlyList<SiteCell> cells, IReadOnlyList<Cell> footprint, Cell anchor)
    {
        if (footprint.Count == 0)
        {
            return false;
        }
        var seen = new Dictionary<Cell, SiteCell>();
        foreach (var row in cells)
        {
            seen[row.Cell] = row;
        }
        var onLump = false;
        foreach (var cell in footprint)
        {
            var exists = seen.TryGetValue(cell, out var row);
            if (!exists || !row!.Roofed.TryGetValue(out var roof) || roof || row.Occupied() || !row.Walkable.TryGetValue(out var walkable) || !walkable)
            {
                return false;
            }
            onLump = onLump || cell == anchor;
        }
        return onLump;
    }
}
