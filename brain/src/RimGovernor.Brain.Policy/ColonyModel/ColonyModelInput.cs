namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's input (#3523 of #3470): one record per review, collected from facts the review frame already carries.
// The pawn cycle, engine, graph and valuation read it (RoundsColonyModel.Review runs them once per review, #3531); this is the contract they
// read. Every section is present: a frame that lacks one yields no input (ColonyModelInputRead.Unready names each gap), and Gaps() names a value outside its range.

/// <summary>A growing zone or planned field: its soil. Fertility lists each cell's fertility, so a poor cell is not averaged away.</summary>
public sealed record ModelField
{
    /// <summary>The native zone id of a standing zone, the plan's zone label of a planned field.</summary>
    public string Id { get; init; } = "";

    /// <summary>The crop the zone grows now; empty for a zone with nothing sown and for a planned field, whose crop is not chosen.</summary>
    public string Crop { get; init; } = "";

    /// <summary>True for a field only the layout plan holds; no zone stands on it yet.</summary>
    public bool Planned { get; init; }

    public EquatableArray<double> Fertility { get; init; }
    /// <summary>Native cells in the same order as fertility, used to join a detailed harvest to its field.</summary>
    public EquatableArray<Cell> Cells { get; init; }
}

/// <summary>One unfinished research project the model may spend points on.</summary>
public sealed record ModelProject
{
    public ResearchProjectId Project { get; init; }

    /// <summary>The project's cost in research points (the catalog's cost).</summary>
    public double Cost { get; init; }

    /// <summary>The researched fraction of the cost (0 to 1).</summary>
    public double Progress { get; init; }

    public EquatableArray<ResearchProjectId> Prerequisites { get; init; }

    /// <summary>The project's knowledge category (Anomaly, ResearchProjectDef.knowledgeCategory); empty for a project researched at a bench. A knowledge project's cost is its knowledgeCost in knowledge points, which only study gives.</summary>
    public string KnowledgeCategory { get; init; } = "";

    /// <summary>The defs finishing the project unlocks (<see cref="ResearchProjectFacts.Unlocks"/>).</summary>
    public EquatableArray<string> Unlocks { get; init; }
}

/// <summary>
/// One knowledge category (Anomaly) as the model reads it: the project a knowledge point of it funds now, and where the point goes
/// while it funds none (ResearchManager.ApplyKnowledge).
/// </summary>
public sealed record ModelKnowledge
{
    public string Category { get; init; } = "";

    /// <summary>The category slot's project, or for an empty slot the project the research Need funds into it (<see cref="ResearchRules.KnowledgeFunding"/>); empty when there is neither.</summary>
    public ResearchProjectId Project { get; init; }

    /// <summary>KnowledgeCategoryDef.overflowCategory; empty when the category has none and a point it cannot fund is lost.</summary>
    public string Overflow { get; init; } = "";
}

/// <summary>The research state: what is being researched, what is done and what is left to do.</summary>
public sealed record ModelResearch
{
    public ResearchProjectId Current { get; init; }

    public EquatableArray<ResearchProjectId> Finished { get; init; }

    /// <summary>Every listed project not finished.</summary>
    public EquatableArray<ModelProject> Projects { get; init; }

    /// <summary>
    /// The research benches the current project can be researched at, one researcher each; 0 for a colony with none. A pawn's
    /// <see cref="ModelPawn.ResearchSpeed"/> already carries the factor of its bench, so only the count limits the labor.
    /// </summary>
    public required int Benches { get; init; }

    /// <summary>Every knowledge category, sorted by category; empty without Anomaly.</summary>
    public EquatableArray<ModelKnowledge> Knowledge { get; init; }
}

/// <summary>The colony's stocks the model moves: nutrition, materials and medicine. Research points are per project (<see cref="ModelProject.Progress"/>); power is not a stock in v1.</summary>
public sealed record ModelStocks
{
    public required double Nutrition { get; init; }

    public required EquatableArray<Amount> Materials { get; init; }

