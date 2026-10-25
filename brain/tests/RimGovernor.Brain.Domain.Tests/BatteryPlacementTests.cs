using System.Reflection;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

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
            if (targetMethod?.Name != nameof(IRoundsBuildingSource.PreviewBuildingAsync))
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
                MadeFromStuff = false,
                WatchCells = WatchAccess.NotWatched,
                Wind = new BuildingWind.NotWindTurbine(),
                Footprint = [c, new(c.X, c.Z + 1)],
                Costs = [],
                Blockers = [],
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
                cells.Add(SiteCellFixtures.Ground with { Cell = c, Walkable = true, Zone = false, Roofed = Roofed(c), Indoors = Roofed(c) });
            }
        }
        var facts = ColonyFactsCoreTests.Projection() with { Identity = new() { Tick = new(100) }, Bounds = new(20, 20), Cells = cells };
        var native = DispatchProxy.Create<IRoundsBuildingSource, Native>();
        var planner = new RoundsBuildingPlanner
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
                cells.Add(SiteCellFixtures.Ground with { Cell = new(x, z), Walkable = true, Zone = false, Terrain = "Soil" });
            }
        }
        var battery = new PowerSite
        {
            Id = "battery",
            Definition = PowerRules.BatteryDefinition,
            Cell = new(8, 8),
            Occupied = [new(8, 8), new(8, 9)],
            BaseW = 0,
            Fuel = new RimGovernor.Contracts.Wire.Observations.BuildingFuel.NotRefuelable(),
            Connected = true,
            Network = "net",
            RainVulnerable = true,
            Roofed = false,
        };
        var topology = new PowerTopology { Buildings = [battery], Blackout = false, Eclipse = false };
        var planning = new PowerPlanning { ComponentSteel = new InputCost.NoRecipe(), Battery = new PowerBattery(600, 0.5), Light = light };

        var proposal = PowerRules.SelectPowerMethod(topology, new Bounds(20, 20), cells, [], planning);

        Assert.Equal(PowerMethod.Shelter, proposal.Method);
        Assert.Equal(battery.Cell, proposal.Center);
        Assert.All(battery.Occupied, c => Assert.True(c.X > proposal.Room.X && c.X < proposal.Room.X + proposal.Room.Width - 1
            && c.Z > proposal.Room.Z && c.Z < proposal.Room.Z + proposal.Room.Height - 1, $"battery cell {c} is not inside the shelter"));
    }
}
