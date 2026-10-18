namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Assembles the tier-2 colony model's input (#3523 of #3470) from the review frame: reads of facts the frame already carries,
/// no native call of its own. Nothing runs the model yet; a section the frame did not carry stays an unknown Fact.
/// </summary>
internal static class RoundsColonyModel
{
    /// <summary>The input and the sections it cannot start from, each named with its reason (<see cref="ColonyModelInput.Unknowns"/>).</summary>
    internal static (ColonyModelInput Input, IReadOnlyList<string> Unknowns) InputOf(ColonyProjection colony)
    {
        var facts = colony.Facts;
        var input = new ColonyModelInput
        {
            Climate = ClimateOf(facts.Gear),
            Calendar = facts.Calendar,
            Land = LandOf(colony),
            Crops = CropsOf(colony),
            Research = ResearchOf(facts.Research),
            Stocks = new ModelStocks { Nutrition = NutritionOf(colony.CombinedFoodSupply), Materials = MaterialsOf(colony.Resources), Medicine = MedicineOf(facts.MedicalReserve) },
            Pawns = PawnsOf(colony),
            Recipes = facts.WorkAdmission.Recipes,
            Sites = facts.CurrentConstruction.TryGetValue(out var construction) ? Known(construction.Sites) : Unknown<EquatableArray<ConstructionSite>>(),
            PlannedSites = facts.PlannedSites.TryGetValue(out var planned) ? Known(planned.Sites) : Unknown<EquatableArray<PlannedBuildSite>>(),
        };
        return (input, input.Unknowns());
    }

    /// <summary>The seasonal curve is the map's, so every pawn's gear read carries the same one; the first is the colony's.</summary>
    private static Fact<GearClimate> ClimateOf(Fact<GearObservation> gear)
    {
        if (gear.TryGetValue(out var observation))
        {
            foreach (var pawn in observation.Pawns)
            {
                if (pawn.Climate is { } climate)
                {
                    return Known(climate);
                }
            }
        }

        return Unknown<GearClimate>();
    }

    /// <summary>
    /// Each growing zone's cells and each planned field's cells (the layout plan's field zones off the standing zones), with their
    /// fertility. A cell outside the planning window or without a fertility reading makes the land unknown: a field is not priced
    /// on the cells that happened to be read.
    /// </summary>
    internal static Fact<EquatableArray<ModelField>> LandOf(ColonyProjection colony)
    {
        var cells = new Dictionary<Cell, SiteCell>(colony.Cells.Count);
        foreach (var cell in colony.Cells)
        {
            cells[cell.Cell] = cell;
        }

        var zoned = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var farm in colony.Farms)
        {
            zoned[farm.Id] = [];
        }

        var occupied = new HashSet<Cell>();
        foreach (var cell in colony.Cells)
        {
            if (!cell.Zone.TryGetValue(out var inZone) || !inZone || !cell.ZoneId.TryGetValue(out var id) || !zoned.TryGetValue(id, out var fertility))
            {
                continue;
            }

            if (!cell.Fertility.TryGetValue(out var f))
            {
                return Unknown<EquatableArray<ModelField>>();
            }

            fertility.Add(f);
            occupied.Add(cell.Cell);
        }

        var fields = new List<ModelField>();
        foreach (var farm in colony.Farms)
        {
            fields.Add(new ModelField { Id = farm.Id, Crop = farm.Crop, Fertility = [.. zoned[farm.Id]] });
        }

        if (colony.LayoutPlan.TryGetValue(out var plan))
        {
            var index = 0;
            foreach (var zone in plan.Zones.Where(z => z.Kind == LayoutPlanRules.ZoneField))
            {
                var fertility = new List<double>();
                foreach (var run in zone.Runs)
                {
                    for (var x = run.X; x < run.X + run.Length; x++)
                    {
                        var at = new Cell(x, run.Z);
                        if (occupied.Contains(at))
                        {
                            continue;
                        }

                        if (!cells.TryGetValue(at, out var cell) || !cell.Fertility.TryGetValue(out var f))
                        {
                            return Unknown<EquatableArray<ModelField>>();
                        }

                        fertility.Add(f);
                    }
                }

                if (fertility.Count > 0)
                {
                    fields.Add(new ModelField { Id = $"plan-{index}", Planned = true, Fertility = [.. fertility] });
                }

                index++;
            }
        }

