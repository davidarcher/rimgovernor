namespace RimGovernor.Brain.BuildingRuntime;

public interface IOverRockPreviewer
{
    Task<(BuildingPreview Preview, BridgeResult Result)> PreviewBuildingOverRockAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct);
}

public sealed partial class RoundsBuildingPlanner
{
    internal const int DigWaves = 64;
    internal const string RoomDigMethodPrefix = "plan-dig-room-";

    internal async Task<(RoundsBuildingResult Result, bool Handled)> AdmitRockStepAsync(ExcavationStep s, IReadOnlyList<RoleCell> planned, Cell access, MethodId method, IReadOnlyList<Building> buildings, Action check, CancellationToken call, CancellationToken epoch)
    {
        var roofs = await RoofRulesForAsync(s.State.Snapshot, planned, call).ConfigureAwait(false);
        var step = PolicyFunctions.RockStepRoofs(planned, s.Facts.Cells, roofs);
        if (step.Unfit.Count > 0)
        {
            return (new RoundsBuildingResult { Verdict = RockNotDug(method.Value, $"{step.Unfit.Count}_cells_thick_or_unseen_roof") }, true);
        }

        return await DigPlannedSkyAsync(s, step.Dig, step.Unroof, access, method, buildings, check, call, epoch).ConfigureAwait(false);
    }