    /// <summary>The usable medicine by kind, in the order the colony uses it up: highest potency first, as a doctor takes the best medicine (#3634).</summary>
    public required EquatableArray<Amount> Medicine { get; init; }
}

/// <summary>One need's observed mechanics: it falls by Fall per tick, a service gives Gain per tick, and the pawn seeks service under Threshold.</summary>
public sealed record ModelNeed
{
    public string Kind { get; init; } = "";

    public required double Fall { get; init; }

    /// <summary>The best gain per tick among the observed services and the pawn's unserved recovery; 0 while nothing serves it at a rate.</summary>
    public required double Gain { get; init; }

    /// <summary>
    /// For joy, the share of each twelfth in which the outdoor recreation of <see cref="Gain"/> (a walk or skygazing, #3825 D7) can
    /// be taken (<c>NeedRate.OutdoorShareByTwelfth</c>); empty when the gain needs no outdoor weather.
    /// </summary>
    public EquatableArray<double> OutdoorShares { get; init; }

    /// <summary>The best gain per tick of a building that serves the need in any weather (0 with none), the joy the pawn takes outside <see cref="OutdoorShares"/>.</summary>
    public double ShelteredGain { get; init; }

    public required double Threshold { get; init; }

    /// <summary>The best observed service by use (a meal): what one use gives, how long it takes and how far it is; <see cref="ModelService.NoneObserved"/> while no service by use is observed or the service is continuous.</summary>
    public required ModelService Service { get; init; }
}

/// <summary>A need's best service by use (eating a meal), read from the need tasks observed now.</summary>
public abstract record ModelService
{
    private protected ModelService() { }

    /// <summary>One use gives <paramref name="GainPerUse"/> of the need level and takes <paramref name="ServiceTicks"/>; reaching one of the need's targets takes <paramref name="ApproachTicks"/> on average.</summary>
    public sealed record Observed(double GainPerUse, double ServiceTicks, double ApproachTicks) : ModelService;

    /// <summary>No service by use is observed, or the need's service is continuous.</summary>
    public sealed record NoneObserved : ModelService;
}

/// <summary>
/// One race's huntable animals of the wildlife census, as the hunt planner prices them (<see cref="HuntSquadRules.Herds"/>): the
/// pursuit work of hunting them all, the nutrition of their meat and what butchering them yields besides (leather, from the race row).
/// </summary>
public sealed record ModelHerd
{
    public Resource Race { get; init; }

    public int Animals { get; init; }

    /// <summary>The hunt planner's pursuit work ticks, summed over the animals.</summary>
    public double Work { get; init; }

    /// <summary>The meat's nutrition: each animal's MeatAmount stat times the meat def's Nutrition.</summary>
    public double Nutrition { get; init; }

    /// <summary>The race's butchery products (<see cref="AnimalRace.Butchery"/>) times the animals, sorted by def.</summary>
    public EquatableArray<SourceProduct> Products { get; init; }
}

/// <summary>One colonist as the model's pawn cycle reads them.</summary>
public sealed record ModelPawn
{
    public string Pawn { get; init; } = "";

    public EquatableArray<ModelNeed> Needs { get; init; }

    public required EquatableArray<WorkSkill> Skills { get; init; }

    /// <summary>The five capacities a pawn-day is counted in (#3519).</summary>
    public required double Consciousness { get; init; }

    public required double Moving { get; init; }

    public required double Manipulation { get; init; }

    public required double Sight { get; init; }

    public required double Talking { get; init; }

    /// <summary>The comfortable temperature range of the pawn's gear now, the pawn cycle's thermal term is read against it (#3526).</summary>
    public required double ComfortableMin { get; init; }

    public required double ComfortableMax { get; init; }

    /// <summary>The ResearchSpeed stat times the factor of the colony's best research bench; 0 in a colony with no bench, where <see cref="ModelResearch.Benches"/> keeps anyone from researching.</summary>
    public required double ResearchSpeed { get; init; }

    /// <summary>The PlantWorkSpeed stat.</summary>
    public required double PlantWorkSpeed { get; init; }

