using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

/// <summary>One deep drill site the colony could build on: the preview and the plan that would place it.</summary>
internal sealed class DeepDrillPlacement
{
    public string Id = "";
    public RimGovernor.Brain.GameFacts.Observations.DeepResourceLump Site = null!;
    public double Distance;
    public GenerationSnapshot Snapshot;
    public PlanSpec Plan = null!;
    public BuildingPreview Preview = null!;
}

/// <summary>What one deep drill review read: the sites worth drilling, whether a drill stands and the placeable sites.</summary>
internal sealed class DeepDrillReading
{
    public GameClient Native = null!;
    public ColonyReading F = null!;
    public List<RimGovernor.Brain.GameFacts.Observations.DeepResourceLump> Sites = [];
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

public sealed partial class ResourcePlanner
{
    /// <summary>Deconstructs the first exhausted, undesignated deep drill that has no method yet.</summary>
    internal (ResourceResult Result, bool Handled) RemoveExhaustedDrill(ControlState state, NeedOwner goal, ColonyReading f, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var deep = f.Snapshot.DeepResources;
        var history = reviewer.Player.Journal.OwnerMethodHistory(goal, call);
        foreach (var drill in DeepDrillRules.ExhaustedDrills(deep))
        {
            if (f.Facts.Preferences.Protects(drill.Id))
            {
                continue;
            }
            var prefix = $"deconstruct-drill-{drill.Id}-";
            var plans = history.Where(m => m.Method.Value.StartsWith(prefix, StringComparison.Ordinal)).Select(m => m.Plan).ToList();
            var (_, ok) = ReviewRefusalBudget.AdmitSubject(reviewer.Player.Journal, prefix, plans, state.Snapshot, call);
            if (!ok)
            {
                continue;
            }
            var method = ReviewRefusalBudget.NextMethodId(prefix, [.. history.Select(m => m.Method)]);
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
                return (new ResourceResult { Verdict = BuildingPlanner.OwnerCommitVerdict(commit) }, true);
            }
            return (new ResourceResult { Verdict = BuildingPlanner.OwnerCommitVerdict(commit), Plan = planId }, true);
        }
        return (new ResourceResult(), false);
    }

