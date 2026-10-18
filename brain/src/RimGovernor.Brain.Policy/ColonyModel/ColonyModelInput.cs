namespace RimGovernor.Brain.Policy;

// The tier-2 colony model's input (#3523 of #3470): one record per review, collected from facts the review frame already carries.
// Nothing runs the model yet (the pawn cycle, engine, graph and valuation are the next children); this is the contract they
// read. A section the frame did not carry is an unknown Fact, never an empty default, and Unknowns() names each one.

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
}

/// <summary>One unfinished research project the model may spend points on.</summary>
public sealed record ModelProject
{
    public ResearchProjectId Project { get; init; }

    /// <summary>The project's cost in research points (the catalog's cost).</summary>
    public double Cost { get; init; }

    /// <summary>The researched fraction of the cost; unknown when the census did not list the project.</summary>
    public Fact<double> Progress { get; init; }

    public EquatableArray<ResearchProjectId> Prerequisites { get; init; }
}

/// <summary>The research state: what is being researched, what is done and what is left to do.</summary>
public sealed record ModelResearch
{
    public ResearchProjectId Current { get; init; }

    public EquatableArray<ResearchProjectId> Finished { get; init; }

    /// <summary>Every listed project not finished.</summary>
    public EquatableArray<ModelProject> Projects { get; init; }

    /// <summary>The ResearchSpeedFactor of each research bench; unknown until the research provider's bench read reaches the frame (#3527 reads it in the coupled path only).</summary>
    public Fact<EquatableArray<double>> BenchFactors { get; init; }
}

/// <summary>The colony's stocks the model moves: nutrition, materials and medicine. Research points are per project (<see cref="ModelProject.Progress"/>); power is not a stock in v1.</summary>
public sealed record ModelStocks
{
    public Fact<double> Nutrition { get; init; }

    public Fact<EquatableArray<Amount>> Materials { get; init; }

    public Fact<long> Medicine { get; init; }
}

/// <summary>One need's observed mechanics: it falls by Fall per tick, a service gives Gain per tick, and the pawn seeks service under Threshold.</summary>
public sealed record ModelNeed
{
    public string Kind { get; init; } = "";

    public Fact<double> Fall { get; init; }

    /// <summary>The best observed service's gain per tick; unknown while no service is observed.</summary>
    public Fact<double> Gain { get; init; }

    public Fact<double> Threshold { get; init; }
}

/// <summary>One colonist as the model's pawn cycle reads them.</summary>
public sealed record ModelPawn
{
    public string Pawn { get; init; } = "";

    public EquatableArray<ModelNeed> Needs { get; init; }

    public Fact<EquatableArray<WorkSkill>> Skills { get; init; }

    /// <summary>The five capacities a pawn-day is counted in (#3519).</summary>
    public Fact<double> Consciousness { get; init; }

    public Fact<double> Moving { get; init; }

    public Fact<double> Manipulation { get; init; }

    public Fact<double> Sight { get; init; }

    public Fact<double> Talking { get; init; }

    /// <summary>The ResearchSpeed stat; unknown until the research provider's read reaches the frame (#3527 reads it in the coupled path only).</summary>
    public Fact<double> ResearchSpeed { get; init; }

    /// <summary>The PlantWorkSpeed stat; unknown until the sowing provider's read reaches the frame (#3528 reads it in the coupled path only).</summary>
    public Fact<double> PlantWorkSpeed { get; init; }
}

/// <summary>
/// Everything the tier-2 colony model starts from, assembled per review by <c>RoundsColonyModel.InputOf</c>. Every section is a
/// Fact: unknown means the frame did not carry it, and <see cref="Unknowns"/> names it.
/// </summary>
public sealed record ColonyModelInput
{
    /// <summary>The twelve twelfth temperatures, the current twelfth and the ticks to the next.</summary>
    public Fact<GearClimate> Climate { get; init; }

    /// <summary>The tile's growing days, the days remaining and until, and the non-growing stretch.</summary>
    public Fact<Calendar> Calendar { get; init; }