    /// <summary>Whether the pawn may hunt: a hunting weapon and the Hunting work (<see cref="HuntSquadRules.ArmedHunter"/>).</summary>
    public required bool Hunts { get; init; }

    /// <summary>The pawn's native stat values at each stored skill level (#3633); the practice node reads its surgery speed here.</summary>
    public EquatableArray<RimGovernor.Contracts.Wire.Observations.SkillStatCurve> SkillStatCurves { get; init; }

    /// <summary>The pawn's work speed stats now (PawnUpkeepRead.Speeds, #3868); a craft node reads its recipe's workSpeedStat here.</summary>
    public EquatableArray<RimGovernor.Contracts.Wire.Observations.WorkSpeedRead> WorkSpeeds { get; init; }
}

/// <summary>
/// Everything the tier-2 colony model starts from, assembled per review by <c>RoundsColonyModel.InputOf</c>. Every section is
/// present; <see cref="Gaps"/> names a value outside its range.
/// </summary>
public sealed record ColonyModelInput
{
    /// <summary>Observed daily food demand from consumers without a modeled pawn cycle, such as colony animals.</summary>
    public required double OtherNutritionPerDay { get; init; }

    /// <summary>The twelve twelfth temperatures, the current twelfth and the ticks to the next.</summary>
    public required GearClimate Climate { get; init; }

    /// <summary>The tile's growing days, the days remaining and until, and the non-growing stretch.</summary>
    public required Calendar Calendar { get; init; }

    /// <summary>Standing growing zones and planned fields with their cells' fertility.</summary>
    public required EquatableArray<ModelField> Land { get; init; }

    /// <summary>The sowable crops with their growth, sowing, soil and temperature facts.</summary>
    public required EquatableArray<CropChoice> Crops { get; init; }

    public required ModelResearch Research { get; init; }

    public required ModelStocks Stocks { get; init; }

    public required EquatableArray<ModelPawn> Pawns { get; init; }

    /// <summary>The recipes of the standing benches the model may craft: their work, input slots and products.</summary>
    public required EquatableArray<ProductionRecipeInput> Recipes { get; init; }

    /// <summary>The huntable wildlife, one herd per race; empty when nothing on the map can be hunted for food.</summary>
    public required EquatableArray<ModelHerd> Wildlife { get; init; }

    /// <summary>Whether every value is inside its range.</summary>
    public bool Valid() => Gaps().Count == 0;

    /// <summary>
    /// Names each section or member the model cannot start from, with the reason: a value outside its range, a pawn cycle that is
    /// not a day. The bench count, research speeds and plant work speeds must be finite and not negative.
    /// </summary>
    public IReadOnlyList<string> Gaps()
    {
        var gaps = new List<string>();
        ClimateGaps(gaps);
        CalendarGaps(gaps);
        LandGaps(gaps);
        CropGaps(gaps);
        ResearchGaps(gaps);
        StockGaps(gaps);
        if (!Number(OtherNutritionPerDay) || OtherNutritionPerDay < 0) gaps.Add("stocks: other food-consumer demand is invalid");
        PawnGaps(gaps);
        CycleGaps(gaps);
        RecipeGaps(gaps);
        WildlifeGaps(gaps);
        return gaps;
    }

    private static bool Number(double v) => double.IsFinite(v);

    private static bool Nonnegative(double v) => Number(v) && v >= 0;

    private void ClimateGaps(List<string> gaps)
    {
        try
        {
            Climate.Validate();
        }
        catch (DomainException e)
        {
            gaps.Add($"climate: {e.Message}");
        }
    }

    private void CalendarGaps(List<string> gaps)
    {
        if (!Calendar.Valid())
        {
            gaps.Add("calendar: a day count is outside the year");
        }
        else if (Calendar.GrowingYear.Count != CalendarRules.YearDays)
        {
            gaps.Add("calendar: the native daily growing year is unavailable");
        }
    }

