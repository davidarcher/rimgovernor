namespace RimGovernor.Brain.BuildingRuntime;

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

public sealed partial class Rounder
{
    internal TunnelMemory Tunnels { get; } = new();
}

public sealed partial class RoundsResourcePlanner
{
    internal const int TunnelMaxCorridor = 16;

    /// <summary>Digs a corridor to ore the surface sources cannot reach; dispatched when an excavation method was driven.</summary>
    internal async Task<(RoundsResourceResult Result, bool Dispatched)> TunnelToBuriedOreAsync(ControlState state, StandardState goal, Tick reviewTick, C.Identity identity, Resource resource, CancellationToken call, CancellationToken epoch)
    {
        if (native is not IRoundsExcavationSource source)
        {
            return (default, false);
        }

        var p = reviewer.Player;
        var dig = new RoundsBuildingPlanner { Reviewer = reviewer, Concern = PolicyFunctions.MaintainResource, Excavation = source };
        var review = p.Journal.LoadRounds(call);
        var (last, _) = await native.IdentityAsync(call).ConfigureAwait(false);
        Identity expected;
        try
        {
            expected = ObservationPackage.DecodeIdentity(last);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("tunnelToBuriedOre: boundary", e);
        }

        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, reviewTick))
        {
            throw new ControlException("tunnelToBuriedOre: boundary");
        }

        var reading = await reviewer.ObserveColonyAsync(native, expected, null, call).ConfigureAwait(false);
        var step = new ExcavationStep { State = state, Review = review, Owner = goal, Facts = reading.Projection, Read = reading };

        (RoundsResourceResult Result, bool Dispatched) Finish(RoundsBuildingResult result)
        {
            var output = new RoundsResourceResult { Verdict = result.Verdict };
            foreach (var m in result.Decision.Standard.Methods)
            {
                if (RoundsBuildingPlanner.IsExcavationMethod(m.MethodId))
                {
                    output = output with { Plan = m.Plan };
                }
            }

            return (output, true);
        }
        async Task<(RoundsBuildingResult Result, bool Done)> ResumeAsync(ExcavationTarget target)
        {
            step = new ExcavationStep { State = step.State, Review = step.Review, Owner = step.Owner, Facts = step.Facts, Read = step.Read, Target = target };
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
            (rows, _, _) = await native.ReadResourceSourcesAsync(identity, resource.Value, call).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (default, false);
        }

        var remote = await MiningReachAsync(state, reviewTick, call).ConfigureAwait(false);
        var (ore, ok) = PolicyFunctions.BuriedResourceSource(rows, remote);
        if (!ok)
        {
            return (default, false);
        }

        var world = BuildingRuntimePackage.StockpileWorld(state.Snapshot);
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
        var facts = reading.Projection;
        if (native is not IDefinitionSource definitions)
        {
            throw new InvalidOperationException("roof rules: the native source serves no definitions");
        }

        var catalog = await definitions.DefinitionCatalogAsync(identity, call).ConfigureAwait(false)
            ?? throw new InvalidOperationException("roof rules: the native source returned no definition catalog");
        var roofs = catalog.RoofRules();
        if (!facts.Center().TryGetValue(out var anchor))
        {
            return (new RoundsResourceResult { Verdict = BuildingRuntimePackage.BuildingReasonNoLayoutPlan }, false);
        }

        var request = new ExcavationSiteRequest
        {
            Bounds = facts.Bounds,
            Region = facts.Region,
            Anchor = anchor,
            Cells = [.. facts.Cells],
            Protected = [.. protectedCells],
            MinCorridor = 1,
            MaxCorridor = TunnelMaxCorridor,
            RoofSupport = facts.RoofSupport,
            Roofs = roofs,
        };
        SnapshotPackage.NoteExcavation(request);
        var targets = PolicyFunctions.CorridorExcavationSites(request, ore.Cell);
        var snapshot = state.Snapshot with { Revision = new PlanRevision(1) };
        for (var i = 0; i < targets.Count; i++)
        {
            if (i >= RoundsBuildingPlanner.ExcavationCandidates)
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
