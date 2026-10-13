namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>The planner resolved to a paste dispenser site when the meal tier is paste; otherwise this planner unchanged.</summary>
    internal (RoundsBuildingPlanner Planner, Verdict Verdict) SelectPaste(ColonyProjection p)
    {
        var seasonal = Reviewer.Seasonal(p.Facts);
        var meals = p.MealRequest(seasonal.FoodMinDays, seasonal.FoodTargetDays);
        MealTierReview review;
        try
        {
            review = PolicyFunctions.ReviewMealTier(meals, p.ProductionBenches);
        }
        catch (DomainException)
        {
            return (this, default);
        }

        if (review.Tier != MealTier.Paste)
        {
            return (this, default);
        }

        if (!p.FoodChannels.TryGetValue(out var channels))
        {
            return (this, FieldUnavailable("food_channels"));
        }

        if (channels.PasteDispenser.Count > 0)
        {
            return (this, BuildingExistingFacility);
        }

        var request = new PasteSiteRequest { Meals = meals, Benches = p.ProductionBenches, Power = p.PowerPlanning };
        foreach (var d in p.Definitions)
        {
            if (d.Name == "Hopper")
            {
                request = request with { Hopper = Known(new Infrastructure { Name = d.Name, Available = d.Available, Costs = d.CheapestCosts() }) };
            }

            if (d.Name == "NutrientPasteDispenser")
            {
                request = request with { Size = d.Size };
            }
        }

        if (!p.Center().TryGetValue(out var anchor))
        {
            return (this, BuildingNoLayoutPlan);
        }

        var (site, ok) = PolicyFunctions.PlanSiteType(new SiteTypeRequest { Paste = request, Field = new FieldRequest { Site = new FarmSiteRequest { Cells = p.Cells, Bounds = p.Bounds, Anchor = anchor } } });
        if (!ok)
        {
            return (this, NoSpace("paste_dispenser_site"));
        }

        var result = Copy();
        result.Paste = site.Buildings;
        result.Definition = "NutrientPasteDispenser";
        return (result, default);
    }

    internal (IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict) PreviewPaste(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection p, IReadOnlyList<Cell> protectedCells, Action check)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = p.Identity.Tick };
        var blocked = protectedCells.ToHashSet();
        var output = new List<Preview>();
        for (var i = 0; i < Paste.Count; i++)
        {
            var site = Paste[i];
            var building = Building.NewBuilding(site.Definition, site.Cell, site.Rotation, "");
            var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{i}"), building, PolicyFunctions.PlannerTier(Concern, Phase));
            var (read, _) = Native.PreviewBuilding(ct, action, snapshot);
            var v = read.Preview;
            var lk = v.CanPlace.TryGetValue(out var legal);
            var sk = v.SafeToPlace.TryGetValue(out var safe);
            var ck = v.Footprint.TryGetValue(out var cells);
            if (!lk || !sk || !ck || !legal || !safe || cells!.Count == 0)
            {
                return (null!, stock, SiteBlocked("paste_dispenser_site", "preview_refused"));
            }

            foreach (var cell in cells)
            {
                if (!blocked.Add(cell))
                {
                    return (null!, stock, SiteBlocked("paste_dispenser_site", "footprints_overlap"));
                }
            }

            MergeRoundsStock(ref stock, read.Stock, true);
            output.Add(v);
        }

        return (output, stock, default);
    }

    internal (RoundsBuildingResult Result, bool Handled) DigPaste(CancellationToken call, CancellationToken epoch, ExcavationStep s, IReadOnlyList<Cell> protectedCells, Action check)
    {
        var planned = new List<RoleCell>();
        var buildings = new List<Building>();
        foreach (var site in Paste)
        {
            if (protectedCells.Contains(site.Cell))
            {
                return (new RoundsBuildingResult { Verdict = BuildingReasonExistingWork }, true);
            }

            buildings.Add(Building.NewBuilding(site.Definition, site.Cell, site.Rotation, ""));
            planned.Add(new RoleCell { Cell = site.Cell, Role = RockRole.NeedsFloor });
        }

        var dig = PolicyFunctions.RockStep(planned, s.Facts.Cells).Dig;
        if (dig.Count == 0)
        {
            return (default, false);
        }

        var (access, ok) = PolicyFunctions.RockAccess(dig, s.Facts.Cells);
        if (!ok)
        {
            return (new RoundsBuildingResult { Verdict = RockNotDug(Definition, "no_open_cell_beside_footprint") }, true);
        }

        var method = new MethodId($"plan-dig-paste-{dig[0].X}-{dig[0].Z}");
        return AdmitRockStep(call, epoch, s, planned, access, method, buildings, check);
    }
}
