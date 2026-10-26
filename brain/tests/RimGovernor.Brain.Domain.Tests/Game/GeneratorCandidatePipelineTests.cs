using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using System.Reflection;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class GeneratorCandidatePipelineTests(ITestOutputHelper output)
{
#pragma warning disable CA1852 // DispatchProxy derives its implementation.
    private class PlacementNative : DispatchProxy
    {
        internal List<Building> Calls { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IReviewBuildingSource.PreviewBuildingAsync)) throw new NotSupportedException(targetMethod?.Name);
            var action = (DomainAction)args![0]!;
            var snapshot = (GenerationSnapshot)args[1]!;
            var building = action.Building!;
            Calls.Add(building);
            var source = PreviewTestFacts.Source(building.Cell);
            source = source with { Observed = source.Observed with { MadeFromStuff = building.Stuff.Length > 0 } };
            return Task.FromResult(new BuildingPreview
            {
                Preview = new() { Action = action, Snapshot = snapshot, CanPlace = Calls.Count > 1, SafeToPlace = true,
                    Source = source, Footprint = [building.Cell], Costs = [] },
                Stock = new() { Snapshot = snapshot, Tick = new(0) },
            });
        }
    }
#pragma warning restore CA1852

    [Theory]
    [InlineData(24, false, false)]
    [InlineData(96, false, false)]
    [InlineData(24, true, false)]
    [InlineData(96, true, false)]
    [InlineData(24, false, true)]
    [InlineData(24, true, true)]
    public async Task InstalledGeneratorsReachFactsComparisonAndBuildingIntent(int count, bool steam, bool stuffed)
    {
        var research = new ResearchProjectDef { defName = "InstalledGeneratorResearch" };
        var kind = steam ? typeof(CompPowerPlantSteam) : typeof(CompPowerPlant);
        var generators = Enumerable.Range(0, count).Select(i => Generator(
            "InstalledGenerator" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), kind)).ToArray();
        var locked = Generator("LockedGenerator", kind);
        locked.researchPrerequisites = [research];
        var unfamiliar = Generator("UnmodeledGenerator", typeof(CompPowerPlantGravcore));
        ThingDef[] all = [.. generators.Reverse(), locked, unfamiliar];
        var steel = new ThingDef { defName = nameof(ThingDefOf.Steel), stuffProps = new() { commonality = 1 } };
        var exotic = new ThingDef { defName = "InstalledAlloy", stuffProps = new() { commonality = 1 } };
        var stuffs = new Dictionary<BuildableDef, IReadOnlyList<string>>();
        var subjects = new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>
        {
            [(steel, null)] = new([], null), [(exotic, null)] = new([], null),
        };
        var values = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>
        {
            [(steel, null, CatalogStats.MarketValue)] = new(2, true, null),
            [(exotic, null, CatalogStats.MarketValue)] = new(1, true, null),
        };
        foreach (var generator in all)
        {
            if (stuffed && generators.Contains(generator))
            {
                generator.stuffCategories = [new StuffCategoryDef()];
                stuffs[generator] = [steel.defName, exotic.defName];
                subjects[(generator, steel)] = new([new(steel.defName, generator == generators[^2] ? 5 : 20)], null);
                subjects[(generator, exotic)] = new([new(exotic.defName, generator == generators[^1] ? 1 : 100)], null);
                values[(generator, steel, CatalogStats.WorkToBuild)] = new(100, true, null);
                values[(generator, exotic, CatalogStats.WorkToBuild)] = new(100, true, null);
            }
            else
            {
                var resource = generator == generators[^1] ? exotic : steel;
                subjects[(generator, null)] = new([new(resource.defName, generator == generators[^1] || generator == locked ? 1 : generator == generators[^2] ? 5 : 20)], null);
                values[(generator, null, CatalogStats.WorkToBuild)] = new(100, true, null);
            }
        }
        var catalog = new DefinitionCatalog([.. all, steel, exotic, new TerrainDef { defName = "NaturalSoil", natural = true },
            new ThingDef { defName = nameof(ThingDefOf.Silver) }],
            new(subjects, values, stuffs, new HashSet<CatalogStat>(CatalogStats.All)),
            new(nameof(ThingDefOf.Silver), "Wort", 10f));        var names = catalog.GeneratorDefinitions();
        Assert.Equal(count + 1, names.Count);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.DoesNotContain(unfamiliar.defName, names);
        var unsupported = catalog.PowerSources()[unfamiliar.defName];
        Assert.Contains(nameof(CompPowerPlantGravcore), unsupported.UnsupportedReason, StringComparison.Ordinal);
        Assert.Throws<DomainException>(() => PowerRules.ComputePowerBudget(new() { Producers = [new() { NominalW = 1000, Profile = unsupported }] }));

        var definitions = new DefinitionFacts { Catalog = catalog, Finished = new HashSet<string>(),
            Crops = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.EdibleCrop>() }.AppendDefinitions([], names);
        var facts = ColonyFactsCoreTests.Reading().Facts with { Definitions = definitions,
            PowerSources = catalog.PowerSources(), GeneratorRows = names.ToDictionary(name => name, name => catalog.GeneratorRow(name, 1), StringComparer.Ordinal) };
        var options = ColonyPlanningRules.GeneratorOptions(facts, new Dictionary<Resource, long>(), _ => false);
        var consumer = PowerBuildingFixtures.Trader("consumer", "InstalledConsumer", new(2, 3), -100, 0, false);
        var planning = new PowerPlanning { Battery = new(600, .5), Generators = options, Sources = facts.PowerSources };
        var topology = new PowerTopology { Buildings = [consumer], Geysers = steam ? [new() { Id = "geyser", Cell = new(3, 3), Cells = [new(3, 3)], Occupied = false }] : [] };
        var proposal = PowerRules.SelectPowerMethod(topology, new(20, 20), [], [], planning);
        Assert.Equal(PowerMethod.Generate, proposal.Method);
        Assert.Equal(generators[^1].defName, proposal.Definition);
        Assert.Equal(1, options.Single(option => option.Definition == proposal.Definition).BuildValue);
        Assert.Equal(count - 1, proposal.Alternatives.Count);
        Assert.Equal(steam, proposal.FixedSite());
        if (steam)
        {
            Assert.All(options, option => Assert.True(option.Profile.Steam));
            Assert.Equal(new Cell(3, 3), proposal.Center);
            Assert.Equal(PowerMethod.NoGenerator, PowerRules.SelectPowerMethod(topology with { Geysers = [] }, new(20, 20), [], [], planning).Method);
            Assert.Equal(PowerMethod.NoGenerator, PowerRules.SelectPowerMethod(topology with { Geysers = [topology.Geysers[0] with { Occupied = true }] }, new(20, 20), [], [], planning).Method);
        }
        var action = DomainAction.NewBuildingAction(new("generator"), new(proposal.Definition, steam ? proposal.Center : new(4, 5), Rotation.North, facts.GeneratorRows[proposal.Definition].Stuff), ConstructionTier.Sustain);
        var selected = generators[^1].defName;
        if (steam)
        {
            Assert.Equal(BuiltPlacer.Geothermal, GoalRules.PlacerOf(selected, facts.PowerSources));
            var native = DispatchProxy.Create<IReviewBuildingSource, PlacementNative>();
            var planner = new BuildingPlanner { Native = native, Concern = PowerFamily.Basic.FilingKey, Need = PowerFamily.Basic, Definition = selected, Stuff = facts.GeneratorRows[selected].Stuff, Power = proposal };
            var reading = ColonyFactsCoreTests.Reading();
            reading = reading with { Facts = facts };
            var snapshot = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
            var (previews, _, verdict) = await planner.PreviewMethodAsync(snapshot, reading, [], 1, () => { }, TestContext.Current.CancellationToken);
            Assert.True(verdict.IsZero, verdict.ToString());
            Assert.NotNull(previews);
            action = Assert.Single(previews).Action;
            selected = generators[^2].defName;
            Assert.Equal([proposal.Center, proposal.Center], ((PlacementNative)(object)native).Calls.Select(call => call.Cell));
        }
        var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(IntentActionRules.IntentAction("generator", action).Intent).Value;
        Assert.Equal(selected, intent.Placement!.DefName);
        Assert.Equal(stuffed ? (steam ? steel.defName : exotic.defName) : "", intent.Placement.Stuff);
        Assert.True(ReconcileRules.CarriedOver([], ComfortTests.NoComfortObservation, new ItemFacts(), facts.GeneratorRows.Keys, [])(selected));
        output.WriteLine($"Supported generator candidates: {names.Count}; planning rows: {definitions.Count}; ranked available: {proposal.Alternatives.Count + 1}");
    }

    [Fact]
    public void FuelTankStockUsesTheObservedDifficultyAndNativeItemMultiplier()
    {
        var generator = Generator("InstalledFuelPlant", typeof(CompPowerPlant));
        var fuel = new ThingDef { defName = "InstalledFuel" };
        var filter = new ThingFilter();
        filter.SetAllow(fuel, true);
        var refuel = new CompProperties_Refuelable { fuelCapacity = 21, fuelFilter = filter, factorByDifficulty = true };
        GameMembers.FuelMultiplier(refuel) = 2;
        generator.comps.Add(refuel);
        var catalog = new DefinitionCatalog([generator, fuel, new TerrainDef { defName = "NaturalSoil", natural = true },
            new ThingDef { defName = nameof(ThingDefOf.Silver) }],
            new(new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(generator, null)] = new([], null) },
                new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>(),
                new Dictionary<BuildableDef, IReadOnlyList<string>>(), new HashSet<CatalogStat>(CatalogStats.All)),
            new(nameof(ThingDefOf.Silver), "Wort", 10f));
        Assert.Equal(21, catalog.GeneratorRow(generator.defName, 2).FuelCapacity);
        Assert.Equal(6, catalog.GeneratorRow(generator.defName, .5).FuelCapacity);
        refuel.factorByDifficulty = false;
        Assert.Equal(11, catalog.GeneratorRow(generator.defName, 2).FuelCapacity);
        Assert.Throws<CatalogException>(() => catalog.GeneratorRow(generator.defName, double.NaN));
    }

    [Fact]
    public void MultipleAcceptedFuelsUseStockAndRenewabilityInsteadOfRejectingTheGenerator()
    {
        Resource scarce = new("ScarceFuel"), renewable = new("RenewableFuel");
        var options = PowerRules.DefaultGeneratorOptions(_ => true, fuel => fuel == renewable ? 100 : 0, fuel => fuel == renewable,
            new Dictionary<string, PowerSourceProfile> { ["InstalledGenerator"] = PowerRules.ConstantPowerProfile },
            new Dictionary<string, GeneratorRow> { ["InstalledGenerator"] = new() { NominalW = 1000, BuildValue = 10, Stuff = "", Fuels = [scarce, renewable], FuelCapacity = 20 } });
        var option = Assert.Single(options);
        Assert.Equal(renewable, option.Fuel);
        Assert.Equal(100, option.FuelStock);
        Assert.True(option.FuelRenewable);
    }

    private static ThingDef Generator(string name, Type kind)
    {
        var power = new CompProperties_Power { compClass = kind };
        GameMembers.BasePowerConsumption(power) = -1000;
        return new() { defName = name, thingClass = typeof(Verse.Building), designationCategory = new(), comps = [power] };
    }
}