    internal async Task<RoofRules?> RoofRulesForAsync(GenerationSnapshot snapshot, IReadOnlyList<RoleCell> planned, CancellationToken ct)
    {
        if (!planned.Any(c => c.Role == RockRole.NeedsSky))
        {
            return null;
        }

        if (Native is not IDefinitionSource source)
        {
            throw new ControlException("roof rules: the native source serves no definitions");
        }

        var catalog = await source.DefinitionCatalogAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false);
        return (catalog ?? throw new ControlException("roof rules: no definition catalog")).RoofRules();
    }

    internal async Task<(RoundsBuildingResult Result, bool Handled)> DigPlannedAsync(ExcavationStep s, IReadOnlyList<Cell> rock, Cell access, MethodId method, IReadOnlyList<Building> buildings, Action check, CancellationToken call, CancellationToken epoch) =>
        await DigPlannedSkyAsync(s, rock, [], access, method, buildings, check, call, epoch).ConfigureAwait(false);

    internal async Task<(RoundsBuildingResult Result, bool Handled)> DigPlannedSkyAsync(ExcavationStep s, IReadOnlyList<Cell> rock, IReadOnlyList<Cell> unroof, Cell access, MethodId method, IReadOnlyList<Building> buildings, Action check, CancellationToken call, CancellationToken epoch)
    {
        if (rock.Count == 0 && unroof.Count == 0)
        {
            return (new RoundsBuildingResult(), false);
        }

        var dig = Copy();
        if (dig.Excavation is null && rock.Count > 0)
        {
            if (Native is not IRoundsExcavationSource source)
            {
                return (new RoundsBuildingResult(), false);
            }

            dig.Excavation = source;
        }

        var snapshot = s.State.Snapshot with { Revision = new PlanRevision(1) };
        var excavations = new List<Excavation>();
        var designated = false;
        if (rock.Count > 0)
        {
            var site = await dig.ReadExcavationSiteAsync(snapshot, s.Facts.Identity.Tick, "plan-dig", new ExcavationTarget(), rock, access, check, call).ConfigureAwait(false);
            foreach (var cell in site.Cells)
            {
                designated = designated || cell.MineDesignated;
                if (!cell.Eligible || cell.MineDesignated || cell.Definition == "")
                {
                    continue;
                }

                excavations.Add(new Excavation(cell.Cell, cell.Definition));
            }

            if (buildings.Count == 0)
            {
                excavations = [.. ExposedFirst(excavations, s.Facts.Cells)];
            }

            if (excavations.Count == 0)
            {
                if (designated)
                {
                    return (new RoundsBuildingResult { Verdict = BuildingReasonExistingWork }, true);
                }

                return (new RoundsBuildingResult(), false);
            }

            if (site.CollapsePending || site.Support == ExcavationSupport.Unsupported || !site.WorkerAvailable)
            {
                var cause = "dig_no_worker";
                if (site.CollapsePending)
                {
                    cause = "dig_collapse_pending";
                }
                else if (site.Support == ExcavationSupport.Unsupported)
                {
                    cause = "dig_roof_unsupported";
                }

                return (new RoundsBuildingResult { Verdict = NoSpace(cause) }, true);
            }
        }

        var admitted = method;
        for (var wave = 0; ; wave++)
        {
            Method prior;
            try
            {
                prior = Reviewer.Player.Journal.LoadOwnerMethod(s.Owner, admitted, call);
            }
            catch (NotFoundException)
            {
                break;
            }

            var plan0 = Reviewer.Player.Journal.LoadPlan(prior.Plan, call);
            if (Standard.WorkOpen(plan0.Progress))
            {
                return (new RoundsBuildingResult { Verdict = WaitFor(Cause.MethodUsed, "dig_plan") }, true);
            }

            if (excavations.Count == 0)
            {
                return (new RoundsBuildingResult { Verdict = RockNotDug(method.Value, $"roof_standing_{unroof.Count}_cells") }, true);
            }

            if (buildings.Count > 0 || wave + 1 >= DigWaves)
            {
                return (new RoundsBuildingResult { Verdict = RockNotDug(method.Value, $"{excavations.Count}_cells_standing") }, true);
            }

            admitted = new MethodId($"{method}~{wave + 1}");
        }

        snapshot = snapshot with { Plan = PlanIds.MintPlanId() };
        var stock = new StockObservation { Snapshot = snapshot, Tick = s.Facts.Identity.Tick };
        var previews = new List<Preview>();
        var actions = new List<DomainAction>();
        var digging = rock.ToHashSet();
        var overRock = Native is IOverRockPreviewer;
        for (var i = 0; i < buildings.Count; i++)
        {
            var building = buildings[i];
            if (!overRock && digging.Contains(building.Cell))
            {
                return (new RoundsBuildingResult { Verdict = FieldUnavailable("over_rock_preview") }, true);
            }

            var (preview, next, reason) = await PreviewPlannedBuildingAsync(snapshot, s.Facts, building, i, overRock, !digging.Contains(building.Cell), call).ConfigureAwait(false);
            if (!reason.IsZero)
            {
                return (new RoundsBuildingResult { Verdict = reason }, true);
            }

            stock = MergeRoundsStock(stock, next, i == 0);
            previews.AddRange(preview);
            actions.Add(preview[0].Action);
        }

        var built = actions.ToList();
        var dependencies = new List<ActionDependency>();
        var dug = new List<ActionId>();
        foreach (var excavation in excavations)
        {
            var action = DomainAction.NewExcavationAction(new ActionId($"{snapshot.Plan}-{actions.Count}"), excavation);
            foreach (var building in built)
            {
                dependencies.Add(new ActionDependency(building.Id, action.Id));
            }

            dug.Add(action.Id);
            actions.Add(action);
        }

        if (unroof.Count > 0)
        {
            var roof = new RemoveRoof(unroof);
            var action = DomainAction.NewRemoveRoofAction(new ActionId($"{snapshot.Plan}-{actions.Count}"), roof);
            foreach (var id in dug)
            {
                dependencies.Add(new ActionDependency(action.Id, id));
            }

            foreach (var building in built)
            {
                dependencies.Add(new ActionDependency(building.Id, action.Id));
            }

            actions.Add(action);
        }

        var plan = new PlanSpec(snapshot.Plan, new PlanRevision(1), actions, [.. dependencies]);
        var result = await dig.AdmitExcavationAsync(s, snapshot, admitted, plan, previews, stock, check, call, epoch).ConfigureAwait(false);
        return (result, true);
    }

    internal static bool IsRoomDigMethod(MethodId method) => method.Value.StartsWith(RoomDigMethodPrefix, StringComparison.Ordinal);

    internal async Task<(RoundsBuildingResult Result, bool Handled)> DigPlannedRoomAsync(ExcavationStep s, LayoutPlan plan, PlannedRoom room, Action check, CancellationToken call, CancellationToken epoch)
    {
        RoomFootprint shell;
        try
        {
            shell = room.Footprint();
        }
        catch (DomainException)
        {
            return (new RoundsBuildingResult(), false);
        }

        return await DigPlannedAsync(s, plan.RoomRock(room, s.Facts.Cells).Dig, shell.Threshold, BuildingRuntimePackage.DigMethod("room", room), [], check, call, epoch).ConfigureAwait(false);
    }

    internal async Task<(RoundsBuildingResult Result, bool Handled)> DigExhaustAsync(ExcavationStep s, Action check, CancellationToken call, CancellationToken epoch)
    {
        if (!s.Facts.LayoutPlan.TryGetValue(out var plan) || !s.Facts.Rooms.TryGetValue(out var rooms) || !s.Facts.TechTier.TryGetValue(out var tier) || tier < TechTier.Masonry)
        {
            return (new RoundsBuildingResult(), false);
        }

        foreach (var room in plan.Rooms)
        {
            if (plan.CoolerExhaust(room) is not { } exhaust)
            {
                continue;
            }

            var site = exhaust.Site;

            var (standing, found) = PolicyFunctions.CensusRoomIn(room, rooms);
            if (!found || standing is null || standing.Id != Refrigeration?.Room)
            {
                continue;
            }

            var cooler = new RefrigerationCooler { Position = site.Cell, Rotation = site.Rotation };
            var (step, _) = plan.ExhaustRock(room, s.Facts.Cells);
            var rock = step.Dig;
            var place = new List<Building>();
            if (rock.Contains(site.Cell))
            {
                if (Native is not IOverRockPreviewer)
                {
                    return (new RoundsBuildingResult(), false);
                }

                place.Add(new Building("Cooler", site.Cell, site.Rotation, ""));
            }

            return await DigPlannedAsync(s, rock, cooler.Cold(), BuildingRuntimePackage.DigMethod("exhaust", room), place, check, call, epoch).ConfigureAwait(false);
        }

        return (new RoundsBuildingResult(), false);
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>Prefers excavations that touch an open walkable cell, so the digger can reach them; all of them when none do.</summary>
    internal static IReadOnlyList<Excavation> ExposedFirst(IReadOnlyList<Excavation> excavations, IReadOnlyList<SiteCell> cells)
    {
        var open = new HashSet<Cell>();
        foreach (var c in cells)
        {
            if (c.Walkable.TryGetValue(out var walkable) && walkable)
            {
                open.Add(c.Cell);
            }
        }

        var output = new List<Excavation>();
        foreach (var e in excavations)
        {
            var c = e.Cell;
            if (open.Contains(new Cell(c.X + 1, c.Z)) || open.Contains(new Cell(c.X - 1, c.Z)) || open.Contains(new Cell(c.X, c.Z + 1)) || open.Contains(new Cell(c.X, c.Z - 1)))
            {
                output.Add(e);
            }
        }

        return output.Count == 0 ? excavations : output;
    }

    internal static MethodId DigMethod(string what, PlannedRoom room) =>
        new($"plan-dig-{what}-{room.Role}-{room.Region.Bounds().X}-{room.Region.Bounds().Z}");
}
