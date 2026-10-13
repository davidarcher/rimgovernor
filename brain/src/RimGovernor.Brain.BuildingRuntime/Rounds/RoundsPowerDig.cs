namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>Digs the rock under a geothermal generator's fixed site; not handled when the footprint is already open.</summary>
    internal (RoundsBuildingResult Result, bool Handled) DigGeothermal(CancellationToken call, CancellationToken epoch, ExcavationStep s, IReadOnlyList<Cell> protectedCells, Action check)
    {
        if (Power is null || !Power.FixedSite())
        {
            return (default, false);
        }

        check();
        var building = Building.NewBuilding(Definition, Power.Center, Rotation.North, "");
        var probe = s.State.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };
        var action = DomainAction.NewBuildingAction(new ActionId($"{probe.Plan}-0"), building, PolicyFunctions.PlannerTier(Concern, Phase));
        var (preview, _) = Native.PreviewBuilding(call, action, probe);
        if (!preview.Preview.Footprint.TryGetValue(out var footprint) || footprint.Count == 0)
        {
            return (default, false);
        }

        var planned = new List<RoleCell>(footprint.Count);
        foreach (var c in footprint)
        {
            if (protectedCells.Contains(c))
            {
                return (new RoundsBuildingResult { Verdict = BuildingReasonExistingWork }, true);
            }

            planned.Add(new RoleCell { Cell = c, Role = RockRole.NeedsFloor });
        }

        if (PolicyFunctions.RockStep(planned, s.Facts.Cells).Dig.Count == 0)
        {
            return (default, false);
        }

        var (access, ok) = PolicyFunctions.RockAccess(footprint, s.Facts.Cells);
        if (!ok)
        {
            return (new RoundsBuildingResult { Verdict = RockNotDug(Definition, "no_open_cell_beside_footprint") }, true);
        }

        var method = new MethodId($"plan-dig-geothermal-{Power.Center.X}-{Power.Center.Z}");
        return AdmitRockStep(call, epoch, s, planned, access, method, [], check);
    }

    /// <summary>Clears roofs and rock above a wind turbine or solar site; not handled when no planned site needs it.</summary>
    internal (RoundsBuildingResult Result, bool Handled) DigSky(CancellationToken call, CancellationToken epoch, ExcavationStep s, IReadOnlyList<Cell> protectedCells, Action check)
    {
        if (Power is null || Power.Method != PowerMethod.Generate || (Definition != PolicyFunctions.WindTurbineDefinition && Definition != PolicyFunctions.SolarDefinition))
        {
            return (default, false);
        }

        var planned = s.Facts.LayoutPlan.TryGetValue(out var plan);
        var tk = s.Facts.TechTier.TryGetValue(out var tier);
        if (!planned || !tk || tier < TechTier.Masonry)
        {
            return (default, false);
        }

        var taken = protectedCells.ToHashSet();
        if (s.Facts.Facts.CurrentConstruction.TryGetValue(out var census) && census.Colony)
        {
            foreach (var b in census.Buildings)
            {
                foreach (var c in b.Cells)
                {
                    taken.Add(c);
                }
            }
        }

        var roofs = RoofRulesFor(call, s.State.Snapshot, [new RoleCell { Role = RockRole.NeedsSky }]);
        PlannedPowerSite? pick = null;
        List<RoleCell> pickCells = [];
        foreach (var site in PolicyFunctions.PlannedPowerSites(plan!, Definition))
        {
            var footprint = PolicyFunctions.RectangleCells(site.Area);
            var cells = footprint.Select(c => new RoleCell { Cell = c, Role = RockRole.NeedsSky }).ToList();
            var zone = PolicyFunctions.TurbineCatchZone(plan!, site.Area);
            foreach (var lane in zone)
            {
                cells.Add(new RoleCell { Cell = lane, Role = RockRole.NeedsSky });
            }

            var step = PolicyFunctions.RockStepRoofs(cells, s.Facts.Cells, roofs);
            if (footprint.Any(taken.Contains))
            {
                if (step.Unroof.Count > 0)
                {
                    var stalled = RoofStalled(call, s, BuildingRuntimePackage.SkyMethod(Definition, site.Area));
                    if (stalled)
                    {
                        return (new RoundsBuildingResult { Verdict = RockNotDug(Definition, $"roof_standing_{step.Unroof.Count}_cells_after_stall") }, true);
                    }
                }

                continue;
            }

            if (step.Dig.Count == 0 && step.Unroof.Count == 0 && step.Unfit.Count == 0)
            {
                return (default, false);
            }

            if (pick is null && step.Unfit.Count == 0)
            {
                pick = site;
                pickCells = cells;
            }
        }

        if (pick is null)
        {
            return (default, false);
        }

        check();
        var rockCells = pickCells.Select(c => c.Cell).ToList();
        var (access, ok) = PolicyFunctions.RockAccess(rockCells, s.Facts.Cells);
        if (!ok)
        {
            return (new RoundsBuildingResult { Verdict = RockNotDug(Definition, "no_open_cell_beside_site") }, true);
        }

        return AdmitRockStep(call, epoch, s, pickCells, access, BuildingRuntimePackage.SkyMethod(Definition, pick.Area), [], check);
    }

    internal bool RoofStalled(CancellationToken call, ExcavationStep s, MethodId method)
    {
        var journal = Reviewer.Player.Journal;
        IncidentMethodRecord prior;
        try
        {
            prior = journal.LoadOwnerMethod(call, s.Owner, method);
        }
        catch (NotFoundException)
        {
            return false;
        }

        var plan = journal.LoadPlan(call, prior.Plan);
        foreach (var progress in plan.Progress)
        {
            if (!progress.Action.TryRemoveRoof(out _))
            {
                continue;
            }

            var v = progress.View;
            if (v.Stage == ProgressStage.Completed && s.Facts.Identity.Tick.Value - v.Tick.Value >= BuildingRuntimePackage.ExcavationStallTicks)
            {
                return true;
            }
        }

        return false;
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static MethodId SkyMethod(string definition, Rectangle area) => new($"plan-dig-sky-{definition}-{area.X}-{area.Z}");
}
