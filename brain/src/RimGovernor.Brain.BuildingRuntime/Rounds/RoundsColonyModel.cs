using System.Globalization;
using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The tier-2 run of one review (#3531): the marginals, the input sections it could not start from and the wall time it took. For the
/// unmet goals asked about (#3505): <paramref name="Etas"/> the game day each completes where the run completes it, and <paramref name="GoalNotes"/> one line per goal.
/// <paramref name="ClothingStuff"/> is the stuff the run puts on each pawn's slot within the clothing horizon (<see cref="ColonyValue.ClothingStuff"/>, #3623).
/// </summary>
internal sealed record ColonyModelReview(ProjectMarginals Marginals, IReadOnlyList<string> Unknowns, double HorizonDays, double ElapsedMs, IReadOnlyDictionary<string, int> Etas, IReadOnlyList<string> GoalNotes,
    IReadOnlyDictionary<(PawnId Pawn, GearSlot Slot), Resource> ClothingStuff)
{
    internal required ForecastModelBasis Basis { get; init; }
    /// <summary>The tending a wound gets now (<see cref="ColonyValue.TendingNow"/>, #3804); null when unread.</summary>
    internal ForecastTending? Tending { get; init; }
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
            (ColonyModelTelemetry.ArrivalsKey, trajectory.Arrivals.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Day:0.##}:{a.Node}:{a.Resource}={a.Count:0.#}")).ToArray()),
            (ColonyModelTelemetry.ProjectsKey, string.Join(';', Marginals.PerHour.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key}={p.Value:0.#####}")))),
            (ColonyModelTelemetry.SkillsKey, trajectory.Skills.Where(s => Marginals.PerUnit.ContainsKey(s.Key))
                .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Key}={Marginals.PerUnit[s.Key]:G4}")).ToArray()),
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
        // The pawns' bill options, and the apparel only unfinished knowledge projects keep from the colony (#3848): a garment node of one waits on them.
        var options = colony.Facts.Gear.TryGetValue(out var gear)
            ? gear.Pawns.SelectMany(p => p.Model?.Options ?? []).Where(o => o.Source == GearSource.Bill).Concat(gear.KnowledgeBills).DistinctBy(o => o.ID).ToArray()
            : [];
        var policy = new ColonyModelPolicy { Projects = paths.Aggregate(ProjectGraph.Standing(input, options, gear?.Threat), (graph, p) => graph.Then(p.Path.Graph)) };
        var valuation = new ColonyValue(context, options, policy.HorizonDays, input.Pawns, colony.Facts.MedicalReserve.Catalog);
        policy = policy with { MedicineTendsPerDay = valuation.TreatmentsPerDay(input) };
        var marginals = ProjectMarginalValue.Of(input, policy, valuation, [.. paths.Where(p => p.Path.Terminal is not null).Select(p => p.Path.Terminal!)]);
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
                etas[goal] = (int)((context.Needs.Tick.Value + (long)(day * GameTime.TicksPerDay)) / GameTime.TicksPerDay);
                notes.Add($"{goal}=day:{etas[goal]}");
            }
            else
            {
                notes.Add($"{goal}=open");
            }
        }

        return new(marginals, unknowns, policy.HorizonDays, clock.Elapsed.TotalMilliseconds, etas, notes,
            valuation.ClothingStuff(marginals.Trajectory, ClothingDemandRules.ClothingHorizonDays))
        { Basis = new(input, policy), Tending = valuation.TendingNow(marginals.Trajectory, new SortedSet<string>(StringComparer.Ordinal)) };
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
            Stocks = new ModelStocks { Nutrition = NutritionOf(colony.CombinedFoodSupply), Materials = MaterialsOf(colony.Resources), Medicine = MedicineOf(facts.MedicalReserve, facts.ResourceThings) },
            Pawns = PawnsOf(colony),
            OtherNutritionPerDay = ForecastStockScope.OtherDemand(colony.CombinedFoodSupply,
                facts.NeedsAlternatives?.Observation.Pawns.Select(p => p.Pawn) ?? []),
            Recipes = facts.WorkAdmission.Recipes,
            Sites = facts.CurrentConstruction.TryGetValue(out var construction) ? Known(construction.Sites) : Unknown<EquatableArray<ConstructionSite>>(),
            PlannedSites = facts.PlannedSites.TryGetValue(out var planned) ? Known(planned.Sites) : Unknown<EquatableArray<PlannedBuildSite>>(),
            Wildlife = colony.Acquisition.TryGetValue(out var sources) ? Known(HuntSquadRules.Herds(sources)) : Unknown<EquatableArray<ModelHerd>>(),
        };
        return (input, input.Unknowns());
    }

    /// <summary>The map's seasonal curve, as the gear census reads it.</summary>
    internal static Fact<GearClimate> ClimateOf(Fact<GearObservation> gear) =>
        gear.TryGetValue(out var observation) ? Known(observation.Climate) : Unknown<GearClimate>();

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
            fields.Add(new ModelField { Id = farm.Id, Crop = farm.Crop, Fertility = [.. zoned[farm.Id]],
                Cells = [.. colony.Cells.Where(c => c.ZoneId == Known(farm.Id) && c.Zone == Known(true)).Select(c => c.Cell)] });
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
                KnowledgeCategory = detail?.KnowledgeCategory ?? "",
                Unlocks = detail?.Unlocks ?? default,
            });
        }

        // An empty knowledge slot is filled by the research Need with the project it funds first, so that is where the category's points go.
        var done = facts.Finished.Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
        var knowledge = facts.Knowledge.Select(slot => new ModelKnowledge
        {
            Category = slot.Category,
            Project = slot.Current.IsEmpty ? new ResearchProjectId(ResearchRules.KnowledgeFunding(facts.Details, done, slot.Category)) : slot.Current,
            Overflow = slot.Overflow,
        });
        return Known(new ModelResearch { Current = facts.Current, Finished = facts.Finished, Projects = EquatableArray.From(projects), Benches = BenchesOf(needs), Knowledge = EquatableArray.From(knowledge) });
    }

    /// <summary>The research benches the needs read counts, whatever the current project is; unknown without the read.</summary>
    private static Fact<int> BenchesOf(NeedsForecastRead? read) => read is null ? Unknown<int>() : Known(read.ResearchBenches.Count);

    /// <summary>The nutrition of the food stacks the colony can eat; unknown while the food census is incomplete or a stack's nutrition is.</summary>
    private static Fact<double> NutritionOf(Fact<FoodSupply> supply) => supply.TryGetValue(out var food) ? ForecastStockScope.Nutrition(food) : default;

    private static Fact<EquatableArray<Amount>> MaterialsOf(Fact<IReadOnlyDictionary<Resource, long>> resources) =>
        resources.TryGetValue(out var held)
            ? Known(EquatableArray.From(held.OrderBy(p => p.Key).Select(p => new Amount(p.Key, p.Value))))
            : Unknown<EquatableArray<Amount>>();

    /// <summary>
    /// The medicine on hand by kind: the unforbidden stacks of the medical reserve census, highest potency first (the catalog's
    /// potency order, a kind it does not rank last), then by name.
    /// </summary>
    private static Fact<EquatableArray<Amount>> MedicineOf(MedicalReserveObservation reserve, Fact<EquatableArray<string>> resourceThings)
    {
        if (!reserve.Items.TryGetValue(out var items) || !resourceThings.TryGetValue(out var identities))
        {
            return Unknown<EquatableArray<Amount>>();
        }

        var potency = reserve.Catalog.MedicalPotency;
        return Known(EquatableArray.From(items.Where(i => !i.Forbidden && identities.Contains(i.Id)).GroupBy(i => i.Definition)
            .Select(g => new Amount(g.Key, g.Sum(i => i.Count)))
            .OrderByDescending(a => potency.TryGetValue(a.Resource, out var p) ? p : double.NegativeInfinity).ThenBy(a => a.Resource)));
    }

    private static Fact<EquatableArray<ModelPawn>> PawnsOf(ColonyProjection colony)
    {
        if (colony.Facts.NeedsAlternatives is not { } review)
        {
            return Unknown<EquatableArray<ModelPawn>>();
        }

        var skills = new Dictionary<string, Fact<EquatableArray<WorkSkill>>>(StringComparer.Ordinal);
        var hunters = new Dictionary<string, Fact<bool>>(StringComparer.Ordinal);
        var curves = new Dictionary<string, EquatableArray<SkillStatCurve>>(StringComparer.Ordinal);
        if (colony.WorkPawns.TryGetValue(out var workers))
        {
            foreach (var worker in workers)
            {
                skills[worker.Id.Value] = worker.Skills;
                curves[worker.Id.Value] = worker.SkillStatCurves;
            }

            // The hunt planner's armed hunter: a hunting weapon and the Hunting work; an unread weapon leaves it unknown.
            var unread = workers.Where(w => !w.Hunts.IsKnown).Select(w => w.Id).ToHashSet();
            foreach (var profile in PawnRules.Profiles(workers))
            {
                hunters[profile.Id.Value] = unread.Contains(profile.Id) ? Unknown<bool>() : Known(HuntSquadRules.ArmedHunter(profile));
            }
        }

        // The best bench is the one the researcher takes, so the bench factor joins the pawn's stat here, as the points a tick at speed 1 does in the model.
        var bestBench = review.Observation.ResearchBenches.Count == 0 ? (double?)null : review.Observation.ResearchBenches.Max(b => b.Factor);
        var pawns = new List<ModelPawn>();
        foreach (var pawn in review.Observation.Pawns)
        {
            var needs = new List<ModelNeed>();
            foreach (var need in pawn.Needs)
            {
                // A pawn with no usable service still recovers where it stands when the need has such a recovery (rest on the ground).
                var gain = need.UnservedGainPerTick;
                NeedRecovery.Ingest? meal = null;
                var approach = new List<double>();
                // Joy's recovery without a building is a walk, taken only while the weather allows it (#3825 D7): the read says when.
                if (need.Kind == "joy" && need.OutdoorShareByTwelfth.Count != 12)
                {
                    throw new RequiredFactException("outdoor recreation share of " + pawn.Pawn);
                }

                var sheltered = 0d;
                foreach (var target in pawn.Targets)
                {
                    if (target.Kind != need.Kind)
                    {
                        continue;
                    }

                    if (target.Recovery is NeedRecovery.Rate { GainPerTick: var g })
                    {
                        sheltered = Math.Max(sheltered, g);
                        gain = Math.Max(gain, g);
                    }

                    if (target.Recovery is NeedRecovery.Ingest ingest && (meal is null || ingest.GainPerUnit > meal.GainPerUnit))
                    {
                        meal = ingest;
                    }

                    approach.Add(target.TravelTicks);
                }

                needs.Add(new ModelNeed
                {
                    Kind = need.Kind,
                    Fall = need.FallPerTick,
                    Gain = gain,
                    OutdoorShares = need.OutdoorShareByTwelfth,
                    ShelteredGain = sheltered,
                    Threshold = need.Threshold,
                    GainPerUse = meal is null ? Unknown<double>() : Known(meal.GainPerUnit),
                    ServiceTicks = meal is null ? Unknown<double>() : Known(meal.ServiceTicks),
                    ApproachTicks = approach.Count == 0 ? Unknown<double>() : Known(approach.Average()),
                });
            }

            pawns.Add(new ModelPawn
            {
                Pawn = pawn.Pawn,
                Needs = EquatableArray.From(needs),
                Skills = skills.GetValueOrDefault(pawn.Pawn),
                Consciousness = Known(pawn.Consciousness),
                Moving = Known(pawn.Moving),
                Manipulation = Known(pawn.Manipulation),
                Sight = Known(pawn.Sight),
                Talking = Known(pawn.Talking),
                ComfortableMin = Known(pawn.ComfortableMin),
                ComfortableMax = Known(pawn.ComfortableMax),
                ResearchSpeed = bestBench is { } factor ? Known(pawn.ResearchSpeed * factor) : Unknown<double>(),
                PlantWorkSpeed = Known(pawn.PlantWorkSpeed),
                Hunts = hunters.GetValueOrDefault(pawn.Pawn),
                SkillStatCurves = curves.GetValueOrDefault(pawn.Pawn),
            });
        }

        return Known(EquatableArray.From(pawns));
    }
}
