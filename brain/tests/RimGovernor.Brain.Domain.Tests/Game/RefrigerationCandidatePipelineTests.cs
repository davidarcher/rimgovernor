using System.Reflection;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class RefrigerationCandidatePipelineTests(ITestOutputHelper output)
{
    public interface ITestNative : IReviewBuildingSource, IRefrigerationSource;

#pragma warning disable CA1852 // DispatchProxy derives its implementation.
    private class Native : DispatchProxy
    {
        internal List<Building> Placements { get; } = [];
        internal List<string> Reads { get; } = [];
        internal string Reject { get; set; } = "";
        internal RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot Buildings { get; set; } = new() { Context = ColonyFactsCoreTests.Snapshot().Context, Buildings = [] };
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(IRefrigerationSource.ReadConstructionBuildingsAsync))
            {
                Reads.AddRange((IReadOnlyList<string>)args![1]!);
                return Task.FromResult(Buildings);
            }
            if (method?.Name != nameof(IReviewBuildingSource.PreviewBuildingAsync)) throw new NotSupportedException(method?.Name);
            var action = (DomainAction)args![0]!;
            var snapshot = (GenerationSnapshot)args[1]!;
            var building = action.Building!;
            Placements.Add(building);
            return Task.FromResult(new BuildingPreview
            {
                Preview = new() { Action = action, Snapshot = snapshot, CanPlace = building.Definition != Reject, SafeToPlace = true,
                    Source = PreviewTestFacts.Source(building.Cell), Footprint = [building.Cell], Costs = [] },
                Stock = new() { Snapshot = snapshot, Tick = new(0) },
            });
        }
    }