    /// <summary>Standing growing zones and planned fields with their cells' fertility.</summary>
    public Fact<EquatableArray<ModelField>> Land { get; init; }

    /// <summary>The sowable crops with their growth, sowing, soil and temperature facts.</summary>
    public Fact<EquatableArray<CropChoice>> Crops { get; init; }

    public Fact<ModelResearch> Research { get; init; }

    public ModelStocks Stocks { get; init; } = new();

    public Fact<EquatableArray<ModelPawn>> Pawns { get; init; }

    /// <summary>The recipes of the standing benches the model may craft: their work, input slots and products.</summary>
    public Fact<EquatableArray<ProductionRecipeInput>> Recipes { get; init; }

    /// <summary>Standing blueprints and frames with their remaining work and owed materials.</summary>
    public Fact<EquatableArray<ConstructionSite>> Sites { get; init; }

    /// <summary>The sites the layout plan would place, with the material each takes.</summary>
    public Fact<EquatableArray<PlannedBuildSite>> PlannedSites { get; init; }

    /// <summary>Whether every section the model needs is known and well formed.</summary>
    public bool Valid() => Unknowns().Count == 0;

    /// <summary>
    /// Names each section or member the model cannot start from, with the reason: an unknown the frame did not carry, or a value
    /// outside its range. The bench factors, research speeds and plant work speeds are not named while unknown (the providers'
    /// reads reach them later); once known they must be finite and not negative.
    /// </summary>
    public IReadOnlyList<string> Unknowns()
    {
        var gaps = new List<string>();
        ClimateGaps(gaps);
        CalendarGaps(gaps);
        LandGaps(gaps);
        CropGaps(gaps);
        ResearchGaps(gaps);
        StockGaps(gaps);
        PawnGaps(gaps);
        RecipeGaps(gaps);
        SiteGaps(gaps);
        return gaps;
    }

    private static bool Number(double v) => double.IsFinite(v);

    private static bool Nonnegative(Fact<double> v) => !v.TryGetValue(out var x) || Number(x) && x >= 0;

    private void ClimateGaps(List<string> gaps)
    {
        if (!Climate.TryGetValue(out var climate))
        {
            gaps.Add("climate: the review frame carries no seasonal temperatures");
            return;
        }

        try
        {
            climate.Validate();
        }
        catch (DomainException e)
        {
            gaps.Add($"climate: {e.Message}");
        }
    }

    private void CalendarGaps(List<string> gaps)
    {
        if (!Calendar.TryGetValue(out var calendar))
        {
            gaps.Add("calendar: the review frame carries no growing calendar");
        }
        else if (!calendar.Valid())
        {
            gaps.Add("calendar: a day count is outside the year");
        }
    }

    private void LandGaps(List<string> gaps)
    {
        if (!Land.TryGetValue(out var fields))
        {
            gaps.Add("land: a cell of a growing zone or planned field has no fertility reading");
            return;
        }

        foreach (var field in fields)
        {
            if (field.Id.Length == 0 || field.Fertility.Count == 0 || field.Fertility.Any(f => !Number(f) || f < 0) || field.Planned && field.Crop.Length != 0)
            {
                gaps.Add($"land: field '{field.Id}' is not a named set of fertile cells (planned={field.Planned}, crop='{field.Crop}')");
            }
        }
    }

    private void CropGaps(List<string> gaps)
    {
        if (!Crops.TryGetValue(out var crops))
        {
            gaps.Add("crops: the catalog lists no sowable crop");
            return;
        }

        foreach (var crop in crops)
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
        if (!Research.TryGetValue(out var research))
        {
            gaps.Add("research: the review frame carries no research census");
            return;
        }

        var listed = research.Projects.Select(p => p.Project).Concat(research.Finished).ToHashSet();
        if (!research.Current.IsEmpty && !listed.Contains(research.Current))
        {
            gaps.Add($"research: current project {research.Current} is not in the census");
        }

        foreach (var project in research.Projects)
        {
            var unknownPrerequisite = project.Prerequisites.FirstOrDefault(p => !listed.Contains(p));
            if (project.Project.IsEmpty || !Number(project.Cost) || project.Cost <= 0
                || project.Progress.TryGetValue(out var progress) && !(progress >= 0 && progress <= 1)
                || !unknownPrerequisite.IsEmpty)
            {
                gaps.Add($"research: project '{project.Project}' has no positive cost, a progress outside 0..1 or a prerequisite ('{unknownPrerequisite}') outside the census");
            }
            else if (project.Project == research.Current && !project.Progress.IsKnown)
            {
                gaps.Add($"research: current project {project.Project} has no progress reading");
            }
        }

        if (research.BenchFactors.TryGetValue(out var factors) && factors.Any(f => !Number(f) || f < 0))
        {
            gaps.Add("research: a bench factor is not a finite non-negative number");
        }
    }

