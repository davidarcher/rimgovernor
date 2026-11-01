using RimGovernor.Brain.Facts;
using System.Reflection;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3814: the layout plans no battery room, and every battery still stands under a roof. The store method's search
/// takes only roofed indoor cells, so a battery wanted beside an outdoor consumer goes to the nearest roofed room; and
/// a battery standing unroofed is enclosed where it stands by shelter_power.
/// </summary>
public sealed class BatteryPlacementTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    /// <summary>The roofed indoor room: interior x 10..12, z 4..6.</summary>
    private static bool Roofed(Cell c) => c.X is >= 10 and <= 12 && c.Z is >= 4 and <= 6;

    /// <summary>Records every anchor previewed; a battery's 1x2 footprint (facing north) is ready wherever it is asked.</summary>
#pragma warning disable CA1852 // DispatchProxy derives the proxy from its base, so the base cannot be sealed
    private class Native : DispatchProxy
    {
        public List<Cell> Previewed { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IReviewBuildingSource.PreviewBuildingAsync))
            {
                throw new NotSupportedException(targetMethod?.Name + " is outside the battery rig");
            }
            var action = (DomainAction)args![0]!;
            var snapshot = (GenerationSnapshot)args[1]!;
            var c = action.Building!.Cell;
            Previewed.Add(c);
            var preview = new Preview
            {
                Action = action,
                Snapshot = snapshot,
                CanPlace = true,
                SafeToPlace = true,
                Source = PreviewTestFacts.Source(c),
                Footprint = [c, new(c.X, c.Z + 1)],
                Costs = [],
            };
            return Task.FromResult(new BuildingPreview { Preview = preview, Stock = new StockObservation { Snapshot = snapshot, Tick = new(100) } });
        }
    }
