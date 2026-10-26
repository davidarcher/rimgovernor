using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class GeneratorCandidatePipelineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(24)]
    [InlineData(96)]
    public void InstalledGeneratorsReachFactsComparisonAndBuildingIntent(int count)
    {
        var research = new ResearchProjectDef { defName = "InstalledGeneratorResearch" };
        var generators = Enumerable.Range(0, count).Select(i => Generator(
            "InstalledGenerator" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture), typeof(CompPowerPlant))).ToArray();
        var locked = Generator("LockedGenerator", typeof(CompPowerPlant));
        locked.researchPrerequisites = [research];
        var unfamiliar = Generator("UnmodeledGenerator", typeof(CompPowerPlantGravcore));
        ThingDef[] all = [.. generators.Reverse(), locked, unfamiliar];
        var subjects = new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost>();
        var values = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value>();
        foreach (var generator in all)
        {
            subjects[(generator, null)] = new([new(nameof(ThingDefOf.Steel), generator == locked ? 1 : 10)], null);
            values[(generator, null, CatalogStats.WorkToBuild)] = new(100, true, null);
        }
        var catalog = new DefinitionCatalog([.. all, new TerrainDef { defName = "NaturalSoil", natural = true },
            new ThingDef { defName = nameof(ThingDefOf.Silver) }],
            new(subjects, values, new Dictionary<BuildableDef, IReadOnlyList<string>>(), new HashSet<CatalogStat>(CatalogStats.All)),
            new(nameof(ThingDefOf.Silver), "Wort", 10f));
        var names = catalog.GeneratorDefinitions();
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
        var proposal = PowerRules.SelectPowerMethod(new() { Buildings = [consumer] }, new(20, 20), [], [],
            new() { ComponentSteel = new InputCost.NoRecipe(), Battery = new(600, .5), Generators = options, Sources = facts.PowerSources });
        Assert.Equal(PowerMethod.Generate, proposal.Method);
        Assert.Equal(generators[0].defName, proposal.Definition);
        Assert.Equal(count - 1, proposal.Alternatives.Count);
        var action = DomainAction.NewBuildingAction(new("generator"), new(proposal.Definition, new(4, 5), Rotation.North, ""), ConstructionTier.Sustain);
        var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(IntentActionRules.IntentAction("generator", action).Intent).Value;
        Assert.Equal(generators[0].defName, intent.Placement!.DefName);
        Assert.True(ReconcileRules.CarriedOver([], ComfortTests.NoComfortObservation, new ItemFacts(), facts.GeneratorRows.Keys)(proposal.Definition));
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
            new Dictionary<string, GeneratorRow> { ["InstalledGenerator"] = new() { NominalW = 1000, Fuels = [scarce, renewable], FuelCapacity = 20 } });
        var option = Assert.Single(options);
        Assert.Equal(renewable, option.Fuel);
        Assert.Equal(100, option.FuelStock);
        Assert.True(option.FuelRenewable);
    }

    private static ThingDef Generator(string name, Type kind)
    {
        var power = new CompProperties_Power { compClass = kind };
        GameMembers.BasePowerConsumption(power) = -1000;
        return new() { defName = name, thingClass = typeof(Building), designationCategory = new(), comps = [power] };
    }
}