    private void StockGaps(List<string> gaps)
    {
        if (!Stocks.Nutrition.TryGetValue(out var nutrition))
        {
            gaps.Add("stocks: the food census is unknown");
        }
        else if (!Number(nutrition) || nutrition < 0)
        {
            gaps.Add("stocks: nutrition is not a finite non-negative number");
        }

        if (!Stocks.Materials.TryGetValue(out var materials))
        {
            gaps.Add("stocks: the resource census is unknown");
        }
        else if (materials.Any(a => a.Count < 0))
        {
            gaps.Add("stocks: a material count is negative");
        }

        if (!Stocks.Medicine.TryGetValue(out var medicine))
        {
            gaps.Add("stocks: the medicine census is unknown");
        }
        else if (medicine < 0)
        {
            gaps.Add("stocks: the medicine count is negative");
        }
    }

    private void PawnGaps(List<string> gaps)
    {
        if (!Pawns.TryGetValue(out var pawns))
        {
            gaps.Add("pawns: the review frame carries no needs read");
            return;
        }

        foreach (var pawn in pawns)
        {
            var missing = new List<string>();
            foreach (var need in pawn.Needs)
            {
                if (need.Kind.Length == 0 || !need.Fall.TryGetValue(out var fall) || !Number(fall) || !need.Threshold.TryGetValue(out var threshold) || !Number(threshold)
                    || !Nonnegative(need.Gain))
                {
                    missing.Add($"need '{need.Kind}'");
                }
            }

            if (!pawn.Skills.TryGetValue(out var skills) || skills.Any(s => s.Name.Length == 0 || s.Level < 0))
            {
                missing.Add("skills");
            }

            foreach (var (name, capacity) in new[]
            {
                (nameof(pawn.Consciousness), pawn.Consciousness), (nameof(pawn.Moving), pawn.Moving), (nameof(pawn.Manipulation), pawn.Manipulation),
                (nameof(pawn.Sight), pawn.Sight), (nameof(pawn.Talking), pawn.Talking),
            })
            {
                if (!capacity.TryGetValue(out var level) || !Number(level) || level < 0)
                {
                    missing.Add(name);
                }
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

    private void RecipeGaps(List<string> gaps)
    {
        if (!Recipes.TryGetValue(out var recipes))
        {
            gaps.Add("recipes: the review carries no bench recipe census");
            return;
        }

        foreach (var recipe in recipes)
        {
            if (recipe.Recipe.Length == 0 || !recipe.Slots.IsKnown || !recipe.Products.IsKnown || !recipe.WorkAmount.TryGetValue(out var work) || !Number(work) || work < 0)
            {
                gaps.Add($"recipes: '{recipe.Recipe}' lacks its work, input slots or products");
            }
        }
    }

    private void SiteGaps(List<string> gaps)
    {
        if (!Sites.TryGetValue(out var sites))
        {
            gaps.Add("sites: the review frame carries no construction census");
        }
        else
        {
            foreach (var site in sites)
            {
                if (!site.WorkLeft.TryGetValue(out var work) || !Number(work) || work < 0 || !site.MaterialsOwed.IsKnown)
                {
                    gaps.Add($"sites: '{site.Id}' lacks its remaining work or owed materials");
                }
            }
        }

        if (!PlannedSites.IsKnown)
        {
            gaps.Add("sites: the layout plan's sites are not assembled this review");
        }
    }
}