        return Known(EquatableArray.From(fields));
    }

    private static Fact<EquatableArray<CropChoice>> CropsOf(ColonyProjection colony)
    {
        var crops = RoundsFieldPlanner.CropChoices(colony);
        return crops.Count == 0 ? Unknown<EquatableArray<CropChoice>>() : Known(EquatableArray.From(crops));
    }

    private static Fact<ModelResearch> ResearchOf(Fact<ResearchFacts> research)
    {
        if (!research.TryGetValue(out var facts))
        {
            return Unknown<ModelResearch>();
        }

        var finished = facts.Finished.ToHashSet();
        var projects = new List<ModelProject>();
        foreach (var project in facts.Projects)
        {
            if (finished.Contains(project))
            {
                continue;
            }

            facts.Details.TryGetValue(project.Value, out var detail);
            projects.Add(new ModelProject
            {
                Project = project,
                Cost = detail?.Cost ?? 0,
                Progress = detail?.Progress ?? default,
                Prerequisites = detail?.Prerequisites ?? default,
            });
        }

        return Known(new ModelResearch { Current = facts.Current, Finished = facts.Finished, Projects = EquatableArray.From(projects) });
    }

    /// <summary>The nutrition of the food stacks the colony can eat; unknown while the food census is incomplete or a stack's nutrition is.</summary>
    private static Fact<double> NutritionOf(Fact<FoodSupply> supply)
    {
        if (!supply.TryGetValue(out var food) || !food.Complete.TryGetValue(out var complete) || !complete)
        {
            return Unknown<double>();
        }

        var total = 0.0;
        foreach (var stock in food.Stocks)
        {
            if (!stock.Nutrition.TryGetValue(out var nutrition))
            {
                return Unknown<double>();
            }

            total += nutrition;
        }

        return Known(total);
    }

    private static Fact<EquatableArray<Amount>> MaterialsOf(Fact<IReadOnlyDictionary<Resource, long>> resources) =>
        resources.TryGetValue(out var held)
            ? Known(EquatableArray.From(held.OrderBy(p => p.Key).Select(p => new Amount(p.Key, p.Value))))
            : Unknown<EquatableArray<Amount>>();

    /// <summary>The medicine on hand: the unforbidden stacks of the medical reserve census.</summary>
    private static Fact<long> MedicineOf(MedicalReserveObservation reserve) =>
        reserve.Items.TryGetValue(out var items) ? Known(items.Where(i => !i.Forbidden).Sum(i => i.Count)) : Unknown<long>();

    private static Fact<EquatableArray<ModelPawn>> PawnsOf(ColonyProjection colony)
    {
        if (colony.Facts.NeedsAlternatives is not { } review)
        {
            return Unknown<EquatableArray<ModelPawn>>();
        }

        var skills = new Dictionary<string, Fact<EquatableArray<WorkSkill>>>(StringComparer.Ordinal);
        if (colony.WorkPawns.TryGetValue(out var workers))
        {
            foreach (var worker in workers)
            {
                skills[worker.Id.Value] = worker.Skills;
            }
        }

        var pawns = new List<ModelPawn>();
        foreach (var pawn in review.Observation.Pawns)
        {
            var needs = new List<ModelNeed>();
            foreach (var need in pawn.Needs)
            {
                var gain = Unknown<double>();
                foreach (var target in pawn.Targets)
                {
                    if (target.Kind == need.Kind && target.GainPerTick.TryGetValue(out var g) && (!gain.TryGetValue(out var best) || g > best))
                    {
                        gain = Known(g);
                    }
                }

                needs.Add(new ModelNeed { Kind = need.Kind, Fall = need.FallPerTick, Gain = gain, Threshold = need.Threshold });
            }

            pawns.Add(new ModelPawn
            {
                Pawn = pawn.Pawn,
                Needs = EquatableArray.From(needs),
                Skills = skills.GetValueOrDefault(pawn.Pawn),
                Consciousness = pawn.Consciousness,
                Moving = pawn.Moving,
                Manipulation = pawn.Manipulation,
                Sight = pawn.Sight,
                Talking = pawn.Talking,
            });
        }

        return Known(EquatableArray.From(pawns));
    }
}
