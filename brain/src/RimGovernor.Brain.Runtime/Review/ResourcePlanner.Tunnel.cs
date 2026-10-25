namespace RimGovernor.Brain.Runtime;

internal readonly record struct SitedTunnel(Cell Ore, ExcavationTarget Target);

/// <summary>Remembers which corridor was sited to each buried ore so the next step resumes it.</summary>
internal sealed class TunnelMemory
{
    private readonly object mu = new();
    private string world = "";
    private Dictionary<Resource, SitedTunnel>? sited;

    public (ExcavationTarget Target, bool Ok) Get(string newWorld, Resource resource, Cell ore)
    {
        lock (mu)
        {
            if (world != newWorld || sited is null || !sited.TryGetValue(resource, out var t) || t.Ore != ore)
            {
                return (new ExcavationTarget(), false);
            }

            return (t.Target, true);
        }
    }

    public void Set(string newWorld, Resource resource, Cell ore, ExcavationTarget target)
    {
        lock (mu)
        {
            if (world != newWorld || sited is null)
            {
                world = newWorld;
                sited = [];
            }

            sited[resource] = new SitedTunnel(ore, target);
        }
    }

    public void Forget(Resource resource)
    {
        lock (mu)
        {
            sited?.Remove(resource);
        }
    }
}

public sealed partial class ColonyReview
{
    internal TunnelMemory Tunnels { get; } = new();
}

public sealed partial class ResourcePlanner
{
    internal const int TunnelMaxCorridor = 16;

    /// <summary>Digs a corridor to ore the surface sources cannot reach; dispatched when an excavation method was driven.</summary>
    internal async Task<(ResourceResult Result, bool Dispatched)> TunnelToBuriedOreAsync(ControlState state, NeedOwner goal, Tick reviewTick, RimGovernor.Brain.GameFacts.Common.Identity identity, Resource resource, CancellationToken call, CancellationToken epoch)
    {
        if (native is not GameClient source)
        {
            return (default, false);
        }

        var p = reviewer.Player;
        var dig = new BuildingPlanner { Reviewer = reviewer, Concern = EconomyFamily.Resource.FilingKey, Need = EconomyFamily.Resource, Excavation = source };
        var review = p.Journal.LoadReview(call);
        var last = await native.IdentityAsync(call).ConfigureAwait(false);
        Identity expected;
        try
        {
            expected = new ObservationIdentity { Context = last.Context, Paused = last.Paused };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("tunnelToBuriedOre: boundary", e);
        }

        if (!BuildingPlanner.BuildingBoundary(expected, state.Snapshot, reviewTick))
        {
            throw new ControlException("tunnelToBuriedOre: boundary");
        }

        var reading = await reviewer.ObserveColonyAsync(native, expected, null, call).ConfigureAwait(false);
        var step = new ExcavationStep { State = state, Review = review, Owner = goal, Facts = reading };

        (ResourceResult Result, bool Dispatched) Finish(BuildingResult result)
        {
            var output = new ResourceResult { Verdict = result.Verdict };
            foreach (var m in result.Decision?.Owner.OpenMethods ?? [])
            {
                if (BuildingPlanner.IsExcavationMethod(m.Method))
                {
                    output = output with { Plan = m.Plan };
                }
            }

            return (output, true);
        }
        async Task<(BuildingResult Result, bool Done)> ResumeAsync(ExcavationTarget target)
        {
            step = new ExcavationStep { State = step.State, Review = step.Review, Owner = step.Owner, Facts = step.Facts, Target = target };
            var result = await dig.StepExcavationAsync(step, call, epoch).ConfigureAwait(false);
            return (result, result.Verdict.Is(Cause.MethodUsed) || result.Verdict.Is(Cause.SiteBlocked));
        }

        var project = dig.ExcavationProject(goal, call);
        if (project is not null)
        {
            var (result, done) = await ResumeAsync(project).ConfigureAwait(false);
            if (!done)
            {
                return Finish(result);
            }

            reviewer.Tunnels.Forget(resource);
        }

        IReadOnlyList<ResourceSource> rows;
        try
        {
            (rows, _) = await native.ReadResourceSourcesAsync(identity, resource.Value, call).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (default, false);
        }

        var remote = await MiningReachAsync(state, reviewTick, call).ConfigureAwait(false);
        var (ore, ok) = ResourceRules.BuriedResourceSource(rows, remote);
        if (!ok)
        {
            return (default, false);
        }

        var world = StockpilePlanner.StockpileWorld(state.Snapshot);
        var (sited, haveSited) = reviewer.Tunnels.Get(world, resource, ore.Cell);
        if (haveSited && project is null)
        {
            var (result, done) = await ResumeAsync(sited).ConfigureAwait(false);
            if (!done)
            {
                return Finish(result);
            }

            reviewer.Tunnels.Forget(resource);
        }

        void Check()
        {
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("tunnelToBuriedOre: session state changed");
            }
        }

        var held = p.Journal.BuildingReservations(state.Snapshot, call);
        var protectedCells = held.SelectMany(h => h.Footprint).ToList();
        var facts = reading;
        if (native is not IDefinitionSource definitions)
        {
            throw new InvalidOperationException("roof rules: the native source serves no definitions");
        }

        var catalog = await definitions.DefinitionCatalogAsync(identity, call).ConfigureAwait(false);
        var roofs = catalog.RoofRules();
        if (facts.Facts.LayoutPlan?.Center() is not { } anchor)
        {
            return (new ResourceResult { Verdict = Verdict.BuildingReasonNoLayoutPlan }, false);
        }

        var request = new ExcavationSiteRequest
        {
            Bounds = facts.Facts.MapBounds,
            Region = facts.PlanningWindow.Region,
            Anchor = anchor,
            Cells = [.. facts.PlanningWindow.Cells],
            Protected = [.. protectedCells],
            MinCorridor = 1,
            MaxCorridor = TunnelMaxCorridor,
            RoofSupport = facts.Facts.RoofSupport,
            Roofs = roofs,
        };
        SnapshotPlannerReplay.NoteExcavation(request);
        var targets = TunnelSiteRules.CorridorExcavationSites(request, ore.Cell);
        var snapshot = state.Snapshot with { Revision = new PlanRevision(1) };
        for (var i = 0; i < targets.Count; i++)
        {
            if (i >= BuildingPlanner.ExcavationCandidates)
            {
                break;
            }

            var target = targets[i];
            if (!await dig.VerifyExcavationAsync(snapshot, facts.Identity.Tick, target, Check, call).ConfigureAwait(false))
            {
                continue;
            }

            reviewer.Tunnels.Set(world, resource, ore.Cell, target);
            var (result, _) = await ResumeAsync(target).ConfigureAwait(false);
            return Finish(result);
        }

        return (default, false);
    }
}
