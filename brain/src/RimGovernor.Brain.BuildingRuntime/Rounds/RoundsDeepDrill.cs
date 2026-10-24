using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>One deep drill site the colony could build on: the preview and the plan that would place it.</summary>
internal sealed class DeepDrillPlacement
{
    public string Id = "";
    public PbObservations.DeepResourceLump Site = null!;
    public double Distance;
    public GenerationSnapshot Snapshot;
    public PlanSpec Plan = null!;
    public BuildingPreview Preview = null!;
}

/// <summary>What one deep drill review read: the sites worth drilling, whether a drill stands and the placeable sites.</summary>
internal sealed class DeepDrillReading
{
    public FacadeNativeClient Native = null!;
    public ColonyProjection F = null!;
    public List<PbObservations.DeepResourceLump> Sites = [];
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
    internal (RoundsResourceResult Result, bool Handled) RemoveExhaustedDrill(ControlState state, NeedOwner goal, ColonyProjection f, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var deep = f.DeepResources;
        var history = reviewer.Player.Journal.OwnerMethodHistory(goal, call);
        foreach (var drill in ExhaustedDrills(deep))
        {
            if (f.Facts.Preferences.Protects(drill.Id))
            {
                continue;
            }
            var prefix = $"deconstruct-drill-{drill.Id}-";
            var plans = history.Where(m => m.Method.Value.StartsWith(prefix, StringComparison.Ordinal)).Select(m => m.Plan).ToList();
            var (_, ok) = RoundsRefusalBudget.AdmitSubject(reviewer.Player.Journal, prefix, plans, state.Snapshot, call);
            if (!ok)
            {
                continue;
            }
            var method = RoundsRefusalBudget.NextMethodId(prefix, [.. history.Select(m => m.Method)]);
            var planId = PlanIds.MintPlanId();
            var action = DomainAction.NewDeconstructionAction(new ActionId(planId.Value + "-0"), new Deconstruction(drill.Id, drill.Definition, drill.Position));
            var spec = new PlanSpec(planId, new PlanRevision(1), [action]);
            var player = reviewer.Player;
            player.Current(call, epoch);
            var elapsed = reviewer.Clock.Now() - started;
            if (player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
            {
                throw new ControlException("removeExhaustedDrill: player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
            }
            var commit = player.Journal.CommitOwnerWork(goal, method, "deconstruct exhausted drill", spec, call);
            if (commit.Outcome == Sto.ClaimOutcome.Conflict)
            {
                continue;
            }
            if (commit.Outcome == Sto.ClaimOutcome.Refused)
            {
                return (new RoundsResourceResult { Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(commit) }, true);
            }
            return (new RoundsResourceResult { Verdict = RoundsBuildingPlanner.OwnerCommitVerdict(commit), Plan = planId }, true);
        }
        return (new RoundsResourceResult(), false);
    }

    /// <summary>Reads the deep drill facts of a review; null when no deep drilling research is owed or the native cannot read buildings.</summary>
    internal async Task<DeepDrillReading?> ReadDeepDrillAsync(ControlState state, RoundsReview review, CancellationToken call)
    {
        var items = await reviewer.ItemFactsAsync(state.Snapshot, call).ConfigureAwait(false);
        if (DeepDrillRules.DeepDrillingResearch([], review.ResourceRunwayState(), items).Count == 0)
        {
            return null;
        }
        if (this.native is not FacadeNativeClient deepNative)
        {
            return null;
        }
        var id = (await deepNative.IdentityAsync(call).ConfigureAwait(false));
        Identity expected;
        try
        {
            expected = DecodeReads.DecodeIdentity(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var reading = await reviewer.ObserveColonyAsync(deepNative, expected, ["DeepDrill"], call).ConfigureAwait(false);
        var f = reading.Projection;
        var research = (await deepNative.ReadResearchAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false));
        try
        {
            Boundary.Context(research.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("deepDrillReading: stale research context");
        }
        if (!RoundsBuildingPlanner.RoundsCachedFresh(FactFamily.Research, new Tick(research.Context.Tick), f.Identity.Tick))
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsCachedFresh(bridge.FactResearch, domain.Tick(research.Context.GetTick()), f.Identity");
        }
        var finishedNames = new List<ResearchProjectId>();
        foreach (var name in research.Finished)
        {
            finishedNames.Add(new ResearchProjectId(name));
        }
        f = f with { Facts = f.Facts with { Research = Known(new ResearchFacts { Finished = [.. finishedNames] }) } };
        PlannerSnapshot.RecordStepRead("deepdrill", EconomyFamily.Resource.FilingKey, state.Snapshot, f);
        var output = new DeepDrillReading { Native = deepNative, F = f };
        output.Sites = DeepDrillSites(f, review.ResourceRunwayState(), items);
        if (output.Sites.Count == 0)
        {
            return output;
        }
        var buildings = (await deepNative.ReadBuildingsAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false));
        try
        {
            Boundary.Context(buildings.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("deepDrillReading: stale buildings context");
        }
        if (!RoundsBuildingPlanner.RoundsCachedFresh(FactFamily.Colony, new Tick(buildings.AsOf()), f.Identity.Tick))
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsCachedFresh(bridge.FactColony, domain.Tick(buildings.AsOf()), f.Identity.Tick)");
        }
        foreach (var row in buildings.Rows.Values())
        {
            if (row.Building.DefName == "DeepDrill" || row.Construction is PbObservations.ConstructionState.Build { Definition: "DeepDrill" })
            {
                output.Built = true;
                return output;
            }
        }
        f.Center().TryGetValue(out var center);
        foreach (var site in output.Sites)
        {
            var distance = Math.Sqrt(Math.Pow(site.Centre.X - center.X, 2) + Math.Pow(site.Centre.Z - center.Z, 2));
            var (placement, ok) = await ReadDeepDrillPlacementAsync(deepNative, state, f, site, distance, call).ConfigureAwait(false);
            if (ok)
            {
                output.Placeable.Add(placement!);
            }
        }
        return output;
    }

    internal static async Task<(DeepDrillPlacement? Placement, bool Ok)> ReadDeepDrillPlacementAsync(FacadeNativeClient native, ControlState state, ColonyProjection f, PbObservations.DeepResourceLump site, double distance, CancellationToken call)
    {
        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };
        var building = new Building("DeepDrill", site.Centre, Rotation.North, "");
        var action = DomainAction.NewBuildingAction(new ActionId(planId.Value + "-0"), building, ConstructionTierRules.PlannerTier(EconomyFamily.Resource.FilingKey, Pol.Phase.None));
        var preview = (await native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false));
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
    internal async Task<(RoundsResourceResult Result, bool Handled)> DeepDrillAsync(ControlState state, NeedOwner goal, RoundsReview review, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var items = await reviewer.ItemFactsAsync(state.Snapshot, call).ConfigureAwait(false);
        if (DeepDrillRules.DeepDrillingResearch([], review.ResourceRunwayState(), items).Count == 0)
        {
            return (new RoundsResourceResult(), false);
        }
        var supply = await reviewer.ResourceSupplyAsync(state, review, goal, call).ConfigureAwait(false);
        var drill = supply.Drill;
        if (drill is null)
        {
            return (new RoundsResourceResult(), false);
        }
        var (removed, handled) = RemoveExhaustedDrill(state, goal, drill.F, started, call, epoch);
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
            return (new RoundsResourceResult { Verdict = BuildingReasonExistingWork }, true);
        }
        foreach (var e in supply.Plan.Plan.Portfolio)
        {
            if (e.Candidate.Kind != Pol.CandidateKind.DeepDrill || e.Decision != Pol.SupplyDecision.Open)
            {
                continue;
            }
            if (!drill.Place(e.Candidate.Id, out var place))
            {
                continue;
            }
            if (reviewer.Player.Journal.OwnerMethodPlan(goal, new MethodId(place.Id), call) is not null)
            {
                return (new RoundsResourceResult { Verdict = WaitFor(Cause.MethodUsed, "deep_drill_method") }, true);
            }
            var player = reviewer.Player;
            player.Current(call, epoch);
            var elapsed = reviewer.Clock.Now() - started;
            if (player.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
            {
                throw new ControlException("deepDrill: player.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
            }
            var decision = await AdmissionLog.AdmitMethodAsync(player.Journal, new BuildingMethodRequest
            {
                Owner = goal,
                Method = new MethodId(place.Id),
                Plan = place.Plan,
                Current = place.Snapshot,
                Tick = drill.F.Identity.Tick,
                Bounds = Known(drill.F.Bounds),
                Stock = place.Preview.Stock,
                Previews = [place.Preview.Preview],
                Purpose = Pol.Purpose.Rounds,
            }, call).ConfigureAwait(false);
            if (!decision.Admitted)
            {
                return (new RoundsResourceResult { Verdict = AdmissionLog.AdmissionRefused(decision) }, true);
            }
            return (new RoundsResourceResult { Verdict = BuildingReasonAdmitted, Plan = place.Plan.Id }, true);
        }
        return (new RoundsResourceResult(), false);
    }
}

partial class RoundsResourcePlanner
{
    /// <summary>The deep resource lumps of the needed resources, nearest the colony centre first; empty without a scanner, the research or the drill.</summary>
    internal static List<PbObservations.DeepResourceLump> DeepDrillSites(ColonyProjection f, IReadOnlyList<ResourceRunway> runways, ItemFacts items)
    {
        var deep = f.DeepResources;
        var ak = f.DefinitionAvailable("DeepDrill").TryGetValue(out var available);
        var planned = f.Center().TryGetValue(out var center);
        if (!planned || ResearchRules.ResearchGate(["DeepDrilling", "GroundPenetratingScanner"], f.Facts.Research) != "" || !ak || !available)
        {
            return [];
        }
        var scanner = false;
        foreach (var row in deep.GroundScanners)
        {
            scanner = scanner || row.Definition == "GroundPenetratingScanner";
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
        var sites = new List<PbObservations.DeepResourceLump>();
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

    /// <summary>The drills with no valuable deposit left under them and no standing Deconstruct designation, by id.</summary>
    internal static List<PbObservations.DeepDrill> ExhaustedDrills(PbObservations.DeepResources deep)
    {
        var output = new List<PbObservations.DeepDrill>();
        foreach (var drill in deep.Drills)
        {
            if (drill.Deposit is null && !drill.Designated)
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
