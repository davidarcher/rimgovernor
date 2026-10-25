using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    /// <summary>The planner resolved to a paste dispenser site when the meal tier is paste; otherwise this planner unchanged.</summary>
    internal (RoundsBuildingPlanner Planner, Verdict Verdict) SelectPaste(ColonyReading p)
    {
        var seasonal = Reviewer.Seasonal(p.Facts);
        var meals = ColonyPlanningRules.MealRequest(p.Facts, p.Snapshot.ControlledEnvironment, seasonal.FoodMinDays, seasonal.FoodTargetDays);
        MealTierReview review;
        try
        {
            review = MealRules.ReviewMealTier(meals, p.Facts.EssentialInputs.Benches);
        }
        catch (DomainException)
        {
            return (this, default);
        }

        if (review.Tier != MealTier.Paste)
        {
            return (this, default);
        }

        if (p.Facts.FoodChannels.PasteDispenser.Count > 0)
        {
            return (this, BuildingReasonExistingFacility);
        }

        var request = new PasteSiteRequest { Meals = meals, Benches = p.Facts.EssentialInputs.Benches, Power = p.Facts.EssentialInputs.Power };
        foreach (var d in p.Facts.Definitions)
        {
            if (d.Name == "Hopper" && d.Available)
            {
                request = request with { Hopper = new Infrastructure { Name = d.Name, Available = d.Available, Costs = d.CheapestCosts() } };
            }

            if (d.Name == "NutrientPasteDispenser")
            {
                request = request with { Size = d.Size };
            }
        }

        if (p.Facts.LayoutPlan?.Center() is not { } anchor)
        {
            return (this, BuildingReasonNoLayoutPlan);
        }

        var (site, ok) = SiteRules.PlanSiteType(new SiteTypeRequest { Environment = p.Snapshot.ControlledEnvironment, Paste = request, Field = new FieldRequest { Site = new FarmSiteRequest { Cells = p.PlanningWindow.Cells, Anchor = anchor } } });
        if (!ok)
        {
            return (this, NoSpace("paste_dispenser_site"));
        }

        var result = Copy();
        result.Paste = site.Buildings;
        result.Definition = "NutrientPasteDispenser";
        return (result, default);
    }

    internal async Task<(IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict)> PreviewPasteAsync(GenerationSnapshot snapshot, ColonyReading p, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken ct)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = p.Identity.Tick };
        var blocked = protectedCells.ToHashSet();
        var output = new List<Preview>();
        for (var i = 0; i < Paste.Count; i++)
        {
            var site = Paste[i];
            var building = new Building(site.Definition, site.Cell, site.Rotation, "");
            var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{i}"), building, ConstructionTierRules.PlannerTier(Concern, Phase));
            var read = await Native.PreviewBuildingAsync(action, snapshot, ct).ConfigureAwait(false);
            var v = read.Preview;
            if (!v.CanPlace || !v.SafeToPlace || v.Footprint.Count == 0)
            {
                return ([], stock, SiteBlocked("paste_dispenser_site", "preview_refused"));
            }

            foreach (var cell in v.Footprint)
            {
                if (!blocked.Add(cell))
                {
                    return ([], stock, SiteBlocked("paste_dispenser_site", "footprints_overlap"));
                }
            }

            stock = MergeRoundsStock(stock, read.Stock);
            output.Add(v);
        }

        return (output, stock, default);
    }

    internal async Task<(RoundsBuildingResult Result, bool Handled)> DigPasteAsync(ExcavationStep s, IReadOnlyList<Cell> protectedCells, Action check, CancellationToken call, CancellationToken epoch)
    {
        var planned = new List<RoleCell>();
        var buildings = new List<Building>();
        foreach (var site in Paste)
        {
            if (protectedCells.Contains(site.Cell))
            {
                return (new RoundsBuildingResult { Verdict = BuildingReasonExistingWork }, true);
            }

            buildings.Add(new Building(site.Definition, site.Cell, site.Rotation, ""));
            planned.Add(new RoleCell { Cell = site.Cell, Role = RockRole.NeedsFloor });
        }

        var dig = RockStepRules.RockStep(planned, s.Facts.PlanningWindow.Cells).Dig;
        if (dig.Count == 0)
        {
            return (new RoundsBuildingResult(), false);
        }

        var (access, ok) = RockStepRules.RockAccess(dig, s.Facts.PlanningWindow.Cells);
        if (!ok)
        {
            return (new RoundsBuildingResult { Verdict = RockNotDug(Definition, "no_open_cell_beside_footprint") }, true);
        }

        var method = new MethodId($"plan-dig-paste-{dig[0].X}-{dig[0].Z}");
        return await AdmitRockStepAsync(s, planned, access, method, buildings, check, call, epoch).ConfigureAwait(false);
    }
}
