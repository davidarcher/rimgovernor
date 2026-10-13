namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>Digs the rock under a geothermal generator's fixed site; not handled when the footprint is already open.</summary>
    internal async Task<(RoundsBuildingResult Result, bool Handled)> DigGeothermalAsync(ExcavationStep s, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken call, CancellationToken epoch)
    {
        if (Power is null || !Power.FixedSite())
        {
            return (new RoundsBuildingResult(), false);
        }

        check();
        var building = new Building(Definition, Power.Center, Rotation.North, "");
        var probe = s.State.Snapshot with { Plan = PlanIds.MintPlanId(), Revision = new PlanRevision(1) };
        var action = DomainAction.NewBuildingAction(new ActionId($"{probe.Plan}-0"), building, PolicyFunctions.PlannerTier(Concern, Phase));
        var (preview, _) = await Native.PreviewBuildingAsync(action, probe, call).ConfigureAwait(false);
        if (!preview.Preview.Footprint.TryGetValue(out var footprint) || footprint.Count == 0)
        {
            return (new RoundsBuildingResult(), false);
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
            return (new RoundsBuildingResult(), false);
        }

        var (access, ok) = PolicyFunctions.RockAccess(footprint, s.Facts.Cells);
        if (!ok)
        {
            return (new RoundsBuildingResult { Verdict = RockNotDug(Definition, "no_open_cell_beside_footprint") }, true);
        }

        var method = new MethodId($"plan-dig-geothermal-{Power.Center.X}-{Power.Center.Z}");
        return await AdmitRockStepAsync(s, planned, access, method, [], check, call, epoch).ConfigureAwait(false);
    }

    /// <summary>Clears roofs and rock above a wind turbine or solar site; not handled when no planned site needs it.</summary>
    internal async Task<(RoundsBuildingResult Result, bool Handled)> DigSkyAsync(ExcavationStep s, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken call, CancellationToken epoch)
    {
        if (Power is null || Power.Method != PowerMethod.Generate || (Definition != PolicyConstants.WindTurbineDefinition && Definition != PolicyFunctions.SolarDefinition))
        {
            return (new RoundsBuildingResult(), false);
        }

        var planned = s.Facts.LayoutPlan.TryGetValue(out var plan);
        var tk = s.Facts.TechTier.TryGetValue(out var tier);
        if (!planned || !tk || tier < TechTier.Masonry)
        {
            return (new RoundsBuildingResult(), false);
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

        var roofs = await RoofRulesForAsync(s.State.Snapshot, [new RoleCell { Role = RockRole.NeedsSky }], call).ConfigureAwait(false);
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
                    var stalled = RoofStalled(s, BuildingRuntimePackage.SkyMethod(Definition, site.Area), call);
                    if (stalled)
                    {
                        return (new RoundsBuildingResult { Verdict = RockNotDug(Definition, $"roof_standing_{step.Unroof.Count}_cells_after_stall") }, true);
                    }
                }

                continue;
            }

            if (step.Dig.Count == 0 && step.Unroof.Count == 0 && step.Unfit.Count == 0)
            {
                return (new RoundsBuildingResult(), false);
            }

            if (pick is null && step.Unfit.Count == 0)
            {
                pick = site;
                pickCells = cells;
            }
        }

        if (pick is null)
        {
            return (new RoundsBuildingResult(), false);
        }

        check();
        var rockCells = pickCells.Select(c => c.Cell).ToList();
        var (access, ok) = PolicyFunctions.RockAccess(rockCells, s.Facts.Cells);
        if (!ok)
        {
            return (new RoundsBuildingResult { Verdict = RockNotDug(Definition, "no_open_cell_beside_site") }, true);
        }

        return await AdmitRockStepAsync(s, pickCells, access, BuildingRuntimePackage.SkyMethod(Definition, pick.Area), [], check, call, epoch).ConfigureAwait(false);
    }

    internal bool RoofStalled(ExcavationStep s, MethodId method, CancellationToken call)
    {
        var journal = Reviewer.Player.Journal;
        Method prior;
        try
        {
            prior = journal.LoadOwnerMethod(s.Owner, method, call);
        }
        catch (NotFoundException)
        {
            return false;
        }

        var plan = journal.LoadPlan(prior.Plan, call);
        foreach (var progress in plan.Progress)
        {
            if (progress.Action.RemoveRoof is null)
            {
                continue;
            }

            var v = progress.View;
            if (v.Stage == Stage.Completed && s.Facts.Identity.Tick.Value - v.Tick.Value >= ExcavationStallTicks)
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
