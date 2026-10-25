namespace RimGovernor.Brain.Observation;
public sealed partial record PlanningDefinition
{
    public PlanningPlant? Plant { get; init; }
    public PlanningHarvest? Harvest { get; init; }
    public PlanningDiet? Diet { get; init; }
    public PlanningFloor? Floor { get; init; }
    public PlanningPower? Power { get; init; }
    public PlanningExplosive? Explosive { get; init; }
    public PlanningGrower? Grower { get; init; }
    public PlanningFixedConstruction? FixedConstruction { get; init; }
}

public sealed record PlanningPlant
{
    public double HarvestWork { get; init; }
    public bool RequiresPollution { get; init; }
    public bool RequiresCleanSoil { get; init; }
    public double GrowDays { get; init; }
    public double FertilityMin { get; init; }
    public double FertilitySensitivity { get; init; }
    public double GrowMinGlow { get; init; }
    public double MinGrowthTemperature { get; init; }
    public double MinOptimalGrowthTemperature { get; init; }
    public double MaxOptimalGrowthTemperature { get; init; }
    public double MaxGrowthTemperature { get; init; }
    public double RestingDayFraction { get; init; }
    public EquatableArray<string> SowTags { get; init; }
    public int SowMinSkill { get; init; }
    public bool BlockAdjacentSow { get; init; }
    public bool MustBeWildToSow { get; init; }
    public double HarvestMinGrowth { get; init; }
    public double SowWork { get; init; }
    public EquatableArray<string> WildBiomes { get; init; }
}

public sealed record PlanningHarvest
{
    public string HarvestedThingDef { get; init; } = "";
    public double HarvestYield { get; init; }
    public bool HarvestDestroysPlant { get; init; }
    public double HarvestNutrition { get; init; }
    public HarvestDecay? Decay { get; init; }
    public bool RawPreferred { get; init; }
    public bool Edible { get; init; }
}

public sealed record PlanningDiet
{
    public double NutritionDemandPerDay { get; init; }
    public bool DietAllowed { get; init; }
}

public sealed record PlanningFloor
{
    public double Cleanliness { get; init; }
    public double Beauty { get; init; }
    public double Flammability { get; init; }
    public IReadOnlyList<string> FloorTags { get; init; } = [];
}

public sealed record PlanningPower
{
    public double PowerW { get; init; }
}

public sealed record PlanningExplosive
{
    public double ExplosiveRadius { get; init; }
}

public sealed record PlanningGrower
{
    public double GrowerFertility { get; init; }
}

public sealed record PlanningFixedConstruction
{
    public EquatableArray<Amount> Costs { get; init; }
    public double WorkToBuild { get; init; }
}

public enum PlanningAvailability
{
    NotInCatalog,
    ResearchLocked,
    Ready
}
