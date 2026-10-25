namespace RimGovernor.Brain.Policy;
public sealed partial record PlanningDefinition
{
    /// <summary>The complete crop option of a harvested plant, including its colony diet when it produces food.</summary>
    public CropChoice CropChoice()
    {
        var plant = Plant ?? throw new RequiredFactException("plant definition:" + Name);
        var harvest = Harvest ?? throw new RequiredFactException("plant harvest:" + Name);
        var diet = harvest.Edible ? Diet ?? throw new RequiredFactException("edible crop diet:" + Name) : null;
        return new CropChoice
        {
            Name = Name,
            Available = Available,
            Research = EquatableArray.From(Research.Select(project => new ResearchProjectId(project))),
            Harvests = new Resource(harvest.HarvestedThingDef),
            UnitsPerCell = harvest.HarvestYield,
            HarvestDestroys = harvest.HarvestDestroysPlant,
            Edible = harvest.Edible,
            HarvestNutrition = harvest.HarvestNutrition,
            RawPreferred = harvest.RawPreferred,
            Decay = harvest.Decay,
            Demand = diet is null ? 0 : diet.NutritionDemandPerDay,
            DietAllowed = diet is null || diet.DietAllowed,
            GrowDays = plant.GrowDays,
            FertilityMin = plant.FertilityMin,
            FertilitySensitivity = plant.FertilitySensitivity,
            SowTags = plant.SowTags,
            MinGlow = plant.GrowMinGlow,
            HarvestWork = plant.HarvestWork,
            RequiresPollution = plant.RequiresPollution,
            RequiresCleanSoil = plant.RequiresCleanSoil,
            MinGrowthTemperature = plant.MinGrowthTemperature,
            MinOptimalGrowthTemperature = plant.MinOptimalGrowthTemperature,
            MaxOptimalGrowthTemperature = plant.MaxOptimalGrowthTemperature,
            MaxGrowthTemperature = plant.MaxGrowthTemperature,
            RestingDayFraction = plant.RestingDayFraction,
            SowMinSkill = plant.SowMinSkill,
            BlockAdjacentSow = plant.BlockAdjacentSow,
            MustBeWildToSow = plant.MustBeWildToSow,
            HarvestMinGrowth = plant.HarvestMinGrowth,
            SowWork = plant.SowWork,
            WildBiomes = plant.WildBiomes,
        };
    }
}
