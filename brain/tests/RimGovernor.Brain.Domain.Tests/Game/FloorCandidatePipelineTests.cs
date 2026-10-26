using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class FloorCandidatePipelineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(24)]
    [InlineData(96)]
    public void InstalledFloorsReachPlanningSelectionAndTheOrdinaryBuildingIntent(int count)
    {
        var research = new ResearchProjectDef { defName = "InstalledFloorResearch" };
        var floors = Enumerable.Range(0, count).Select(i => new TerrainDef
        {
            defName = "InstalledFineFloor" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
            designationCategory = new DesignationCategoryDef(), tags = ["Fine"],
        }).ToArray();
        var locked = new TerrainDef { defName = "LockedFloor", designationCategory = new DesignationCategoryDef(), tags = ["Fine"], researchPrerequisites = [research] };
        TerrainDef[] all = [.. floors.Reverse(), locked];
        var subjects = new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>();
        var values = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>();
        foreach (var (terrain, index) in all.Select((terrain, index) => (terrain, index)))
        {
            subjects[(terrain, null)] = new([new(nameof(ThingDefOf.WoodLog), 1)], null);
            values[(terrain, null, CatalogStats.Cleanliness)] = new(0, true, null);
            values[(terrain, null, CatalogStats.Beauty)] = new(index, true, null);
            values[(terrain, null, CatalogStats.Flammability)] = new(0, true, null);
            values[(terrain, null, CatalogStats.WorkToBuild)] = new(100, true, null);
        }
        var catalog = new DefinitionCatalog([.. all, new TerrainDef { defName = "NaturalSoil", natural = true },
            new ThingDef { defName = nameof(ThingDefOf.Silver) }],
            new(subjects, values, new Dictionary<BuildableDef, IReadOnlyList<string>>(), new HashSet<CatalogStat>(CatalogStats.All)),
            new(nameof(ThingDefOf.Silver), "Wort", 10f));
        var names = catalog.BuildableFloors();
        var definitions = new DefinitionFacts { Catalog = catalog, Finished = new HashSet<string>(),
            Crops = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.EdibleCrop>() }.AppendDefinitions([], names);
        Assert.Equal(count + 1, names.Count);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Equal(names.Count, definitions.Count);
        Assert.False(Assert.Single(definitions, d => d.Name == locked.defName).Available);
        var colony = ColonyFactsCoreTests.Reading();
        colony = colony with { Facts = colony.Facts with { Definitions = definitions, Upkeep = colony.Facts.Upkeep with { Flooring = new() },
            Census = colony.Snapshot with { Resources = [new() { DefName = nameof(ThingDefOf.WoodLog), Units = 100 }] } } };
        var facts = ColonyPlanningRules.FlooringFacts(colony.Facts, []);
        var proposal = FlooringRules.SelectFlooringMethod(new() { Active = true,
            Deficits = [new() { Key = "throne", Tier = FloorTier.Throne, Tags = ["Fine"], Cells = [new(2, 3)] }] },
            facts, FlooringRules.DefaultFlooringPolicy());
        Assert.Equal(FlooringMethod.Build, proposal.Method);
        Assert.Equal(floors[0].defName, proposal.Definition);
        var action = DomainAction.NewBuildingAction(new("floor"), new(proposal.Definition, Assert.Single(proposal.Cells), Rotation.North, ""), ConstructionTier.Comfort);
        var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(IntentActionRules.IntentAction("floor", action).Intent).Value;
        Assert.Equal(floors[0].defName, intent.Placement!.DefName);
        output.WriteLine($"Buildable terrain candidates: {names.Count}; planning rows: {definitions.Count}; selected cells: {proposal.Cells.Count}");
    }
}
