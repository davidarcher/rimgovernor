using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The stuff an ordinary placement is built from is one the colony supplies: a naked start with a few logs and
/// trees around builds its shelf from wood, never from the granite blocks nobody cuts.
/// </summary>
public sealed class BuildStuffSupplyTests
{
    private const string Shelf = "Shelf";
    private const string Wood = "WoodLog";
    private const string Granite = "BlocksGranite";

    private static StuffOption Option(string stuff, double value, double hitPoints, double flammability) => new()
    {
        Stuff = stuff,
        Value = value,
        Common = true,
        Costs = [new Amount(new Resource(stuff), 30)],
        Stats = new Dictionary<string, double>(StringComparer.Ordinal) { [StatDefNames.StatMaxHitPoints] = hitPoints, [StatDefNames.StatFlammability] = flammability },
    };

    /// <summary>Granite blocks are cheaper per unit, tougher and fireproof: ranked blind to supply they always win.</summary>
    private static ReviewFacts Facts(double standingWood) => ColonyFactsCoreTests.Reading().Facts with
    {
        Definitions = [new PlanningDefinition { Name = Shelf, Available = true, Stuffed = true, StuffOptions = [Option(Wood, 1.2, 100, 1), Option(Granite, 0.9, 170, 0)] }],
        Acquisition = standingWood <= 0 ? [] : [new AcquisitionSource
        {
            Tree = true,
            Observed = new() { Source = new() { Id = "tree" }, Token = "tree-cas", Resource = Wood, Yield = standingWood,
                Designation = new RimGovernor.Brain.GameFacts.Observations.AcquisitionDesignation.Open(), Taken = false,
                Kind = new RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Plant(true, 1, false) },
            Entity = new() { Id = "tree", DefName = "Plant_TreeOak", Label = "oak", Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, new(5, 5)) },
        }],
    };

    private static Dictionary<Resource, long> Stock(long wood = 0, long granite = 0)
    {
        var stock = new Dictionary<Resource, long>();
        if (wood > 0) stock[new Resource(Wood)] = wood;
        if (granite > 0) stock[new Resource(Granite)] = granite;
        return stock;
    }

    [Fact]
    public void AFewLogsWithTreesStandingBuildsTheShelfFromWood()
    {
        Assert.Equal(Wood, ColonyPlanningRules.BuildStuff(Facts(standingWood: 500), Stock(wood: 10), Shelf));
    }

    [Fact]
    public void AFewLogsAndNoTreesStillWaitsForWoodRatherThanBlocksNobodyCuts()
    {
        Assert.Equal(Wood, ColonyPlanningRules.BuildStuff(Facts(standingWood: 0), Stock(wood: 10), Shelf));
    }

    [Fact]
    public void NothingSuppliedNamesNoStuffSoThePlacementTakesTheGameDefault()
    {
        Assert.Equal("", ColonyPlanningRules.BuildStuff(Facts(standingWood: 0), Stock(), Shelf));
    }

    [Fact]
    public void GraniteIsChosenOnlyOnceTheColonyHoldsBlocks()
    {
        Assert.Equal(Granite, ColonyPlanningRules.BuildStuff(Facts(standingWood: 500), Stock(wood: 100, granite: 30), Shelf));
        Assert.Equal(Granite, ColonyPlanningRules.BuildStuff(Facts(standingWood: 0), Stock(granite: 5), Shelf));
        // Wood covers the build and blocks fall short of it: the covered stuff ranks ahead of the merely held one.
        Assert.Equal(Wood, ColonyPlanningRules.BuildStuff(Facts(standingWood: 0), Stock(wood: 100, granite: 5), Shelf));
    }

    [Fact]
    public void ARuinOrALooseStackOnThePlannedGroundSuppliesItsBlocks()
    {
        var ruin = Facts(standingWood: 0) with { RingSources = new RingStuffSources { Salvage = [Granite], Wood = [], Blocks = [], Mined = [], Items = [], Unsalvaged = [] } };
        Assert.Equal(Granite, ColonyPlanningRules.BuildStuff(ruin, Stock(), Shelf));
        var loose = Facts(standingWood: 0) with { RingSources = new RingStuffSources { Items = [Granite], Wood = [], Blocks = [], Mined = [], Salvage = [], Unsalvaged = [] } };
        Assert.Equal(Granite, ColonyPlanningRules.BuildStuff(loose, Stock(), Shelf));
        // Natural rock offers blocks only once a stonecutter exists: it is no supply here.
        var rock = Facts(standingWood: 0) with { RingSources = new RingStuffSources { Blocks = [Granite], Wood = [], Mined = [], Items = [], Salvage = [], Unsalvaged = [] } };
        Assert.Equal("", ColonyPlanningRules.BuildStuff(rock, Stock(), Shelf));
    }

    [Fact]
    public void ABulkRingWaitsOnlyForAStuffTheColonySupplies()
    {
        // Two hundred walls' worth of wood stands as trees: wood is covered and chosen; with neither covered, the held one.
        var wall = ColonyFactsCoreTests.Reading().Facts with
        {
            Definitions = [new PlanningDefinition { Name = "Wall", Available = true, Stuffed = true, StuffOptions = [Option(Wood, 1.2, 100, 1) with { Costs = [new Amount(new Resource(Wood), 5)] }, Option(Granite, 0.9, 170, 0) with { Costs = [new Amount(new Resource(Granite), 5)] }] }],
        };
        Assert.Equal(Wood, ColonyPlanningRules.BulkBuildStuff(wall with { Acquisition = Facts(1000).Acquisition }, Stock(wood: 1), "Wall", 200));
        Assert.Equal(Wood, ColonyPlanningRules.BulkBuildStuff(wall, Stock(wood: 1), "Wall", 200));
        Assert.Equal("", ColonyPlanningRules.BulkBuildStuff(wall, Stock(), "Wall", 200));
    }
}