    /// <summary>Reads the deep drill facts of a review; null when no deep drilling research is owed or the native cannot read buildings.</summary>
    internal async Task<DeepDrillReading?> ReadDeepDrillAsync(ControlState state, ReviewStep review, CancellationToken call)
    {
        var items = await reviewer.ItemFactsAsync(state.Snapshot, call).ConfigureAwait(false);
        if (DeepDrillRules.DeepDrillingResearch([], review.ResourceRunwayState(), items).Count == 0)
        {
            return null;
        }
        if (this.native is not GameClient deepNative)
        {
            return null;
        }
        var id = (await deepNative.IdentityAsync(call).ConfigureAwait(false));
        Identity expected;
        try
        {
            expected = new ObservationIdentity { Context = id.Context, Paused = id.Paused };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        if (!BuildingPlanner.BuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var reading = await reviewer.ObserveColonyAsync(deepNative, expected, ["DeepDrill"], call).ConfigureAwait(false);
        var f = reading;
        var research = (await deepNative.ReadResearchAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false));
        try
        {
            Boundary.Context(research.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("deepDrillReading: stale research context");
        }
        if (!BuildingPlanner.CachedFresh(FactFamily.Research, new Tick(research.Context.Tick), f.Identity.Tick))
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsCachedFresh(bridge.FactResearch, domain.Tick(research.Context.GetTick()), f.Identity");
        }
        var catalog = await deepNative.DefinitionCatalogAsync(Boundary.Identity(state.Snapshot), call).ConfigureAwait(false);
        f = f with { Facts = f.Facts with { Research = FrameReads.FrameResearch(research, catalog) } };
        PlannerSnapshot.RecordStepRead("deepdrill", EconomyFamily.Resource.FilingKey, state.Snapshot, f);
        var output = new DeepDrillReading { Native = deepNative, F = f };
        output.Sites = DeepDrillRules.DeepDrillSites(f.Facts, f.Snapshot.DeepResources, review.ResourceRunwayState(), items, nameof(RimWorld.ThingDefOf.DeepDrill), nameof(RimWorld.ThingDefOf.GroundPenetratingScanner));
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
        if (!BuildingPlanner.CachedFresh(FactFamily.Colony, new Tick(buildings.AsOf()), f.Identity.Tick))
        {
            throw new ControlException("deepDrillReading: err != nil || !roundsCachedFresh(bridge.FactColony, domain.Tick(buildings.AsOf()), f.Identity.Tick)");
        }
        foreach (var row in buildings.Rows.Values())
        {
            if (row.Building.DefName == "DeepDrill" || row.Construction is RimGovernor.Brain.GameFacts.Observations.ConstructionState.Build { Definition: "DeepDrill" })
            {
                output.Built = true;
                return output;
            }
        }
        var center = f.Facts.LayoutPlan?.Center() ?? throw new RequiredFactException("layout center for deep drilling");
        foreach (var site in output.Sites)
        {
            var distance = DeepDrillRules.SiteDistance(site.Centre, center);
            var (placement, ok) = await ReadDeepDrillPlacementAsync(deepNative, state, f, site, distance, call).ConfigureAwait(false);
            if (ok)
            {
                output.Placeable.Add(placement!);
            }
        }
        return output;
    }

    internal static async Task<(DeepDrillPlacement? Placement, bool Ok)> ReadDeepDrillPlacementAsync(GameClient native, ControlState state, ColonyReading f, RimGovernor.Brain.GameFacts.Observations.DeepResourceLump site, double distance, CancellationToken call)
    {
        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };
        var building = new Building("DeepDrill", site.Centre, Rotation.North, "");
        var action = DomainAction.NewBuildingAction(new ActionId(planId.Value + "-0"), building, ConstructionTierRules.PlannerTier(EconomyFamily.Resource.FilingKey, Pol.Phase.None));
        var preview = (await native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false));
        var p = preview.Preview;
        if (!p.CanPlace || !p.SafeToPlace || PreviewRules.WatchUnreachable(p) || !DeepDrillRules.DeepDrillFootprint(f.PlanningWindow.Cells, p.Footprint, site.Centre))
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
    internal async Task<(ResourceResult Result, bool Handled)> DeepDrillAsync(ControlState state, NeedOwner goal, ReviewStep review, DateTimeOffset started, CancellationToken call, CancellationToken epoch)
    {
        var items = await reviewer.ItemFactsAsync(state.Snapshot, call).ConfigureAwait(false);
        if (DeepDrillRules.DeepDrillingResearch([], review.ResourceRunwayState(), items).Count == 0)
        {
            return (new ResourceResult(), false);
        }
        var supply = await reviewer.ResourceSupplyAsync(state, review, goal, call).ConfigureAwait(false);
        var drill = supply.Drill;
        if (drill is null)
        {
            return (new ResourceResult(), false);
        }
        var (removed, handled) = RemoveExhaustedDrill(state, goal, drill.F, started, call, epoch);
        if (handled)
        {
            return (removed, handled);
        }
        if (drill.Sites.Count == 0)
        {
            return (new ResourceResult(), false);
        }
        if (drill.Built)
        {
            return (new ResourceResult { Verdict = BuildingReasonExistingWork }, true);
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
                return (new ResourceResult { Verdict = WaitFor(Cause.MethodUsed, "deep_drill_method") }, true);
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
                Bounds = drill.F.Facts.MapBounds,
                Stock = place.Preview.Stock,
                Previews = [place.Preview.Preview],
                Purpose = Pol.Purpose.ReviewState,
            }, call).ConfigureAwait(false);
            if (!decision.Admitted)
            {
                return (new ResourceResult { Verdict = AdmissionLog.AdmissionRefused(decision) }, true);
            }
            return (new ResourceResult { Verdict = BuildingReasonAdmitted, Plan = place.Plan.Id }, true);
        }
        return (new ResourceResult(), false);
    }
}
