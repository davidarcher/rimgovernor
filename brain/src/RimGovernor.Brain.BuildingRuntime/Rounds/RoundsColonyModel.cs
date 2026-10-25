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
    /// <summary>What the review ran from; null when the frame left the input unready (<see cref="Unknowns"/> names why).</summary>
    internal ForecastModelBasis? Basis { get; init; }
    /// <summary>The tending a wound gets over the horizon (<see cref="ColonyValue.TendingOverHorizon"/>, #3804, #3868); null when unread.</summary>
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
        if (trajectory.FoodRunwayDay is ModelEventTime.AtDay { Day: var runway })
        {
            attrs.Add((ColonyModelTelemetry.RunwayKey, runway));
        }

        if (Unknowns.Count == 0)
        {
            attrs.Add((ColonyModelTelemetry.ValueKey, Marginals.Value));
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
/// no native call of its own; a frame that lacks a section yields no input (<see cref="ColonyModelInputRead.Unready"/> names each gap). <see cref="Review"/> runs it once per
/// review (#3531) and hands the project graph and the pricing to Policy.
/// </summary>
internal static class RoundsColonyModel
{
    /// <summary>The finding of a review whose frame left the colony model without an input.</summary>
    private const string UnreadyFinding = "colony_model_input_unready";

    /// <summary>
    /// Runs the model over the review's frame and prices it in the coupled context's currency. Once per review, no caching: the
    /// marginal value of each valued project goes to <see cref="ForecastCoupledContext.Investment"/> and the event to telemetry.
    /// </summary>
    internal static ColonyModelReview Review(ColonyProjection colony, ForecastCoupledContext context, GoalsFacts? goals)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var read = InputOf(colony);
        if (read is ColonyModelInputRead.Unready unready)
        {
            return Unready(unready.Gaps, clock);
        }

        var input = ((ColonyModelInputRead.Ready)read).Input;
        // Each unmet goal with a path through the project graph is a terminal of it (#3505); the rest are named and keep the flat value.
        var paths = (goals?.Goals ?? []).Where(g => GoalRules.Evaluate(g, goals!).State != GoalState.Met).OrderBy(g => g.Id)
            .Select(g => (Goal: GoalRules.NeedOf(g).Value, Path: GoalPaths.Of(g, goals!, input))).ToList();
        // The pawns' bill options, and the apparel only unfinished knowledge projects keep from the colony (#3848): a garment node of one waits on them.
        var gear = colony.Facts.Gear;
        var options = gear.Pawns.SelectMany(p => p.Model?.Options ?? []).Where(o => o.Source == GearSource.Bill).Concat(gear.KnowledgeBills).DistinctBy(o => o.ID).ToArray();
        var valuation = new ColonyValue(context, options, ColonyModelPolicy.DefaultHorizonDays, input.Pawns, colony.Facts.MedicalReserve.Catalog);
        // The medicine the colony crafts toward the reserve's stock target (#3868): the best kind a crafter can make.
        var reserve = colony.Facts.MedicalReserve;
        var policy = new ColonyModelPolicy
        {
            Projects = paths.Aggregate(ProjectGraph.Standing(input, options, gear.Threat), (graph, p) => graph.Then(p.Path.Graph)),
            MedicineTendsPerDay = valuation.TreatmentsPerDay(input),
            MedicineCraft = context.Stock.MedicineCraft([.. reserve.Catalog.MedicineTiers().Reverse()], colony.Facts.Colonists * MedicalReservePolicy.Default().TargetPerColonist),
            MedicineTrade = context.Stock.MedicineTrade,
        };
        var marginals = ProjectMarginalValue.Of(input, policy, valuation, [.. paths.Where(p => p.Path.Terminal is not null).Select(p => p.Path.Terminal!)]);
        var etas = new Dictionary<string, int>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var (goal, path) in paths)
        {
            if (path.Terminal is null)
            {
                notes.Add($"{goal}=unmodelled:{path.Unmodelled}");
            }
            else if (marginals.Goals[goal].CompletionDay is ModelEventTime.AtDay { Day: var day })
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

        return new(marginals, [], policy.HorizonDays, clock.Elapsed.TotalMilliseconds, etas, notes,
            valuation.ClothingStuff(marginals.Trajectory, ClothingDemandRules.ClothingHorizonDays))
        { Basis = new(input, policy), Tending = valuation.TendingOverHorizon(marginals.Trajectory, new SortedSet<string>(StringComparer.Ordinal)) };
    }

    /// <summary>The review of a frame that left the input unready: no run, no basis, and the gaps named.</summary>
    private static ColonyModelReview Unready(IReadOnlyList<string> gaps, System.Diagnostics.Stopwatch clock) =>
        new(new ProjectMarginals { Findings = [UnreadyFinding] }, gaps, ColonyModelPolicy.DefaultHorizonDays,
            clock.Elapsed.TotalMilliseconds, new Dictionary<string, int>(), [], new Dictionary<(PawnId Pawn, GearSlot Slot), Resource>());

    /// <summary>
    /// The input when the frame carries every section it needs and each value is inside its range (<see cref="ColonyModelInput.Gaps"/>);
    /// otherwise the sections it cannot start from, each named with its reason.
    /// </summary>
    internal static ColonyModelInputRead InputOf(ColonyProjection colony)
    {
        var facts = colony.Facts;
        var gaps = new List<string>();
        var climate = facts.Gear.Climate;

        var calendar = facts.Calendar;

        var recipes = facts.WorkAdmission.Recipes;

        var wildlife = HuntSquadRules.Herds(colony.Acquisition);

        var otherDemand = ForecastStockScope.OtherDemand(colony.CombinedFoodSupply, facts.NeedsAlternatives?.Observation.Pawns.Select(p => p.Pawn) ?? []);

        var land = LandOf(colony, gaps);
        var crops = CropsOf(colony, gaps);
        var research = ResearchOf(facts.Research, facts.NeedsAlternatives?.Observation, gaps);
        var stocks = StocksOf(colony);
        var pawns = PawnsOf(colony, gaps);
        if (gaps.Count > 0)
        {
            return new ColonyModelInputRead.Unready(gaps);
        }

        var input = new ColonyModelInput
        {
            OtherNutritionPerDay = otherDemand,
            Climate = climate,
            Calendar = calendar,
            Land = land,
            Crops = crops,
            Research = research!,
            Stocks = stocks!,
            Pawns = pawns,
            Recipes = recipes,
            Wildlife = wildlife,
        };
        return input.Gaps() is { Count: > 0 } outOfRange ? new ColonyModelInputRead.Unready(outOfRange) : new ColonyModelInputRead.Ready(input);
    }

    /// <summary>
    /// Each growing zone's cells and each planned field's cells (the layout plan's field zones off the standing zones), with their
    /// fertility. A cell outside the planning window or without a fertility reading makes the land unread: a field is not priced
    /// on the cells that happened to be read.
    /// </summary>
    internal static EquatableArray<ModelField> LandOf(ColonyProjection colony, List<string> gaps)
    {
        const string Unread = "land: a cell of a growing zone or planned field has no fertility reading";
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
            if (cell.ZoneId is not { } id || !zoned.TryGetValue(id, out var fertility))
            {
                continue;
            }

            fertility.Add(cell.Fertility);
            occupied.Add(cell.Cell);
        }

        var fields = new List<ModelField>();
        foreach (var farm in colony.Farms)
        {
            fields.Add(new ModelField { Id = farm.Id, Crop = farm.Crop, Fertility = [.. zoned[farm.Id]],
                Cells = [.. colony.Cells.Where(c => c.ZoneId == farm.Id).Select(c => c.Cell)] });
        }

        if (colony.LayoutPlan is { } plan)
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

                        if (!cells.TryGetValue(at, out var cell))
                        {
                            gaps.Add(Unread);
                            return default;
                        }

                        fertility.Add(cell.Fertility);
                    }
                }

                if (fertility.Count > 0)
                {
                    fields.Add(new ModelField { Id = $"plan-{index}", Planned = true, Fertility = [.. fertility] });
                }

                index++;
            }
        }

        return EquatableArray.From(fields);
    }

    private static EquatableArray<CropChoice> CropsOf(ColonyProjection colony, List<string> gaps)
    {
        var crops = RoundsFieldPlanner.CropChoices(colony);
        if (crops.Count == 0)
        {
            gaps.Add("crops: the catalog lists no sowable crop");
            return default;
        }

        return EquatableArray.From(crops);
    }

    private static ModelResearch? ResearchOf(ResearchFacts? facts, NeedsForecastRead? needs, List<string> gaps)
    {
        if (facts is null)
        {
            gaps.Add("research: the review frame carries no research census");
            return null;
        }

        if (needs is null)
        {
            gaps.Add("research: the review frame carries no research bench read");
            return null;
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
        return new ModelResearch { Current = facts.Current, Finished = facts.Finished, Projects = EquatableArray.From(projects), Benches = needs.ResearchBenches.Count, Knowledge = EquatableArray.From(knowledge) };
    }

    /// <summary>The colony's nutrition, materials and medicine from the required frame censuses.</summary>
    private static ModelStocks StocksOf(ColonyProjection colony)
    {
        var nutrition = ForecastStockScope.Nutrition(colony.CombinedFoodSupply);
        var materials = default(EquatableArray<Amount>);
        var medicine = default(EquatableArray<Amount>);

        materials = EquatableArray.From(colony.Resources.OrderBy(p => p.Key).Select(p => new Amount(p.Key, p.Value)));

        medicine = MedicineOf(colony.Facts.MedicalReserve, colony.Facts.ResourceThings);

        return new ModelStocks { Nutrition = nutrition, Materials = materials, Medicine = medicine };
    }

    /// <summary>
    /// The medicine on hand by kind: the unforbidden stacks of the medical reserve census, highest potency first (the catalog's
    /// potency order, a kind it does not rank last), then by name.
    /// </summary>
    private static EquatableArray<Amount> MedicineOf(MedicalReserveObservation reserve, EquatableArray<string> identities)
    {
        var items = reserve.Items;
        var potency = reserve.Catalog.MedicalPotency;
        return EquatableArray.From(items.Where(i => !i.Forbidden && identities.Contains(i.Id)).GroupBy(i => i.Definition)
            .Select(g => new Amount(g.Key, g.Sum(i => i.Count)))
            .OrderByDescending(a => potency.TryGetValue(a.Resource, out var p) ? p : double.NegativeInfinity).ThenBy(a => a.Resource));
    }

    private static EquatableArray<ModelPawn> PawnsOf(ColonyProjection colony, List<string> gaps)
    {
        if (colony.Facts.NeedsAlternatives is not { } review)
        {
            gaps.Add("pawns: the review frame carries no needs read");
            return default;
        }

        var skills = new Dictionary<string, EquatableArray<WorkSkill>>(StringComparer.Ordinal);
        var hunters = new Dictionary<string, bool>(StringComparer.Ordinal);
        var curves = new Dictionary<string, EquatableArray<SkillStatCurve>>(StringComparer.Ordinal);
        var workers = colony.WorkPawns.Pawns;
        {
            foreach (var worker in workers)
            {
                skills[worker.Id.Value] = worker.Skills;
                curves[worker.Id.Value] = worker.SkillStatCurves;
            }

            // The hunt planner's armed hunter: a hunting weapon and the Hunting work.
            foreach (var profile in PawnRules.Profiles(workers))
            {
                hunters[profile.Id.Value] = HuntSquadRules.ArmedHunter(profile);
            }
        }

        // The best bench is the one the researcher takes, so the bench factor joins the pawn's stat here, as the points a tick at speed 1 does in the model.
        // A colony with no bench has no research speed; its bench count of 0 keeps anyone from researching.
        var bestBench = review.Observation.ResearchBenches.Count == 0 ? 0 : review.Observation.ResearchBenches.Max(b => b.Factor);
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
                    // The meal came from a target of the kind, which also gave an approach.
                    Service = meal is null ? new ModelService.NoneObserved() : new ModelService.Observed(meal.GainPerUnit, meal.ServiceTicks, approach.Average()),
                });
            }

            if (!skills.TryGetValue(pawn.Pawn, out var workerSkills) || !hunters.TryGetValue(pawn.Pawn, out var hunts))
            {
                gaps.Add($"pawns: '{pawn.Pawn}' lacks skills or the hunt capability (the work census carries no row for it)");
                continue;
            }

            pawns.Add(new ModelPawn
            {
                Pawn = pawn.Pawn,
                Needs = EquatableArray.From(needs),
                Skills = workerSkills,
                Consciousness = pawn.Consciousness,
                Moving = pawn.Moving,
                Manipulation = pawn.Manipulation,
                Sight = pawn.Sight,
                Talking = pawn.Talking,
                ComfortableMin = pawn.ComfortableMin,
                ComfortableMax = pawn.ComfortableMax,
                ResearchSpeed = pawn.ResearchSpeed * bestBench,
                PlantWorkSpeed = pawn.PlantWorkSpeed,
                Hunts = hunts,
                SkillStatCurves = curves.GetValueOrDefault(pawn.Pawn),
                WorkSpeeds = pawn.Upkeep.Speeds,
            });
        }

        return EquatableArray.From(pawns);
    }
}
