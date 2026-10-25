namespace RimGovernor.Brain.Policy;

/// <summary>Reviewed planning values owned by the existing family review, alongside canonical native frame references.</summary>
public sealed partial record RoundsFacts
{
    private IReadOnlyList<Book>? books;
    private IReadOnlyList<Food>? foods;
    private IReadOnlyList<FoodEaterDiet>? foodEaters;
    private FoodSupply? foodSupply;
    private FoodSupply? combinedFoodSupply;

    public IReadOnlyList<Book> Books { get => books ?? throw new RequiredFactException("book planning definitions"); init => books = value; }
    public IReadOnlyList<Food> Foods { get => foods ?? throw new RequiredFactException("food planning definitions"); init => foods = value; }
    public IReadOnlyList<FoodEaterDiet> FoodEaters { get => foodEaters ?? throw new RequiredFactException("food eater diet views"); init => foodEaters = value; }
    public FoodSupply FoodSupply { get => foodSupply ?? throw new RequiredFactException("human food supply review"); init => foodSupply = value; }
    public FoodSupply CombinedFoodSupply { get => combinedFoodSupply ?? throw new RequiredFactException("combined food supply review"); init => combinedFoodSupply = value; }

    public IsolationPlanning Isolation { get; init; } = new();
    public PieceShapes Shapes { get; init; } = new();
    public StationCatalog Stations { get; init; } = new() { PlayBuildings = [] };
    public EquatableArray<FoodField> FoodFields { get; init; }
    public FoodChannelPlanning FoodChannels { get; init; } = new();
    public CropClimate CropClimate => new() { Census = Census, OutdoorsDark = OutdoorsDark };
    public double FullRotRateC { get; init; }
    public EquatableArray<AcquisitionSource> Acquisition { get; init; }
    public IReadOnlyList<HuntHold> HuntHolds { get; init; } = [];
    public RestSchedule? Rest { get; init; }
    public MechCatalog MechCatalog { get; init; } = new();
    public MechFleet Mechs { get; init; } = new();
    public EquatableArray<FieldCrop> FieldCrops { get; init; }
    public EquatableArray<FieldCrop> FieldCapacityCrops { get; init; }
    public IReadOnlyDictionary<string, PowerSourceProfile> PowerSources { get; init; } = new Dictionary<string, PowerSourceProfile>();
    public PowerBattery PowerBattery { get; init; }
    public IReadOnlyDictionary<string, GeneratorRow> GeneratorRows { get; init; } = new Dictionary<string, GeneratorRow>();
    public InputCost? ComponentSteel { get; init; }
    public LightTerrains LightTerrains { get; init; } = new();
    public double RoofSupport { get; init; }
    public ImpressivenessLevels Impressiveness { get; init; } = new();
    public IReadOnlySet<string> Packable { get; init; } = new HashSet<string>();
    public LayoutPlan? LayoutPlan { get; init; }
    public RingStuffSources? RingSources { get; init; }
    public IReadOnlyList<PlanningDefinition> Definitions { get; init; } = [];
}