#pragma warning restore CA1852

    [Fact]
    public async Task TheStoreSearchPlacesABatteryOnARoofedCell()
    {
        var ct = TestContext.Current.CancellationToken;
        var cells = new List<SiteCell>();
        for (var z = 0; z < 20; z++)
        {
            for (var x = 0; x < 20; x++)
            {
                var c = new Cell(x, z);
                cells.Add(SiteCellFixtures.At(c, roof: Roofed(c) ? "RoofConstructed" : null, indoors: Roofed(c)));
            }
        }
        var facts = ColonyFactsCoreTests.Reading(new()
            {
                Context = ColonyFactsCoreTests.Snapshot().Context with
                {
                    Tick = 100
                }
            }) with
        {

            Facts = ColonyFactsCoreTests.Reading().Facts with
            {
                Comfort = ComfortTests.NoComfortObservation,
                Upkeep = new()
                {
                    Rooms = RoomTestFacts.NoRooms,
                    Routes = new() { PawnIds = [], Facilities = [], Traffic = [], TrafficSamples = 0 }
                },
                BasicComfort = ComfortTests.NoComfortObservation,
                MapBounds = new(20, 20)
            },
            Window = new Held<PlanningCells>
            {
                Value = new PlanningCells
                {
                    Cells = [.. cells]
                }
            }
        };
        var native = DispatchProxy.Create<IReviewBuildingSource, Native>();
        var planner = new BuildingPlanner
        {
            Native = native,
            Concern = PowerFamily.Basic.FilingKey,
            Need = PowerFamily.Basic,
            Definition = PowerRules.BatteryDefinition,
            Environment = PlacementEnvironment.Indoors,
            // The draining consumer stands outdoors at (5, 5); the nearest open ground is all unroofed.
            Power = new PowerProposal { Method = PowerMethod.Store, Definition = PowerRules.BatteryDefinition, Center = new(5, 5) },
        };
        var snapshot = World with { Plan = new("store"), Revision = new(1) };

        var (previews, _, verdict) = await planner.PreviewPowerOrSearchAsync(snapshot, facts, [], 1, () => { }, ct);

        Assert.True(verdict.IsZero, verdict.ToString());
        var battery = Assert.Single(previews);
        Assert.Equal(PowerRules.BatteryDefinition, battery.Action.Building!.Definition);
        Assert.All(battery.Footprint, c => Assert.True(Roofed(c), $"battery cell {c} is unroofed"));
        Assert.All(((Native)(object)native).Previewed, c => Assert.True(Roofed(c), $"previewed unroofed anchor {c}"));
    }

    [Fact]
    public void AnUnroofedBatteryIsShelteredWhereItStands()
    {
        var light = new LightTerrains(new Dictionary<string, bool> { ["Soil"] = true });
        var cells = new List<SiteCell>();
        for (var z = 0; z < 20; z++)
        {
            for (var x = 0; x < 20; x++)
            {
                cells.Add(SiteCellFixtures.At(new(x, z)));
            }
        }
        var battery = PowerBuildingFixtures.Trader("battery", PowerRules.BatteryDefinition, new(8, 8), 0, 0, true) with
        {
            Service = BuildingServiceWire.NoService with { Power = new RimGovernor.Brain.GameFacts.Observations.BuildingPower.Battery { Network = "net", StorageWD = 600 } },
            Occupied = new RimGovernor.Brain.GameFacts.Observations.Rectangle { Minimum = new() { X = 8, Z = 8 }, Maximum = new() { X = 8, Z = 9 } },
            RainVulnerable = true, Roofed = false,
        };
        // The battery feeds a freezer whose interruption is priced, beside a generator that covers its draw.
        var generator = PowerBuildingFixtures.Trader("gen", "WoodFiredGenerator", new(2, 2), 500, 500, true);
        var freezer = PowerBuildingFixtures.Trader("freezer", "Cooler", new(4, 2), -200, 0, true);
        var topology = new PowerTopology { Buildings = [battery, generator, freezer], Blackout = false, Eclipse = false };
        static ProvisionOption Piece(string name, double cost) => new(name, false, 0.01, cost);
        var planning = new PowerPlanning
        {
            Battery = new PowerBattery(600, 0.5), BatteryAvailable = true, Light = light, Now = new(600_000),
            Sources = new Dictionary<string, PowerSourceProfile> { ["WoodFiredGenerator"] = PowerRules.ConstantPowerProfile },
            Valuation = new PowerValuation
            {
                Consumers = new Dictionary<string, ConsumerValue>(StringComparer.Ordinal) { ["freezer"] = new(UtilityService.Refrigeration, new InterruptionLoss.PerDay(20)) },
                Pieces = new[] { Piece(ContainmentCellRules.ShellWallDefinition, 0.01), Piece(ContainmentCellRules.ShellDoorDefinition, 0.02), Piece(PowerRules.RoofPiece, 0.005),
                    Piece(PowerRules.BatteryDefinition, 1) }.ToDictionary(p => p.Kind, StringComparer.Ordinal),
            },
            Rain = new PowerRain.Read(0.5),
        };

        var proposal = PowerRules.SelectPowerMethod(topology, new Bounds(20, 20), cells, [], planning);

        // Roofing and a second battery are compared inside the one choice under the read rain rate: the battery keeps the
        // short-circuit exposure, the shelter clears it and costs less than the exposed burn over the horizon.
        Assert.Equal(PowerMethod.Shelter, proposal.Method);
        var exposure = RainShortCircuitRules.Exposure([(true, 600, true)], 1, planning.Rain, 200);
        // 60000/97 rolls a day at 2%, each picking the lone vulnerable building at 0.2 x 1 x 0.5; a stored 600 Wd wicks at 5% for 400 Wd.
        var expected = Assert.IsType<UtilityExposure.Expected>(exposure);
        Assert.Equal(GameTime.TicksPerDay / 97d * 0.02 * 0.1, expected.EventsPerDay, 9);
        Assert.Equal(0.05 * 400 / 200, expected.OutageDays, 9);
        var decision = proposal.Decision!;
        Assert.Equal(UtilityChoiceKind.Shelter, decision.Kind);
        var store = decision.Alternatives.Single(a => a.Alternative.Kind == UtilityChoiceKind.Store);
        var shelter = decision.Alternatives.Single(a => a.Alternative.Kind == UtilityChoiceKind.Shelter);
        Assert.True(Assert.IsType<UtilityPrice.Days>(store.Price).Value > Assert.IsType<UtilityPrice.Days>(shelter.Price).Value);
        // Without the rain rate the exposure is unassessed: only the shelter stays priced, so the choice defers and nothing is built.
        Assert.NotEqual(PowerMethod.Shelter, PowerRules.SelectPowerMethod(topology, new Bounds(20, 20), cells, [], planning with { Rain = new PowerRain.Unread("rain rate") }).Method);
        Assert.Equal(battery.Cell, proposal.Center);
        Assert.All(battery.OccupiedCells, c => Assert.True(c.X > proposal.Room.X && c.X < proposal.Room.X + proposal.Room.Width - 1
            && c.Z > proposal.Room.Z && c.Z < proposal.Room.Z + proposal.Room.Height - 1, $"battery cell {c} is not inside the shelter"));
    }
}
