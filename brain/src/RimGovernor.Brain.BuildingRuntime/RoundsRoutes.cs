namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Composes MaintainRoutes' building method: cut a door into the wall enclosing a facility no colonist can
    /// reach. The deficit and its release are the native reachability census, never a flood fill: the latch
    /// opens only once a measured census reads the facility reachable by some colonist.
    /// </summary>
    public static RoundsBuildingPlanner NewRoundsRoutesPlanner(Rounder? reviewer, IRoundsBuildingSource? native)
    {
        if (reviewer is null || native is null || !reviewer.MethodEnabled(MaintainRoutes))
        {
            throw new ControlException("NewRoundsRoutesPlanner: reviewer == nil || native == nil || !reviewer.methodEnabled(Pol.MaintainRoutes)");
        }
        if (native is not Observation.IRoundsSource)
        {
            throw new ControlException("NewRoundsRoutesPlanner: !ok");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = MaintainRoutes, Definition = reviewer.Policy.Routes.Door };
    }

    /// <summary>Lists the door the policy opens breaches with so the census read carries its availability.</summary>
    internal IReadOnlyList<string> RoutesDefinitions() => [Reviewer.Policy.Routes.Door];

    /// <summary>Re-reviews the fresh census under the review's latch and maps the policy outcome onto the planner.</summary>
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectRoutes(Observation.ColonyProjection facts, Pol.RoundsLatches latches)
    {
        var p = Reviewer.Policy.Routes;
        var review = ReviewRoutes(facts.Facts.Upkeep.Routes, latches.Routes, p);
        if (!review.Active)
        {
            return (null, BuildingReasonNoDeficit);
        }
        if (!review.Known)
        {
            return (null, FieldUnavailable("routes"));
        }
        var routes = new Pol.RoutesFacts();
        foreach (var d in facts.Definitions)
        {
            if (d.Name == p.Door)
            {
                routes = routes with { DoorAvailable = d.Available };
            }
        }
        var proposal = SelectRoutesMethod(review, routes, p);
        switch (proposal.Method)
        {
            case Pol.RoutesMethod.Build:
                var resolved = Copy();
                resolved.Routes = proposal;
                resolved.Definition = proposal.Definition;
                return (resolved, new Verdict());
            case Pol.RoutesMethod.Unknown:
                // The review is known by here, so the door's availability is what the game did not report.
                return (null, FieldUnavailable(p.Door + "_availability"));
            case Pol.RoutesMethod.NoMethod:
                return (null, BuildingReasonNoDeficit);
            case Pol.RoutesMethod.DoorUnavailable:
                var (door, _) = DefinitionRow(facts, p.Door);
                return (null, DefinitionAvailability(door));
            case Pol.RoutesMethod.DoorPending:
                return (null, AwaitingPlan("route_door", "ordered"));
            case Pol.RoutesMethod.NoBreach:
                return (null, NoSpace("route_breach"));
            default:
                return (null, AwaitingMethod(proposal.Method));
        }
    }

    /// <summary>
    /// Previews the proposal's breach cells nearest first and admits the first one native reports legal with a
    /// one-cell footprint on the wall cell; one door is enough to open the room, so the batch is a single
    /// action. A door rotates with its wall, so both orientations are tried.
    /// </summary>
    internal async Task<(IReadOnlyList<Pol.Preview>? Previews, Pol.StockObservation Stock, Verdict Verdict)> PreviewRoutesAsync(GenerationSnapshot snapshot, Observation.ColonyProjection facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
    {
        var stock = new Pol.StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        if (Routes is not { Method: Pol.RoutesMethod.Build } routes)
        {
            throw new ControlException("previewRoutes: r.routes == nil || r.routes.Method != Pol.RoutesBuild");
        }
        var guarded = new HashSet<Cell>(protectedCells);
        var missing = "";
        foreach (var cell in routes.Breaches)
        {
            if (guarded.Contains(cell))
            {
                continue;
            }
            foreach (var rotation in new[] { Rotation.North, Rotation.East })
            {
                var building = new Building(routes.Definition, cell, rotation, routes.Stuff);
                var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-0"), building, PlannerTier(Concern, Phase));
                var (preview, _) = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
                var p = preview.Preview;
                var fk = p.Footprint.TryGetValue(out var footprint);
                var mk = p.MadeFromStuff.TryGetValue(out var made);
                var lk = p.CanPlace.TryGetValue(out var legal);
                var sk = p.SafeToPlace.TryGetValue(out var safe);
                if (!fk || !mk || !lk || !sk)
                {
                    if (missing == "")
                    {
                        missing = FirstUnknown(["preview_footprint", "preview_made_from_stuff", "preview_can_place", "preview_safe_to_place"], fk, mk, lk, sk);
                    }
                    continue;
                }
                if (!safe && DeliberateBreach(p))
                {
                    safe = true;
                    p = p with { SafeToPlace = Known<bool>(true) };
                }
                if (!made || footprint!.Count != 1 || footprint[0] != cell || !legal || !safe)
                {
                    continue;
                }
                stock = MergeRoundsStock(stock, preview.Stock, true);
                return ([p], stock, new Verdict());
            }
        }
        if (missing != "")
        {
            return (null, stock, FieldUnavailable(missing));
        }
        return (null, stock, NoSpace("breach_door_site"));
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// A preview whose only disturbance is the wall the door replaces: one wiped building, no blueprint, frame
    /// or cancelled work. Native reports a door over a wall as unsafe because the wall is wiped; here the wipe
    /// is the method, so the planner marks the placement safe itself. Any other disturbance keeps native's verdict.
    /// </summary>
    internal static bool DeliberateBreach(Pol.Preview p)
    {
        if (p.Blockers.Count != 1)
        {
            return false;
        }
        var b = p.Blockers[0];
        return b.Category == "Building" && b.Wiped && !b.Blueprint && !b.Frame && !b.Cancelled;
    }

    /// <summary>Names the first field whose known flag is false.</summary>
    internal static string FirstUnknown(IReadOnlyList<string> names, params bool[] known)
    {
        for (var i = 0; i < known.Length; i++)
        {
            if (!known[i])
            {
                return names[i];
            }
        }
        return "";
    }
}
