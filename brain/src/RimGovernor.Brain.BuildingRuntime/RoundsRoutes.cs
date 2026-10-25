using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>
    /// Composes the layout/routes Need's building method (#3265): cut a door into the wall enclosing a facility no
    /// colonist can reach, or set a traffic door the policy wants held. The deficit is the native reachability census
    /// read fresh, never a flood fill, and the Methods commit under the Need owner.
    /// </summary>
    public static RoundsBuildingPlanner NewRoundsRoutesPlanner(Rounder? reviewer, IRoundsBuildingSource? native)
    {
        if (reviewer is null || native is null || !reviewer.Needs.Contains(LayoutFamily.Routes))
        {
            throw new ControlException("NewRoundsRoutesPlanner: the reviewer, its native source or the layout/routes Need is missing");
        }
        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsRoutesPlanner: !ok");
        }
        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = LayoutFamily.Routes.FilingKey, Need = LayoutFamily.Routes, Definition = reviewer.Policy.Routes.Door };
    }

    /// <summary>Lists the door the policy opens breaches with so the census read carries its availability.</summary>
    internal IReadOnlyList<string> RoutesDefinitions() => [Reviewer.Policy.Routes.Door];

    /// <summary>Reviews the fresh census and maps the policy outcome onto the planner.</summary>
    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectRoutes(RimGovernor.Brain.Bridge.Contracts.ColonyReading facts)
    {
        var p = Reviewer.Policy.Routes;
        var review = RoutesRules.ReviewRoutes(facts.Facts.Upkeep.Routes, p);
        if (!review.Active)
        {
            return (null, BuildingReasonNoDeficit);
        }
        var routes = new Pol.RoutesFacts();
        foreach (var d in facts.Facts.Definitions)
        {
            if (d.Name == p.Door)
            {
                routes = routes with { DoorAvailable = d.Available };
            }
        }
        var proposal = RoutesRules.SelectRoutesMethod(review, routes, p);
        switch (proposal.Method)
        {
            case Pol.RoutesMethod.NoMethod:
                return (null, BuildingReasonNoDeficit);
            case Pol.RoutesMethod.DoorUnavailable:
                var (door, _) = DefinitionRow(facts, p.Door);
                return (null, DefinitionAvailability(door));
            case Pol.RoutesMethod.DoorPending:
                return (null, AwaitingPlan("route_door", "ordered"));
            case Pol.RoutesMethod.NoBreach:
                return (null, NoSpace("route_breach"));
            case Pol.RoutesMethod.Build:
                break;
        }

        var resolved = Copy();
        resolved.Routes = proposal;
        resolved.Definition = proposal.Definition;
        return (resolved, new Verdict());
    }

    /// <summary>
    /// Previews the proposal's breach cells nearest first and admits the first one native reports legal with a
    /// one-cell footprint on the wall cell; one door is enough to open the room, so the batch is a single
    /// action. A door rotates with its wall, so both orientations are tried.
    /// </summary>
    internal async Task<(IReadOnlyList<Pol.Preview>? Previews, Pol.StockObservation Stock, Verdict Verdict)> PreviewRoutesAsync(GenerationSnapshot snapshot, RimGovernor.Brain.Bridge.Contracts.ColonyReading facts, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
    {
        var stock = new Pol.StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        if (Routes is not { Method: Pol.RoutesMethod.Build } routes)
        {
            throw new ControlException("previewRoutes: r.routes == nil || r.routes.Method != Pol.RoutesBuild");
        }
        var guarded = new HashSet<Cell>(protectedCells);
        foreach (var cell in routes.Breaches)
        {
            if (guarded.Contains(cell))
            {
                continue;
            }
            foreach (var rotation in new[] { Rotation.North, Rotation.East })
            {
                var building = new Building(routes.Definition, cell, rotation, routes.Stuff);
                var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-0"), building, ConstructionTierRules.PlannerTier(Concern, Phase));
                var preview = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
                var p = preview.Preview;
                if (!p.SafeToPlace && DeliberateBreach(p))
                {
                    p = p with { SafeToPlace = true };
                }
                if (!p.MadeFromStuff || p.Footprint.Count != 1 || p.Footprint[0] != cell || !p.CanPlace || !p.SafeToPlace)
                {
                    continue;
                }
                stock = MergeRoundsStock(stock, preview.Stock);
                return ([p], stock, new Verdict());
            }
        }
        return (null, stock, NoSpace("breach_door_site"));
    }
}

partial class RoundsBuildingPlanner
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
        return b.Category == "Building" && b.WouldBeWiped && !b.IsBlueprint && !b.IsFrame && !b.FrameWouldBeCancelled;
    }
}
