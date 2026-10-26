using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class TemperatureCandidatePipelineTests(ITestOutputHelper output)
{
#pragma warning disable CA1852 // DispatchProxy derives an implementation.
    private class PlacementNative : DispatchProxy
    {
        internal List<Building> Calls { get; } = [];
        internal string RejectedDefinition { get; set; } = "";
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IReviewBuildingSource.PreviewBuildingAsync)) throw new NotSupportedException(targetMethod?.Name);
            var action = (DomainAction)args![0]!;
            var snapshot = (GenerationSnapshot)args[1]!;
            var building = action.Building!;
            Calls.Add(building);
            return Task.FromResult(new BuildingPreview
            {
                Preview = new() { Action = action, Snapshot = snapshot, CanPlace = RejectedDefinition.Length == 0 ? Calls.Count > 1 : building.Definition != RejectedDefinition, SafeToPlace = true,
                    Source = PreviewTestFacts.Source(building.Cell), Footprint = [building.Cell], Costs = [] },
                Stock = new() { Snapshot = snapshot, Tick = new(0) },
            });
        }
    }
#pragma warning restore CA1852

    [Fact]
    public async Task ARejectedPoweredCoolerYieldsToALegalPassiveCandidate()
    {
        var wall = ClimateStatesTests.Thermal("wall-cooler", -21, true) with { Wall = true };
        var passive = ClimateStatesTests.Thermal("passive-cooler", -11);
        var definitions = new[] { ClimateStatesTests.Definition(wall.Name), ClimateStatesTests.Definition(passive.Name) };
        var cells = new[]
        {
            SiteCellFixtures.At(new(1, 2), walkable: false, things: [new RimGovernor.Brain.Policy.Thing { Def = "wall", Flags = ThingMarks.Edifice }]), SiteCellFixtures.At(new(1, 3)),
            SiteCellFixtures.At(new(2, 1), walkable: false, things: [new RimGovernor.Brain.Policy.Thing { Def = "wall", Flags = ThingMarks.Edifice }]), SiteCellFixtures.At(new(3, 1)),
        };
        var cooling = ClimateStatesTests.Cooling(new ClimatePower.Census(new() { Buildings = [PowerBuildingFixtures.Trader("source", "source", new(9, 9), 1000, 1000, true)] })) with
        {
            Candidates = [passive, wall], Definitions = definitions, Cells = cells,
        };
        var rooms = RoomFixtures.Census((RoomFixtures.State("room", beds: ["bed"], temperatureC: 40), [new(1, 1)]));
        var proposal = TemperatureMethodRules.SelectTemperatureMethod(rooms, ["bed"], cooling, Rules.DefaultPlannerSettings());
        Assert.Equal(wall.Name, proposal.Definition);
        Assert.Equal(2, proposal.WallSites.Count);
        Assert.All(proposal.WallSites, site => Assert.Equal(new Cell(1, 1), RefrigerationRules.ColdSide(site.Cell, site.Rotation)));
        var native = DispatchProxy.Create<IReviewBuildingSource, PlacementNative>();
        ((PlacementNative)(object)native).RejectedDefinition = wall.Name;
        var reading = ColonyFactsCoreTests.Reading();
        reading = reading with { Facts = reading.Facts with { Definitions = definitions } };
        var planner = new BuildingPlanner { Native = native, Concern = ShelterFamily.Temperature.FilingKey, Need = ShelterFamily.Temperature, Temperature = proposal, Definition = proposal.Definition };
        var snapshot = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        var (previews, _, verdict) = await planner.PreviewTemperatureAsync(snapshot, reading, [], () => { }, TestContext.Current.CancellationToken);
        Assert.True(verdict.IsZero, verdict.ToString());
        Assert.NotNull(previews);
        var preview = Assert.Single(previews);
        Assert.Equal(3, ((PlacementNative)(object)native).Calls.Count);
        var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(IntentActionRules.IntentAction("cool", preview.Action).Intent).Value;
        Assert.Equal(passive.Name, intent.Placement!.DefName);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(96)]
    public async Task InstalledHeatersReachDefinitionFactsPolicyLegalPreviewAndIntent(int count)
    {
        var steel = new ThingDef { defName = nameof(ThingDefOf.Steel) };
        var heaters = Enumerable.Range(0, count).Select(i => Heater("InstalledHeater" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var locked = Heater("LockedHeater");
        locked.researchPrerequisites = [new ResearchProjectDef { defName = "Research" }];
        ThingDef[] all = [locked, .. heaters.Reverse()];
        var costs = new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(steel, null)] = new([], null) };
        var stats = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value> { [(steel, null, CatalogStats.MarketValue)] = new(1, true, null) };
        foreach (var heater in all)
        {
            costs[(heater, null)] = new([new(steel.defName, heater == locked ? 1 : 10)], null);
            stats[(heater, null, CatalogStats.WorkToBuild)] = new(100, true, null);
        }
        var catalog = new DefinitionCatalog([.. all, steel, new TerrainDef { defName = "NaturalSoil", natural = true }, new ThingDef { defName = nameof(ThingDefOf.Silver) }],
            new(costs, stats, all.ToDictionary(heater => (BuildableDef)heater, _ => (IReadOnlyList<string>)[]), new HashSet<CatalogStat>(CatalogStats.All)), new(nameof(ThingDefOf.Silver), "Wort", 10f));
        var candidates = catalog.TemperatureDefinitions();
        Assert.Equal(count + 1, candidates.Count);
        var definitions = new DefinitionFacts { Catalog = catalog, Finished = new HashSet<string>(), Crops = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.EdibleCrop>() }
            .AppendDefinitions([], candidates.Select(candidate => candidate.Name));
        var supply = PowerBuildingFixtures.Trader("generator", "source", new(10, 10), 1000, 1000, true);
        var cooling = ClimateStatesTests.Cooling(new ClimatePower.Census(new() { Buildings = [supply] })) with
        {
            Candidates = candidates, Definitions = definitions, Stock = new Dictionary<Resource, long> { [new(steel.defName)] = 100 },
        };
        var roomCells = Enumerable.Range(2, 8).SelectMany(x => Enumerable.Range(2, 6).Select(z => new Cell(x, z))).ToArray();
        var rooms = RoomFixtures.Census((RoomFixtures.State("room", beds: ["bed"], temperatureC: 2), roomCells));
        var proposal = TemperatureMethodRules.SelectTemperatureMethod(rooms, ["bed"], cooling, Rules.DefaultPlannerSettings());
        Assert.Equal(TemperatureMethod.HeatElectric, proposal.Method);
        Assert.Equal(heaters[0].defName, proposal.Definition);
        Assert.Equal(count, proposal.Options.Count);
        Assert.Equal(TemperatureMethod.Wait, TemperatureMethodRules.SelectTemperatureMethod(rooms, ["bed"], cooling with { Builders = [] }, Rules.DefaultPlannerSettings()).Method);
        Assert.Equal(TemperatureMethod.Wait, TemperatureMethodRules.SelectTemperatureMethod(rooms, ["bed"], cooling with { Stock = new Dictionary<Resource, long>() }, Rules.DefaultPlannerSettings()).Method);
        var native = DispatchProxy.Create<IReviewBuildingSource, PlacementNative>();
        var reading = ColonyFactsCoreTests.Reading();
        reading = reading with { Facts = reading.Facts with { Definitions = definitions } };
        var planner = new BuildingPlanner { Native = native, Concern = ShelterFamily.Temperature.FilingKey, Need = ShelterFamily.Temperature, Temperature = proposal, Definition = proposal.Definition };
        var snapshot = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        var (previews, _, verdict) = await planner.PreviewTemperatureAsync(snapshot, reading, [], () => { }, TestContext.Current.CancellationToken);
        Assert.True(verdict.IsZero, verdict.ToString());
        Assert.NotNull(previews);
        var preview = Assert.Single(previews);
        Assert.Equal(2, ((PlacementNative)(object)native).Calls.Count);
        var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(IntentActionRules.IntentAction("heat", preview.Action).Intent).Value;
        Assert.Equal(proposal.Definition, intent.Placement!.DefName);
        output.WriteLine($"Thermal definitions: {candidates.Count}; available candidates: {proposal.Options.Count}; room cells: {roomCells.Length}; maximum preview comparisons: {proposal.Options.Count * roomCells.Length}");
    }

    private static ThingDef Heater(string name)
    {
        var power = new CompProperties_Power { compClass = typeof(CompPowerTrader) };
        GameMembers.BasePowerConsumption(power) = 175;
        return new() { defName = name, thingClass = typeof(Building_Heater), category = Verse.ThingCategory.Building, size = IntVec2.One,
            designationCategory = new(), comps = [power, new CompProperties_TempControl { energyPerSecond = 21 }] };
    }
}