    private void LandGaps(List<string> gaps)
    {
        foreach (var field in Land)
        {
            if (field.Id.Length == 0 || field.Fertility.Count == 0 || field.Fertility.Any(f => !Number(f) || f < 0) || field.Planned && field.Crop.Length != 0)
            {
                gaps.Add($"land: field '{field.Id}' is not a named set of fertile cells (planned={field.Planned}, crop='{field.Crop}')");
            }
        }
    }

    private void CropGaps(List<string> gaps)
    {
        foreach (var crop in Crops)
        {
            var missing = new List<string>();
            if (!crop.GrowDays.TryGetValue(out var days) || !(days > 0))
            {
                missing.Add(nameof(crop.GrowDays));
            }

            if (!crop.SowMinSkill.TryGetValue(out var skill) || skill < 0)
            {
                missing.Add(nameof(crop.SowMinSkill));
            }

            AddUnlessNonnegative(missing, nameof(crop.SowWork), crop.SowWork);
            AddUnlessNonnegative(missing, nameof(crop.UnitsPerCell), crop.UnitsPerCell);
            AddUnlessNonnegative(missing, nameof(crop.FertilityMin), crop.FertilityMin);
            AddUnlessNonnegative(missing, nameof(crop.FertilitySensitivity), crop.FertilitySensitivity);
            AddUnlessNonnegative(missing, nameof(crop.HarvestMinGrowth), crop.HarvestMinGrowth);
            if (!crop.RestingDayFraction.TryGetValue(out var resting) || !(resting >= 0 && resting < 1))
            {
                missing.Add(nameof(crop.RestingDayFraction));
            }

            if (!crop.MinGrowthTemperature.TryGetValue(out var lo) || !crop.MinOptimalGrowthTemperature.TryGetValue(out var lo2)
                || !crop.MaxOptimalGrowthTemperature.TryGetValue(out var hi2) || !crop.MaxGrowthTemperature.TryGetValue(out var hi)
                || !(lo <= lo2 && lo2 <= hi2 && hi2 <= hi))
            {
                missing.Add("growth temperature band");
            }

            if (missing.Count > 0)
            {
                gaps.Add($"crops: {crop.Name} lacks {string.Join(", ", missing)}");
            }
        }
    }

    private static void AddUnlessNonnegative(List<string> missing, string name, Fact<double> value)
    {
        if (!value.TryGetValue(out var v) || !Number(v) || v < 0)
        {
            missing.Add(name);
        }
    }

    private void ResearchGaps(List<string> gaps)
    {
        var listed = Research.Projects.Select(p => p.Project).Concat(Research.Finished).ToHashSet();
        if (!Research.Current.IsEmpty && !listed.Contains(Research.Current))
        {
            gaps.Add($"research: current project {Research.Current} is not in the census");
        }

        foreach (var project in Research.Projects)
        {
            var unknownPrerequisite = project.Prerequisites.FirstOrDefault(p => !listed.Contains(p));
            if (project.Project.IsEmpty || !Number(project.Cost) || project.Cost <= 0
                || !(project.Progress >= 0 && project.Progress <= 1)
                || !unknownPrerequisite.IsEmpty)
            {
                gaps.Add($"research: project '{project.Project}' has no positive cost, a progress outside 0..1 or a prerequisite ('{unknownPrerequisite}') outside the census");
            }
        }

        if (Research.Benches < 0)
        {
            gaps.Add("research: the bench count is negative");
        }
    }

    private void StockGaps(List<string> gaps)
    {
        if (!Number(Stocks.Nutrition) || Stocks.Nutrition < 0)
        {
            gaps.Add("stocks: nutrition is not a finite non-negative number");
        }

        if (Stocks.Materials.Any(a => a.Count < 0))
        {
            gaps.Add("stocks: a material count is negative");
        }

        if (Stocks.Medicine.Any(a => a.Count < 0))
        {
            gaps.Add("stocks: a medicine count is negative");
        }
    }

