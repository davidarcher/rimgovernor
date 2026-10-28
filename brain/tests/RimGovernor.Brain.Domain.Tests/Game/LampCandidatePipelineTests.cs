using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using System.Reflection;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class LampCandidatePipelineTests(ITestOutputHelper output)
{
#pragma warning disable CA1852 // DispatchProxy derives its implementation from this class.
    private class PlacementNative : DispatchProxy
    {
        internal List<Building> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IReviewBuildingSource.PreviewBuildingAsync))
                throw new NotSupportedException(targetMethod?.Name);
            var action = (DomainAction)args![0]!;
            var snapshot = (GenerationSnapshot)args[1]!;
            var building = action.Building!;
            Calls.Add(building);
            return Task.FromResult(new BuildingPreview
            {
                Preview = new() { Action = action, Snapshot = snapshot, CanPlace = building.Definition != "InstalledWallFixture", SafeToPlace = true,
                    Source = PreviewTestFacts.Source(building.Cell), Footprint = [building.Cell], Costs = [] },
                Stock = new() { Snapshot = snapshot, Tick = new(0) },
            });
        }
    }
#pragma warning restore CA1852

    [Fact]
    public async Task ARejectedWallFixtureYieldsToALegalStandingFixtureThroughOrdinaryPreviewAndIntent()
    {
        var native = DispatchProxy.Create<IReviewBuildingSource, PlacementNative>();
        LampDefinition wall = new() { Name = "InstalledWallFixture", Wall = true }, standing = new() { Name = "InstalledStandingFixture" };
        var facts = ColonyFactsCoreTests.Reading();
        facts = facts with { Facts = facts.Facts with { Definitions = [new() { Name = wall.Name, Available = true }, new() { Name = standing.Name, Available = true }] } };
        var snapshot = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        var planner = new BuildingPlanner { Native = native, Concern = RoomsFamily.Lighting.FilingKey, Need = RoomsFamily.Lighting,
            Definition = wall.Name, Lighting = new() { Method = LightingMethod.Build, Definition = wall.Name, Options = [wall, standing], Cells = [new(3, 3)] } };
        var (previews, _, verdict) = await planner.PreviewLightingAsync(snapshot, facts, [], () => { }, TestContext.Current.CancellationToken);
        Assert.True(verdict.IsZero, verdict.ToString());
        var preview = Assert.Single(previews);
        var calls = ((PlacementNative)(object)native).Calls;
        Assert.Equal(5, calls.Count);
        Assert.Equal(Enum.GetValues<Rotation>(), calls.Take(4).Select(building => building.Rotation));
        Assert.Equal(standing.Name, preview.Action.Building!.Definition);
        var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(IntentActionRules.IntentAction("lamp", preview.Action).Intent).Value;
        Assert.Equal(standing.Name, intent.Placement!.DefName);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(96)]
    public void InstalledFixturesReachLayoutPricingRuntimeSelectionAndBuildingIntent(int count)
    {
        var steel = new ThingDef { defName = nameof(ThingDefOf.Steel) };
        var lamps = Enumerable.Range(0, count).Select(i => Fixture("InstalledLamp" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var wall = Fixture("InstalledWallFixture");
        wall.placeWorkers = [typeof(Placeworker_AttachedToWall)];
        var locked = Fixture("LockedFixture");
        locked.researchPrerequisites = [new ResearchProjectDef { defName = "FixtureResearch" }];
        ThingDef[] all = [.. lamps.Reverse(), wall, locked];
        var costs = new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(steel, null)] = new([], null) };
        var stats = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value> { [(steel, null, CatalogStats.MarketValue)] = new(1, true, null) };
        foreach (var lamp in all)
        {
            costs[(lamp, null)] = new([new(steel.defName, lamp == locked ? 1 : lamp == wall ? 5 : 10)], null);
            stats[(lamp, null, CatalogStats.WorkToBuild)] = new(100, true, null);
        }
        var catalog = new DefinitionCatalog([.. all, steel, new TerrainDef { defName = "NaturalSoil", natural = true }, new ThingDef { defName = nameof(ThingDefOf.Silver) }],
            new(costs, stats, all.ToDictionary(lamp => (BuildableDef)lamp, _ => (IReadOnlyList<string>)[]), new HashSet<CatalogStat>(CatalogStats.All)), new(nameof(ThingDefOf.Silver), "Wort", 10f));
        var candidates = catalog.LampDefinitions();
        Assert.Equal(count + 2, candidates.Count);
        Assert.Equal(candidates.Select(lamp => lamp.Name).Order(StringComparer.Ordinal), candidates.Select(lamp => lamp.Name));
        var definitions = new DefinitionFacts { Catalog = catalog, Finished = new HashSet<string>(), Crops = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.EdibleCrop>() }
            .AppendDefinitions([], candidates.Select(lamp => lamp.Name));
        var facts = new RimGovernor.Brain.Policy.LightingFacts { Rooms = RoomTestFacts.NoRooms, Candidates = candidates,
            Available = definitions.ToDictionary(def => def.Name, def => def.Available, StringComparer.Ordinal), PoweredSource = true,
            Builders = [PawnFixtures.Worker("builder") with { Work = PawnFixtures.WorkTable(true, [new() { Observed = new() { DefName = "Construction", Priority = 1, Disabled = false } }]) }],
            Stock = new Dictionary<Resource, long> { [new(steel.defName)] = 100 }, Cells = [SiteCellFixtures.At(new(3, 3))] };
        var observation = new LightingObservation { Census = LightingTests.NoLighting with { WorkCells = [new("bench", new(2, 3), .1, true, [])] }, DiesToLight = [], Lamps = [] };
        var policy = LightingPolicy.Default();
        var review = LightingRules.ReviewLighting(observation, policy, false);
        var proposal = LightingRules.SelectLightingMethod(review, observation, facts, policy);
        Assert.Equal(LightingMethod.Build, proposal.Method);
        Assert.Equal(wall.defName, proposal.Definition);
        Assert.True(proposal.Options[0].Wall);
        Assert.Equal(LightingMethod.BuilderNeeded, LightingRules.SelectLightingMethod(review, observation, facts with { Builders = [] }, policy).Method);
        var skilledOnly = candidates.Select(lamp => lamp.Name == wall.defName ? lamp with { ConstructionSkill = 20 } : lamp).ToArray();
        Assert.Equal(lamps[0].defName, LightingRules.SelectLightingMethod(review, observation, facts with { Candidates = skilledOnly }, policy).Definition);
        var styled = LightingRules.SelectLightingMethod(review, observation, facts with { Styled = lamps[0].defName }, policy);
        Assert.Equal(lamps[0].defName, styled.Definition);
        Assert.Equal(LightingMethod.MaterialsNeeded, LightingRules.SelectLightingMethod(review, observation, facts with { Stock = new Dictionary<Resource, long>() }, policy).Method);
        var plannedLocked = facts with { Plan = new() { Lamps = new Dictionary<int, IReadOnlyList<PlannedLamp>> { [1] = [new() { Def = locked.defName, Cell = new(3, 3) }] } } };
        Assert.Equal(LightingMethod.ResearchNeeded, LightingRules.SelectLightingMethod(new() { Active = true, Planned = [1] }, observation,
            plannedLocked, policy).Method);
        var price = LayoutLightRules.NewLightPrice(new() { Model = new() { Lamps = candidates }, Climate = new() { TicksPerSecond = 60 }, SteelValue = 1, UpkeepPerDay = 2, UnitNutrition = 1 });
        Assert.True(price.Valid());
        Assert.Equal(count + 1, price.Lamps.Count);
        var floor = Enumerable.Range(3, 8).SelectMany(x => Enumerable.Range(3, 6).Select(z => new Cell(x, z))).ToArray();
        var empty = new HashSet<Cell>();
        var placements = price.RoomCands(floor, empty, empty, empty, cell => cell.X == 2);
        var selected = Assert.Single(LayoutLightRules.PlanLamps([.. floor.Select(cell => new DarkCell(cell, 1e6))], placements));
        Assert.Equal(wall.defName, selected.Lamp.Def);
        var action = DomainAction.NewBuildingAction(new("lamp"), new(proposal.Definition, Assert.Single(proposal.Cells), Rotation.North, ""), ConstructionTier.Comfort);
        var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(IntentActionRules.IntentAction("lamp", action).Intent).Value;
        Assert.Equal(wall.defName, intent.Placement!.DefName);
        output.WriteLine($"Fixture candidates: {candidates.Count}; available layout rows: {price.Lamps.Count}; room cells: {floor.Length}; placement comparisons: {placements.Count}");
    }

    private static ThingDef Fixture(string name)
    {
        var power = new CompProperties_Power { compClass = typeof(CompPowerTrader) };
        GameMembers.BasePowerConsumption(power) = 20;
        return new() { defName = name, thingClass = typeof(Verse.Building), category = Verse.ThingCategory.Building, size = IntVec2.One,
            designationCategory = new(), comps = [power, new CompProperties_Glower { glowRadius = 12, glowColor = new ColorInt(217, 217, 217) }] };
    }

    [Fact]
    public void HiddenMaterialValueStillPricesLampsWithoutBecomingTradeVisibilityOrZero()
    {
        var ingredient = new ThingDef { defName = "HiddenValueIngredient" };
        var lamp = Fixture("LampWithHiddenValueIngredient");
        var costs = new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>
        {
            [(ingredient, null)] = new([], null), [(lamp, null)] = new([new(ingredient.defName, 2)], null),
        };
        var values = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>
        {
            [(ingredient, null, CatalogStats.MarketValue)] = new(37, false, null),
        };
        var catalog = new DefinitionCatalog([lamp, ingredient, new TerrainDef { defName = "NaturalSoil", natural = true },
                new ThingDef { defName = nameof(ThingDefOf.Silver) }],
            new(costs, values, new Dictionary<BuildableDef, IReadOnlyList<string>> { [lamp] = [] }, new HashSet<CatalogStat>(CatalogStats.All)),
            new(nameof(ThingDefOf.Silver), "Wort", 10f));
        Assert.True(CatalogStats.MarketValue.ReadHidden);
        Assert.Equal(74, catalog.CheapestCostValue(lamp.defName));
        Assert.Equal(74, Assert.Single(catalog.LampDefinitions()).Model.Build);
        Assert.False(catalog.ShownStatValue(ingredient.defName, "", CatalogStats.MarketValue).Shown);
        values[(ingredient, null, CatalogStats.MarketValue)] = new(float.NaN, false, null);
        Assert.Throws<CatalogException>(() => catalog.CheapestCostValue(lamp.defName));
    }
}
