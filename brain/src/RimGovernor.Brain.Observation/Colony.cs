namespace RimGovernor.Brain.Observation;

public sealed partial record PlanningDefinition
{
    public Fact<double> HarvestWork { get; init; }

    public Fact<bool> RawPreferred { get; init; }

    public Fact<bool> DietAllowed { get; init; }

    public Fact<bool> RequiresPollution { get; init; }

    public Fact<bool> RequiresCleanSoil { get; init; }

    public string Name { get; init; } = "";

    public Fact<bool> Edible { get; init; }

    public Fact<bool> Available { get; init; }

    public Fact<int> ConstructionSkill { get; init; }

    public Fact<bool> NeedsPower { get; init; }

    /// <summary>True for a Building_MechCharger definition.</summary>
    public Fact<bool> MechCharger { get; init; }

    /// <summary>
    /// True for a building with a pollution or wastepack comp (CompToxifier, CompPolluteOverTime,
    /// CompWasteProducer); unknown when the row did not say.
    /// </summary>
    public Fact<bool> Pollutes { get; init; }

    /// <summary>The ResearchProjectDefs the definition requires, as the native definition declares them (unfinished ones make it unavailable).</summary>
    public IReadOnlyList<string> Research { get; init; } = [];

    public Fact<EquatableArray<Amount>> Costs { get; init; }

    public Fact<Bounds> Size { get; init; }

    public Fact<double> GrowDays { get; init; }

    public Fact<double> FertilityMin { get; init; }

    public Fact<double> FertilitySensitivity { get; init; }

    public Fact<double> HarvestNutrition { get; init; }

    public Fact<double> NutritionDemandPerDay { get; init; }

    /// <summary>The harvested item's rot facts.</summary>
    public Fact<double> HarvestRotDays { get; init; }

    public Fact<bool> HarvestPerishable { get; init; }

    /// <summary>
    /// What one harvest of the plant yields; SowMinSkill is the sowing skill floor and HarvestDestroysPlant
    /// whether a harvest removes the plant (a tree is felled, a rice stand is cut). Unknown for a plant that
    /// names no product.
    /// </summary>
    public Fact<string> HarvestedThingDef { get; init; }

    public Fact<double> HarvestYield { get; init; }

    public Fact<int> SowMinSkill { get; init; }

    public Fact<bool> HarvestDestroysPlant { get; init; }

    /// <summary>
    /// The sowing facts a tree plantation prices: the native sower leaves the cells beside a sown tree empty,
    /// the game offers only wild species to sow, a tree is harvestable from HarvestMinGrowth, one sowing costs
    /// SowWork ticks, and WildBiomes are the biome defNames the plant grows wild in.
    /// </summary>
    public Fact<bool> BlockAdjacentSow { get; init; }

    public Fact<bool> MustBeWildToSow { get; init; }

    public Fact<double> HarvestMinGrowth { get; init; }

    public Fact<double> SowWork { get; init; }

    public Fact<EquatableArray<string>> WildBiomes { get; init; }

    /// <summary>Crop sow tags and minimum glow; grower sow tag and fertility; building power draw and glow radius, as the native definition declares them.</summary>
    public Fact<EquatableArray<string>> SowTags { get; init; }

    public Fact<double> GrowMinGlow { get; init; }

    public Fact<double> PowerW { get; init; }

    public Fact<double> GrowerFertility { get; init; }

    public Fact<double> GlowRadius { get; init; }

    public Fact<double> ExplosiveRadius { get; init; }

    /// <summary>The crop's growth temperature range (PlantProperties).</summary>
    public Fact<double> MinGrowthTemperature { get; init; }

    public Fact<double> MinOptimalGrowthTemperature { get; init; }

    public Fact<double> MaxOptimalGrowthTemperature { get; init; }

    public Fact<double> MaxGrowthTemperature { get; init; }

    public Fact<string> SowTag { get; init; }

    /// <summary>Floor definition facts: Terrain marks a TerrainDef and the stats are what a laid floor carries.</summary>
    public Fact<bool> Terrain { get; init; }

    public Fact<double> Cleanliness { get; init; }

    public Fact<double> Beauty { get; init; }

    public Fact<double> Flammability { get; init; }

    public Fact<int> PathCost { get; init; }

    /// <summary>The native WorkToBuild stat (work ticks) for the row's stuff.</summary>
    public Fact<double> WorkToBuild { get; init; }

    /// <summary>A TerrainDef's tags (the throne floor requirement).</summary>
    public IReadOnlyList<string> FloorTags { get; init; } = [];

