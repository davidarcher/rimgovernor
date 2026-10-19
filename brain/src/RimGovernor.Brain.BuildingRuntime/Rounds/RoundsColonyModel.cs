using System.Globalization;
using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The tier-2 run of one review (#3531): the marginals, the input sections it could not start from and the wall time it took. For the
/// unmet goals asked about (#3505): <paramref name="Etas"/> the game day each completes where the run completes it, and <paramref name="GoalNotes"/> one line per goal.
/// </summary>
internal sealed record ColonyModelReview(ProjectMarginals Marginals, IReadOnlyList<string> Unknowns, double HorizonDays, double ElapsedMs, IReadOnlyDictionary<string, int> Etas, IReadOnlyList<string> GoalNotes)
{
    /// <summary>The <see cref="ColonyModelTelemetry.Kind"/> row's attrs.</summary>
    internal IReadOnlyList<(string Key, object? Value)> Attrs()
    {
        var trajectory = Marginals.Trajectory;
        var attrs = new List<(string Key, object? Value)>
        {
            (ColonyModelTelemetry.HorizonKey, HorizonDays),
            (ColonyModelTelemetry.EventsKey, trajectory.Events.Count),
            (ColonyModelTelemetry.TruncatedKey, trajectory.Truncated),
            (ColonyModelTelemetry.CompletionsKey, trajectory.Events.Where(e => e.Kind is ModelEventKind.ProjectCompletion or ModelEventKind.Harvest)
                .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Day:0.##}:{e.Id}")).ToArray()),
            (ColonyModelTelemetry.ProjectsKey, string.Join(';', Marginals.PerHour.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key}={p.Value:0.#####}")))),
            (ColonyModelTelemetry.GoalsKey, GoalNotes.ToArray()),
            (ColonyModelTelemetry.FindingsKey, Marginals.Findings.Concat(Unknowns.Select(u => "input:" + u)).ToArray()),
            (ColonyModelTelemetry.ElapsedKey, ElapsedMs),
        };
        if (trajectory.FoodRunwayDay is { } runway)
        {
            attrs.Add((ColonyModelTelemetry.RunwayKey, runway));
        }

        if (Marginals.Value.TryGetValue(out var value))
        {
            attrs.Add((ColonyModelTelemetry.ValueKey, value));
        }

        if (trajectory.Events.Count > 0)
        {
            attrs.Add((ColonyModelTelemetry.NutritionEndKey, trajectory.Events[^1].Levels[(int)ModelStock.Nutrition]));
        }

        return attrs;
    }
}

/// <summary>
/// Assembles the tier-2 colony model's input (#3523 of #3470) from the review frame: reads of facts the frame already carries,
/// no native call of its own; a section the frame did not carry stays an unknown Fact. <see cref="Review"/> runs it once per
/// review (#3531) and hands the project graph and the pricing to Policy.
/// </summary>
internal static class RoundsColonyModel
{
    /// <summary>
    /// Runs the model over the review's frame and prices it in the coupled context's currency. Once per review, no caching: the
    /// marginal value of each valued project goes to <see cref="ForecastCoupledContext.Investment"/> and the event to telemetry.
    /// </summary>
    internal static ColonyModelReview Review(ColonyProjection colony, ForecastCoupledContext context, GoalsFacts? goals)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (input, unknowns) = InputOf(colony);
        // Each unmet goal with a path through the project graph is a terminal of it (#3505); the rest are named and keep the flat value.
        var paths = (goals?.Goals ?? []).Where(g => GoalRules.Evaluate(g, goals!).State != GoalState.Met).OrderBy(g => g.Id)
            .Select(g => (Goal: GoalRules.NeedOf(g).Value, Path: GoalPaths.Of(g, goals!, input))).ToList();
        var options = colony.Facts.Gear.TryGetValue(out var gear)
            ? gear.Pawns.Where(p => p.LoadoutModel.IsKnown).SelectMany(p => p.LoadoutModel.Value.Options).Where(o => o.Source == GearSource.Bill).DistinctBy(o => o.ID).ToArray()
            : [];
        // The colony's expected raid is the same on every pawn's loadout input; the first known one is the colony's.
        var threat = gear is null ? default : gear.Pawns.Where(p => p.LoadoutModel.IsKnown).Select(p => p.LoadoutModel.Value.Threat).FirstOrDefault(t => t.IsKnown);
        var policy = new ColonyModelPolicy { Projects = paths.Aggregate(ProjectGraph.Standing(input, options, threat), (graph, p) => graph.Then(p.Path.Graph)) };
        var marginals = ProjectMarginalValue.Of(input, policy, new ColonyValue(context, options, policy.HorizonDays), [.. paths.Where(p => p.Path.Terminal is not null).Select(p => p.Path.Terminal!)]);
        var etas = new Dictionary<string, int>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var (goal, path) in paths)
        {
            if (path.Terminal is null)
            {
                notes.Add($"{goal}=unmodelled:{path.Unmodelled}");
            }
            else if (marginals.Goals[goal].CompletionDay is { } day)
            {
                // The game day, counted the way the deadline is (days passed since the game began).
                etas[goal] = (int)((context.Needs.Tick + (long)(day * GameTime.TicksPerDay)) / GameTime.TicksPerDay);
                notes.Add($"{goal}=day:{etas[goal]}");
            }
            else
            {
                notes.Add($"{goal}=open");
            }
        }

        return new(marginals, unknowns, policy.HorizonDays, clock.Elapsed.TotalMilliseconds, etas, notes);
    }

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
            Research = ResearchOf(facts.Research, facts.NeedsAlternatives?.Observation),
            Stocks = new ModelStocks { Nutrition = NutritionOf(colony.CombinedFoodSupply), Materials = MaterialsOf(colony.Resources), Medicine = MedicineOf(facts.MedicalReserve) },
            Pawns = PawnsOf(colony),
            Recipes = facts.WorkAdmission.Recipes,
            Sites = facts.CurrentConstruction.TryGetValue(out var construction) ? Known(construction.Sites) : Unknown<EquatableArray<ConstructionSite>>(),
            PlannedSites = facts.PlannedSites.TryGetValue(out var planned) ? Known(planned.Sites) : Unknown<EquatableArray<PlannedBuildSite>>(),
            Wildlife = colony.Acquisition.TryGetValue(out var sources) ? Known(HuntSquadRules.Herds(sources)) : Unknown<EquatableArray<ModelHerd>>(),
        };
        return (input, input.Unknowns());
    }

    /// <summary>The seasonal curve is the map's, so every pawn's gear read carries the same one; the first is the colony's.</summary>
    internal static Fact<GearClimate> ClimateOf(Fact<GearObservation> gear)
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

    private static Fact<ModelResearch> ResearchOf(Fact<ResearchFacts> research, NeedsForecastRead? needs)
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

        return Known(new ModelResearch { Current = facts.Current, Finished = facts.Finished, Projects = EquatableArray.From(projects), Benches = BenchesOf(needs) });
    }

    /// <summary>The research benches the needs read counts, whatever the current project is; unknown without the read.</summary>
    private static Fact<int> BenchesOf(NeedsForecastRead? read) => read is null ? Unknown<int>() : Known(read.ResearchBenchFactors.Count);

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
        var hunters = new Dictionary<string, Fact<bool>>(StringComparer.Ordinal);
        if (colony.WorkPawns.TryGetValue(out var workers))
        {
            foreach (var worker in workers)
            {
                skills[worker.Id.Value] = worker.Skills;
            }

            // The hunt planner's armed hunter: a hunting weapon and the Hunting work; an unread weapon leaves it unknown.
            var unread = workers.Where(w => !w.Hunts.IsKnown).Select(w => w.Id).ToHashSet();
            foreach (var profile in PawnRules.Profiles(workers))
            {
                hunters[profile.Id.Value] = unread.Contains(profile.Id) ? Unknown<bool>() : Known(HuntSquadRules.ArmedHunter(profile));
            }
        }

        // The best bench is the one the researcher takes, so the bench factor joins the pawn's stat here, as the points a tick at speed 1 does in the model.
        var bestBench = review.Observation.ResearchBenchFactors.Count == 0 ? Unknown<double>() : Known(review.Observation.ResearchBenchFactors.Max());
        var pawns = new List<ModelPawn>();
        foreach (var pawn in review.Observation.Pawns)
        {
            var needs = new List<ModelNeed>();
            foreach (var need in pawn.Needs)
            {
                // A pawn with no usable service still recovers where it stands when the need has such a recovery (rest on the ground).
                var gain = need.UnservedGainPerTick;
                var perUse = Unknown<double>();
                var service = Unknown<double>();
                var approach = new List<double>();
                foreach (var target in pawn.Targets)
                {
                    if (target.Kind != need.Kind)
                    {
                        continue;
                    }

                    if (target.GainPerTick.TryGetValue(out var g) && (!gain.TryGetValue(out var best) || g > best))
                    {
                        gain = Known(g);
                    }

                    if (target.GainPerUnit.TryGetValue(out var unit) && target.ServiceTicks.TryGetValue(out var ticks) && (!perUse.TryGetValue(out var bestUnit) || unit > bestUnit))
                    {
                        perUse = Known(unit);
                        service = Known(ticks);
                    }

                    if (target.TravelTicks.TryGetValue(out var travel))
                    {
                        approach.Add(travel);
                    }
                }

                needs.Add(new ModelNeed
                {
                    Kind = need.Kind,
                    Fall = need.FallPerTick,
                    Gain = gain,
                    Threshold = need.Threshold,
                    GainPerUse = perUse,
                    ServiceTicks = service,
                    ApproachTicks = approach.Count == 0 ? Unknown<double>() : Known(approach.Average()),
                });
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
                ComfortableMin = pawn.ComfortableMin,
                ComfortableMax = pawn.ComfortableMax,
                ResearchSpeed = pawn.ResearchSpeed.TryGetValue(out var researching) && bestBench.TryGetValue(out var factor) ? Known(researching * factor) : Unknown<double>(),
                PlantWorkSpeed = pawn.PlantWorkSpeed,
                Hunts = hunters.GetValueOrDefault(pawn.Pawn),
            });
        }

        return Known(EquatableArray.From(pawns));
    }
}