    private void PawnGaps(List<string> gaps)
    {
        foreach (var pawn in Pawns)
        {
            var missing = new List<string>();
            foreach (var need in pawn.Needs)
            {
                if (need.Kind.Length == 0 || !Number(need.Fall) || !Number(need.Threshold)
                    || !(need.Gain >= 0) || need.Service is ModelService.Observed use && !(Nonnegative(use.GainPerUse) && Nonnegative(use.ServiceTicks) && Nonnegative(use.ApproachTicks))
                    || need.OutdoorShares.Count is not (0 or 12) || need.OutdoorShares.Any(s => !(s >= 0 && s <= 1)) || !(need.ShelteredGain >= 0) || double.IsInfinity(need.ShelteredGain))
                {
                    missing.Add($"need '{need.Kind}'");
                }
            }

            if (pawn.Skills.Any(s => s.Name.Length == 0 || s.Level < 0))
            {
                missing.Add("skills");
            }

            foreach (var (name, capacity) in new[]
            {
                (nameof(pawn.Consciousness), pawn.Consciousness), (nameof(pawn.Moving), pawn.Moving), (nameof(pawn.Manipulation), pawn.Manipulation),
                (nameof(pawn.Sight), pawn.Sight), (nameof(pawn.Talking), pawn.Talking),
            })
            {
                if (!Nonnegative(capacity))
                {
                    missing.Add(name);
                }
            }

            if (!Number(pawn.ComfortableMin) || !Number(pawn.ComfortableMax) || pawn.ComfortableMin > pawn.ComfortableMax)
            {
                missing.Add("comfortable range");
            }

            if (!Nonnegative(pawn.ResearchSpeed))
            {
                missing.Add(nameof(pawn.ResearchSpeed));
            }

            if (!Nonnegative(pawn.PlantWorkSpeed))
            {
                missing.Add(nameof(pawn.PlantWorkSpeed));
            }

            if (pawn.Pawn.Length == 0 || missing.Count > 0)
            {
                gaps.Add($"pawns: '{pawn.Pawn}' lacks {string.Join(", ", missing)}");
            }
        }
    }

    /// <summary>The pawn cycle (#3526) is derived per twelfth; a pawn whose year has a twelfth that is not a valid day names the twelfths and every unknown rate behind them.</summary>
    private void CycleGaps(List<string> gaps)
    {
        foreach (var pawn in Pawns)
        {
            var broken = PawnCycle.Year(this, pawn).Where(c => !c.Valid()).ToList();
            if (broken.Count > 0)
            {
                gaps.Add($"cycle: '{broken[0].Pawn}' has no valid day in twelfths {string.Join(",", broken.Select(c => c.Twelfth))} ({string.Join(", ", broken.SelectMany(c => c.Findings).Distinct())})");
            }
        }
    }

    private void RecipeGaps(List<string> gaps)
    {
        foreach (var recipe in Recipes)
        {
            if (recipe.Recipe.Length == 0 || !Number(recipe.WorkAmount) || recipe.WorkAmount < 0)
            {
                gaps.Add($"recipes: '{recipe.Recipe}' lacks its name or a usable work amount");
            }
        }
    }

    private void WildlifeGaps(List<string> gaps)
    {
        foreach (var herd in Wildlife)
        {
            if (herd.Race.IsEmpty || herd.Animals <= 0 || !Number(herd.Work) || herd.Work < 0 || !Number(herd.Nutrition) || herd.Nutrition < 0
                || herd.Products.Any(p => !Number(p.Amount) || p.Amount < 0))
            {
                gaps.Add($"wildlife: herd '{herd.Race}' has no animals or a work or yield that is not a finite non-negative number");
            }
        }
    }
}

/// <summary>What the review frame yields for the colony model: the input, or the sections it could not start from.</summary>
public abstract record ColonyModelInputRead
{
    private protected ColonyModelInputRead() { }

    /// <summary>Every section is present and inside its range.</summary>
    public sealed record Ready(ColonyModelInput Input) : ColonyModelInputRead;

    /// <summary>The frame lacks a section, or a value is outside its range; each entry names one with its reason.</summary>
    public sealed record Unready(IReadOnlyList<string> Gaps) : ColonyModelInputRead;
}