    /// <summary>
    /// Whether the def is made from stuff (it has stuff categories). A stuffed def has no single cost list:
    /// Costs is unknown and StuffChoice prices it from StuffOptions.
    /// </summary>
    public bool Stuffed { get; init; }

    /// <summary>
    /// Every allowed stuff with its cost list and stat values, ordered by defName; empty for a definition not
    /// made from stuff, and for a stuffed one nothing is allowed to make it from.
    /// </summary>
    public IReadOnlyList<StuffOption> StuffOptions { get; init; } = [];

    /// <summary>The room-role furniture roles the native catalog assigns the definition (FurnitureRole names), sorted.</summary>
    public IReadOnlyList<string> RoomRoles { get; init; } = [];
}

/// <summary>One material a stuffed definition may be built from.</summary>
public sealed record StuffOption
{
    public string Stuff { get; init; } = "";

    public IReadOnlyList<Amount> Costs { get; init; } = [];

    /// <summary>The market value of Costs: what the cheapest criterion ranks by.</summary>
    public double Value { get; init; }

    /// <summary>
    /// Whether the game generates the stuff as an ordinary material (StuffProperties.commonality above zero).
    /// Bioferrite is not: it is only ever made by an Anomaly colony, so a def is never planned from it while an
    /// ordinary stuff is allowed.
    /// </summary>
    public bool Common { get; init; }

    /// <summary>The stat values the game shows for the def made of this stuff (the StuffChoice criteria); a stat the game does not show is absent.</summary>
    public IReadOnlyDictionary<string, double> Stats { get; init; } = new Dictionary<string, double>();
}

public sealed partial record ColonyProjection
{
    public Fact<DeepResources> DeepResources { get; init; }

    public Fact<Policies> Policies { get; init; }

    public Fact<BiotechColony> Biotech { get; init; }

    public Fact<OdysseyColony> Odyssey { get; init; }

    public Fact<AnomalyColony> Anomaly { get; init; }

    /// <summary>The Royalty colony section; unknown without Royalty or when the read failed.</summary>
    public Fact<RoyaltyColony> RoyaltyColony { get; init; }

    /// <summary>The creepjoiner isolation room's inputs, set by the rounds.</summary>
    public IsolationPlanning Isolation { get; init; } = new();

    /// <summary>What the tomb reads to stage stranger corpses, set by the rounds; the zero value stages none.</summary>
    public StrangerTomb Strangers { get; init; } = new();

    /// <summary>The catalog's piece shapes and the furniture its rules choose (DefinitionCatalog.PieceShapes), set when a catalog is read; the zero value without one.</summary>
    public PieceShapes Shapes { get; init; } = new();

    /// <summary>The catalog's station table (DefinitionCatalog.StationCatalog), set on a rounds reading; the zero value without one.</summary>
    public StationCatalog Stations { get; init; } = new();

    public Fact<IReadOnlyList<FoodField>> FoodFields { get; init; }

    public Fact<FoodChannels> FoodChannels { get; init; }

    public Fact<IReadOnlyList<ProductionBench>> ProductionBenches { get; init; }

    public Fact<IReadOnlyList<CookingBench>> ButcheringBenches { get; init; }

    public Fact<double> FoodAtRiskNutrition { get; init; }

    public CropClimate CropClimate { get; init; } = new();

    /// <summary>Whether the seasonal outdoor temperature curve dips below freezing (ColdMapCurve); unknown without the curve.</summary>
    public Fact<bool> ColdMap { get; init; }

    /// <summary>Whether the curve peaks above HotEnter (HotMapCurve); unknown without the curve.</summary>
    public Fact<bool> HotMap { get; init; }

    /// <summary>The twelve seasonal means themselves (C).</summary>
    public Fact<IReadOnlyList<double>> OutdoorCurve { get; init; }

    /// <summary>The catalog's full-rot temperature, which the climate advice reads.</summary>
    public double FullRotRateC { get; init; }

    public Fact<int> PendingHunts { get; init; }

    /// <summary>Native's cumulative delivery counters; unknown when the census is missing or malformed.</summary>
    public Fact<DeliveryLedger> DeliveryLedger { get; init; }

    public Fact<IReadOnlyList<AcquisitionSource>> Acquisition { get; init; }

    /// <summary>The hunt rows HuntGate holds, with the gate that failed.</summary>
    public IReadOnlyList<HuntHold> HuntHolds { get; init; } = [];

    public Fact<double> PendingFoodNutrition { get; init; }