#pragma warning restore CA1852

    private static ThingDef Cooler(string name)
    {
        var power = new CompProperties_Power { compClass = typeof(CompPowerTrader) };
        GameMembers.BasePowerConsumption(power) = 200;
        return new() { defName = name, thingClass = typeof(Building_Cooler), category = Verse.ThingCategory.Building, size = IntVec2.One,
            designationCategory = new(), comps = [power, new CompProperties_TempControl { energyPerSecond = -21 }] };
    }

    private static (ColonyReading Reading, IReadOnlyList<TemperatureDefinition> Candidates) Reading(int count)
    {
        var steel = new ThingDef { defName = nameof(ThingDefOf.Steel) };
        var coolers = Enumerable.Range(0, count).Select(i => Cooler($"InstalledCooler{i:D3}")).ToArray();
        var locked = Cooler("LockedCooler");
        locked.researchPrerequisites = [new ResearchProjectDef { defName = "CoolingResearch" }];
        ThingDef[] all = [locked, .. coolers.Reverse()];
        var costs = new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(steel, null)] = new([], null) };
        var stats = new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value> { [(steel, null, CatalogStats.MarketValue)] = new(1, true, null) };
        foreach (var cooler in all)
        {
            costs[(cooler, null)] = new([new(steel.defName, cooler == locked ? 1 : 10)], null);
            stats[(cooler, null, CatalogStats.WorkToBuild)] = new(100, true, null);
        }
        var catalog = new DefinitionCatalog([.. all, steel, new TerrainDef { defName = "NaturalSoil", natural = true }, new ThingDef { defName = nameof(ThingDefOf.Silver) }],
            new(costs, stats, all.ToDictionary(cooler => (BuildableDef)cooler, _ => (IReadOnlyList<string>)[]), new HashSet<CatalogStat>(CatalogStats.All)), new(nameof(ThingDefOf.Silver), "Wort", 10f));
        var candidates = catalog.TemperatureDefinitions();
        var definitions = new DefinitionFacts { Catalog = catalog, Finished = new HashSet<string>(), Crops = new Dictionary<string, RimGovernor.Brain.GameFacts.Observations.EdibleCrop>() }
            .AppendDefinitions([], candidates.Select(candidate => candidate.Name));
        var floor = Enumerable.Range(2, 8).SelectMany(x => Enumerable.Range(2, 6).Select(z => new Cell(x, z))).ToHashSet();
        var sites = new Dictionary<Cell, SiteCell>();
        foreach (var inside in floor)
        foreach (var direction in new[] { new Cell(1, 0), new Cell(-1, 0), new Cell(0, 1), new Cell(0, -1) })
        {
            var wall = new Cell(inside.X + direction.X, inside.Z + direction.Z);
            if (floor.Contains(wall)) continue;
            var outside = new Cell(wall.X + direction.X, wall.Z + direction.Z);
            sites[wall] = SiteCellFixtures.At(wall, walkable: false, things: [new RimGovernor.Brain.Policy.Thing { Def = "wall", Flags = ThingMarks.Edifice }]);
            sites[outside] = SiteCellFixtures.At(outside);
        }
        var reading = ColonyFactsCoreTests.Reading();
        return (reading with
        {
            Window = new Held<PlanningCells> { Value = new() { Cells = [.. sites.Values] } },
            Facts = reading.Facts with
            {
                Definitions = definitions, Stations = reading.Facts.Stations with { Temperature = candidates },
                Upkeep = reading.Facts.Upkeep with { Rooms = RoomFixtures.Census((RoomFixtures.State("room", temperatureC: 30), floor)) },
                EssentialInputs = reading.Facts.EssentialInputs with { Power = new() { Buildings = [] } },
                ForecastObserved = reading.Facts.ForecastObserved with { WorkPawns = new WorkPawnsRead([PawnFixtures.Worker("builder") with
                { Work = PawnFixtures.WorkTable(true, [new() { Observed = new() { DefName = "Construction", Priority = 1, Disabled = false } }]) }]) },
            },
        }, candidates);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(96)]
    public async Task NativeCatalogCoolersReachReadsComparisonLegalPreviewAndIntent(int count)
    {
        var (reading, candidates) = Reading(count);
        var source = DispatchProxy.Create<ITestNative, Native>();
        var native = (Native)(object)source;
        var ct = TestContext.Current.CancellationToken;
        var observed = await RefrigerationReads.RefrigerationFacts(source, reading, ct);
        Assert.Equal(count + 1, observed.Candidates.Count);
        Assert.Empty(native.Reads);
        var policy = FoodStorageUpkeepRules.DefaultFoodStoragePolicy();
        var review = new RefrigerationReview { Active = true, Rooms = ["room"] };
        var proposal = RefrigerationRules.SelectRefrigerationMethod(review, observed, policy, false);
        Assert.Equal(RefrigerationMethod.Build, proposal.Method);
        Assert.Equal(count, proposal.Options.Count);
        Assert.Equal("InstalledCooler000", proposal.Options[0].Name);
        Assert.All(proposal.WallSites, site => Assert.Contains(RefrigerationRules.ColdSide(site.Cell, site.Rotation), reading.Facts.Upkeep.Rooms.CellsOf(reading.Facts.Upkeep.Rooms.Rooms[0])));
        native.Reject = proposal.Options[0].Name;
        var planner = new BuildingPlanner { Native = source, Refrigeration = proposal, Definition = proposal.Options[0].Name, Concern = FoodStoresRules.RefrigerationNeed.FilingKey, Need = FoodStoresRules.RefrigerationNeed };
        var snapshot = new GenerationSnapshot(new("colony"), new(1), new("load"), new("plan"), new(1), new(1));
        var (previews, _, verdict) = await planner.PreviewRefrigerationAsync(snapshot, reading, [], () => { }, ct);
        Assert.True(verdict.IsZero, verdict.ToString());
        Assert.Equal(proposal.WallSites.Count + 1, native.Placements.Count);
        var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(IntentActionRules.IntentAction("cool", Assert.Single(previews).Action).Intent).Value;
        Assert.Equal("InstalledCooler001", intent.Placement!.DefName);
        Assert.Equal(RefrigerationMethod.Wait, RefrigerationRules.SelectRefrigerationMethod(review, observed with { Builders = [] }, policy, false).Method);
        Assert.Throws<RequiredFactException>(() => RefrigerationRules.SelectRefrigerationMethod(review, observed with { Definitions = [] }, policy, false));
        Assert.Equal(RefrigerationMethod.WaitBlackout, RefrigerationRules.SelectRefrigerationMethod(review, observed with { Blackout = true }, policy, false).Method);
        Assert.Equal(count + 1, candidates.Count);
        output.WriteLine($"Cooler candidates {candidates.Count}; available {proposal.Options.Count}; room cells 48; wall sites {proposal.WallSites.Count}; maximum native previews {proposal.Options.Count * proposal.WallSites.Count}");
    }

    [Fact]
    public async Task ExistingCatalogCoolersAreReadByIdAndKeepTheirNativeControl()
    {
        var (reading, _) = Reading(2);
        var row = PowerBuildingFixtures.Trader("installed", "InstalledCooler001", new(1, 2), -200, -200, true);
        row = row with { Rotation = RimGovernor.Brain.GameFacts.Placement.Rotation.West, Settings = row.Settings with
        { Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.Temperature(new() { Context = ConstructionFixtures.Context(), EntityId = row.Building.Id, Token = "control" }, 20) } };
        reading = reading with { Facts = reading.Facts with { EssentialInputs = reading.Facts.EssentialInputs with { Power = new() { Buildings = [row] } } } };
        var source = DispatchProxy.Create<ITestNative, Native>();
        var native = (Native)(object)source;
        native.Buildings = native.Buildings with { Buildings = [row] };
        var observed = await RefrigerationReads.RefrigerationFacts(source, reading, TestContext.Current.CancellationToken);
        Assert.Equal("installed", Assert.Single(native.Reads));
        Assert.Same(row, Assert.Single(observed.Coolers).Observed);
        var proposal = RefrigerationRules.SelectRefrigerationMethod(new() { Active = true, Rooms = ["room"] }, observed, FoodStorageUpkeepRules.DefaultFoodStoragePolicy(), false);
        Assert.Equal(RefrigerationMethod.SetTarget, proposal.Method);
        Assert.Equal(row.Building.Id, proposal.Cooler);
        native.Buildings = native.Buildings with { Buildings = [] };
        await Assert.ThrowsAsync<RequiredFactException>(() => RefrigerationReads.RefrigerationFacts(source, reading, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("InstalledCooler000", true)]
    [InlineData("InstalledCooler001", true)]
    [InlineData("unrelated", false)]
    public void CoolingAllowanceRecognizesTheActualCatalogDefinitions(string definition, bool cooler)
    {
        // Receipt semantics must not depend on another test having initialized the intent registry.
        IntentActionRules.RegisterIntentKinds();
        var (reading, _) = Reading(2);
        var names = RefrigerationRules.CoolerDefinitions(reading.Facts.Stations).Select(candidate => candidate.Name).ToHashSet(StringComparer.Ordinal);
        var action = DomainAction.NewBuildingAction(new("cool-0"), new Building(definition, new(1, 2), Rotation.West, ""), ConstructionTier.Sustain);
        var spec = new PlanSpec(new("cool"), new(1), [action]);
        var snapshot = new GenerationSnapshot(new("colony"), new(1), new("load"), spec.Id, spec.Revision, new(1));
        var progress = new Progress(spec, action.Id).Prepare(snapshot, new(1)).MarkDispatched(snapshot, new(1));
        progress = progress.RecordReceipt(progress.View.Attempt, Receipt.Accepted);
        var plan = new RimGovernor.Brain.Store.PlanState(false, default, new("method"), spec, [progress], []);
        var remaining = BuildingPlanner.RefrigerationCoolingRemaining(plan, snapshot, new(2), names);
        Assert.Equal(cooler, remaining.Completed);
        Assert.Equal(cooler ? BuildingPlanner.RefrigerationCoolingTicks - 1L : 0, remaining.Remaining);
        Assert.Equal((0L, cooler), BuildingPlanner.RefrigerationCoolingRemaining(plan, snapshot, new(BuildingPlanner.RefrigerationCoolingTicks + 1), names));
    }
}