    public Fact<double> PendingWoodUnits { get; init; }

    public Fact<IReadOnlyList<WorkPawn>> WorkPawns { get; init; }

    /// <summary>The Biotech catalog's mech kinds; the zero value without Biotech.</summary>
    public MechCatalog MechCatalog { get; init; } = new();

    /// <summary>The colony's mechanitors and mechs from the pawn table; unknown without a table.</summary>
    public Fact<MechFleet> Mechs { get; init; }

    /// <summary>Meditate TimeAssignmentDef exists.</summary>
    public Fact<bool> MeditateAvailable { get; init; }

    public Fact<IReadOnlyList<FieldCrop>> FieldCrops { get; init; }

    public Fact<IReadOnlyList<FieldCrop>> FieldCapacityCrops { get; init; }

    public Fact<IReadOnlyList<CookingBench>> CookingBenches { get; init; }

    public Fact<PowerTopology> PowerPlanning { get; init; }

    /// <summary>The catalog's producer delivery profiles and the storage of its battery def (DefinitionCatalog.PowerSources, PowerBattery); the power planner refuses a projection without them.</summary>
    public IReadOnlyDictionary<string, PowerSourceProfile> PowerSources { get; init; } = new Dictionary<string, PowerSourceProfile>();

    public PowerBattery PowerBattery { get; init; } = new();

    /// <summary>The catalog rows of the ranked generator definitions (DefinitionCatalog.GeneratorRow).</summary>
    public IReadOnlyDictionary<string, GeneratorRow> GeneratorRows { get; init; } = new Dictionary<string, GeneratorRow>();

    /// <summary>The steel a component costs by the game's recipe (ProductInputCost).</summary>
    public Fact<double> ComponentSteel { get; init; }

    /// <summary>The terrain defs affording Light (DefinitionCatalog.LightTerrains).</summary>
    public LightTerrains LightTerrains { get; init; } = new();

    /// <summary>The game's roof support radius (the catalog constant roof_max_support_distance).</summary>
    public double RoofSupport { get; init; }

    /// <summary>The game's room impressiveness stage thresholds (the Impressiveness RoomStatDef's score stages).</summary>
    public ImpressivenessLevels Impressiveness { get; init; } = new();

    /// <summary>Every built turret gun in the power census with its observed damage per second.</summary>
    public Fact<IReadOnlyList<DefenseTurretFacts>> DefenseTurrets { get; init; }

    public Fact<RoomObservation> Rooms { get; init; }

    public Identity Identity { get; init; } = new();

    /// <summary>The player faction's native TechLevel name, the faction's tech level.</summary>
    public Fact<string> PlayerTechLevel { get; init; }

    /// <summary>The catalog's building defs that pack (minifiedDef); empty without a catalog.</summary>
    public IReadOnlySet<string> Packable { get; init; } = new HashSet<string>();

    /// <summary>The construction tier derived from finished research with PlayerTechLevel as its floor; unknown until a routine reading served the research census.</summary>
    public Fact<TechTier> TechTier { get; init; }

    /// <summary>The persisted v2 layout, served by the routine review; unknown until one is derived.</summary>
    public Fact<LayoutPlan> LayoutPlan { get; init; }

    /// <summary>The Empire ladder, permits and holdings; unknown without Royalty or a royalty source.</summary>
    public Fact<RoyaltyFacts> Royalty { get; init; }

    public RoundsFacts Facts { get; init; } = new();

    /// <summary>Prices a bed by (def, stuff) from the catalog's MarketValue rows; null without a catalog.</summary>
    public BedPrice? BedPrice { get; init; }

    public Fact<int> Workers { get; init; }

    public Bounds Bounds { get; init; }

    /// <summary>The observed planning window; cells absent inside it are fogged, cells outside it were never read.</summary>
    public Rectangle Region { get; init; }

    public IReadOnlyList<SiteCell> Cells { get; init; } = [];

    /// <summary>The hostile lords the map has seen since load (the native RaidArrivalState tracks), the defense layout's arrivals.</summary>
    public IReadOnlyList<RaidTrack> Raids { get; init; } = [];

    /// <summary>The window's provenance when a PlanningWindowSource served it (Source set); null when the reply listed the cells itself.</summary>
    public Held<PlanningCells>? Window { get; init; }

    public Held<ZonesRead>? Zones { get; init; }

    /// <summary>Observed growing zones by native id and current crop.</summary>
    public IReadOnlyList<FarmZoneFact> Farms { get; init; } = [];

    /// <summary>The controlled-growing census inside the planning region.</summary>
    public Fact<ControlledEnvironment> Environment { get; init; }

    public IReadOnlyList<PlanningDefinition> Definitions { get; init; } = [];

    public Fact<FoodSupply> FoodSupply { get; init; }

    public Fact<FoodSupply> CombinedFoodSupply { get; init; }

    /// <summary>The accessible colony stock census by definition; a definition absent from a known census is known zero.</summary>
    public Fact<IReadOnlyDictionary<Resource, long>> Resources { get; init; }

    /// <summary>The census's wealth split and raid points; every reading is unknown under a native build without the section.</summary>
    public ColonyThreat Threat { get; init; } = new();

    /// <summary>
    /// The middle of the layout plan's core: unknown until a plan exists, so nothing anchors on where the
    /// colonists happen to stand.
    /// </summary>
    public Fact<Cell> Center()
    {
        if (LayoutPlan.TryGetValue(out var plan) && plan.Center() is { } center)
        {
            return Known(center);
        }
        return Unknown<Cell>();
    }

    /// <summary>The accessible stock of one definition, unknown when the census itself is unknown.</summary>
    public Fact<long> ResourceStock(Resource name)
    {
        if (!Resources.TryGetValue(out var stock))
        {
            return Unknown<long>();
        }
        return Known(stock.GetValueOrDefault(name));
    }

    /// <summary>
    /// The first of the definition's stuff options whose cost list the colony stock covers, and how many of the
    /// definition that stock builds. None is null, as is an unknown stock census.
    /// </summary>
    public (string Stuff, long Count)? StockedStuff(string name)
    {
        if (!Resources.TryGetValue(out var stock))
        {
            return null;
        }
        foreach (var d in Definitions)
        {
            if (d.Name != name)
            {
                continue;
            }
            foreach (var option in d.StuffOptions)
            {
                var count = -1L;
                foreach (var cost in option.Costs)
                {
                    if (cost.Count <= 0)
                    {
                        continue;
                    }
                    var n = stock.GetValueOrDefault(cost.Resource) / cost.Count;
                    if (count < 0 || n < count)
                    {
                        count = n;
                    }
                }
                if (count > 0)
                {
                    return (option.Stuff, count);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Whether the colony stock, net of holds, covers one allowed stuff's cost list for one of the definition
    /// (the next sarcophagus). An unknown stock census is not funded.
    /// </summary>
    public bool StuffFunded(string name, IReadOnlyDictionary<Resource, long> holds)
    {
        if (!Resources.TryGetValue(out var stock))
        {
            return false;
        }
        foreach (var d in Definitions)
        {
            if (d.Name != name)
            {
                continue;
            }
            foreach (var option in d.StuffOptions)
            {
                var covered = option.Costs.Count > 0;
                foreach (var cost in option.Costs)
                {
                    if (cost.Count > 0 && stock.GetValueOrDefault(cost.Resource) - holds.GetValueOrDefault(cost.Resource) < cost.Count)
                    {
                        covered = false;
                    }
                }
                if (covered)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>One planning definition's native availability, unknown when the census did not describe it.</summary>
    public Fact<bool> DefinitionAvailable(string name)
    {
        foreach (var d in Definitions)
        {
            if (d.Name == name)
            {
                return d.Available;
            }
        }
        return Unknown<bool>();
    }

    /// <summary>Pairs the power family's generator definitions with their native availability, observed fuel stock and catalog delivery profile.</summary>
    public IReadOnlyList<GeneratorOption> GeneratorOptions(Func<Resource, bool> renewable)
    {
        var available = new Dictionary<string, Fact<bool>>(StringComparer.Ordinal);
        foreach (var d in Definitions)
        {
            available[d.Name] = d.Available;
        }
        return PolicyFunctions.DefaultGeneratorOptions(
            name => available.TryGetValue(name, out var fact) ? fact : Unknown<bool>(),
            ResourceStock,
            renewable,
            PowerSources,
            GeneratorRows);
    }
}

public sealed record FarmZoneFact
{
    public string Id { get; init; } = "";

    public string Crop { get; init; } = "";

    public Fact<uint> UsableCells { get; init; }
}

public sealed record CookingBench
{
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public Fact<bool> Usable { get; init; }

    /// <summary>The native room census identity the bench stands in, unknown for a bench outdoors or when the native read left it out.</summary>
    public Fact<string> Room { get; init; }

    /// <summary>A refuelable bench's auto-refuel toggle, unknown for a bench without one.</summary>
    public Fact<bool> AutoRefuel { get; init; }
}
