using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The coupled thermal read is one total facts record (#3771): every member is set, a source's supply is a named
/// <see cref="HeatSupply"/> state and a roof route's approach a named <see cref="RoofApproach"/>. On the wire it round-trips
/// through <c>OrdersJsonContext</c> exactly.
/// </summary>
public sealed class CoupledThermalTests
{
    /// <summary>A complete read with no cells, sources, workers or routes at the game's exchange constants: the base every test thermal read starts from.</summary>
    internal static readonly CoupledThermalRead NoThermal = new()
    {
        Cells = [], Sources = [], Workers = [], RoofRoutes = [],
        Outdoors = 20, ThinRoofExchangePerTick = .00005, NoRoofExchangePerTick = .0007, WallExchangePerTick = .000017,
    };

    /// <summary>An electric heater supply on a network with no other draw, supplied for <paramref name="ticks"/>.</summary>
    internal static HeatSupply Powered(double ticks) => new HeatSupply.Powered("grid", 0, 0, ticks);

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var read = NoThermal with
        {
            Outdoors = -12.5,
            Cells = [new(new(3, 4), 5.5, "none", false, true, "Blueprint_Wall9", true, false, .25, true)],
            Sources =
            [
                new("Thing_Heater1", new(3, 5), true, .35, 21, new HeatSupply.Powered("Thing_Battery2/Thing_Heater1", 175, 400, 60000)),
                new("Thing_Heater3", new(4, 5), false, .35, 21, new HeatSupply.ShortOfPower("Thing_Battery2", 175, 100)),
                new("Thing_Heater4", new(5, 5), false, .35, 21, new HeatSupply.Unconnected()),
                new("Thing_Campfire5", new(6, 5), true, .35, 23, new HeatSupply.Burning("WoodLog", .0002)),
                new("Thing_Torch6", new(7, 5), true, .1, 23, new HeatSupply.UnsteadyBurn()),
            ],
            Workers = [new("Thing_Human7", 38.2)],
            RoofRoutes = [new("Thing_Human7", new(3, 4), new RoofApproach.Path(120)), new("Thing_Human8", new(3, 4), new RoofApproach.Unreachable())],
        };

        var json = OrdersJson.Serialize(read);

        Assert.Equal(
            """{"cells":[{"cell":{"x":3,"z":4},"temperature":5.5,"roof":"none","solid":false,"doorHeldOpen":true,"site":"Blueprint_Wall9","futureSolid":true,"futureDoor":false,"doorExchangePerTick":0.25,"buildRoof":true,"moodCell":null}],"sources":[{"target":"Thing_Heater1","cell":{"x":3,"z":5},"complete":true,"heatPerTick":0.35,"targetTemperature":21,"supply":{"type":"powered","network":"Thing_Battery2/Thing_Heater1","additionalWatts":175,"surplusWatts":400,"ticks":60000},"hotSideTemperature":0,"powerWatts":0,"definition":"","buildCell":{"x":0,"z":0},"buildRotation":0,"buildEstimate":null},{"target":"Thing_Heater3","cell":{"x":4,"z":5},"complete":false,"heatPerTick":0.35,"targetTemperature":21,"supply":{"type":"short_of_power","network":"Thing_Battery2","additionalWatts":175,"surplusWatts":100},"hotSideTemperature":0,"powerWatts":0,"definition":"","buildCell":{"x":0,"z":0},"buildRotation":0,"buildEstimate":null},{"target":"Thing_Heater4","cell":{"x":5,"z":5},"complete":false,"heatPerTick":0.35,"targetTemperature":21,"supply":{"type":"unconnected"},"hotSideTemperature":0,"powerWatts":0,"definition":"","buildCell":{"x":0,"z":0},"buildRotation":0,"buildEstimate":null},{"target":"Thing_Campfire5","cell":{"x":6,"z":5},"complete":true,"heatPerTick":0.35,"targetTemperature":23,"supply":{"type":"burning","fuel":"WoodLog","fuelPerTick":0.0002},"hotSideTemperature":0,"powerWatts":0,"definition":"","buildCell":{"x":0,"z":0},"buildRotation":0,"buildEstimate":null},{"target":"Thing_Torch6","cell":{"x":7,"z":5},"complete":true,"heatPerTick":0.1,"targetTemperature":23,"supply":{"type":"unsteady_burn"},"hotSideTemperature":0,"powerWatts":0,"definition":"","buildCell":{"x":0,"z":0},"buildRotation":0,"buildEstimate":null}],"workers":[{"pawn":"Thing_Human7","roofCellWorkTicks":38.2,"skillWork":null}],"roofRoutes":[{"pawn":"Thing_Human7","cell":{"x":3,"z":4},"approach":{"type":"path","ticks":120}},{"pawn":"Thing_Human8","cell":{"x":3,"z":4},"approach":{"type":"unreachable"}}],"outdoors":-12.5,"thinRoofExchangePerTick":5E-05,"noRoofExchangePerTick":0.0007,"wallExchangePerTick":1.7E-05}""",
            json);
        Assert.Equal(read, OrdersJson.Deserialize<CoupledThermalRead>(json));
    }

    /// <summary>
    /// An unreachable roof route is excluded from shelter routing by its named state (#3771): the colonist who cannot reach
    /// the roof cells gets no roof option and no route, never an approach priced as zero.
    /// </summary>
    [Fact]
    public void AnUnreachableRoofRouteGetsNoRoofOptionOrRoute()
    {
        var interior = EquatableArray.From(Enumerable.Range(1, 3).SelectMany(x => Enumerable.Range(1, 3).Select(z => new Cell(x, z))));
        var cells = Enumerable.Range(0, 5).SelectMany(x => Enumerable.Range(0, 5).Select(z => new Cell(x, z)));
        var read = NoThermal with
        {
            Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, -10, "none", !interior.Contains(c), false, "", false, false, 0, interior.Contains(c)))),
            Workers = [new("near", 5), new("far", 5)],
            RoofRoutes = EquatableArray.From(interior.SelectMany(c => new[]
                { new ThermalRoofRoute("near", c, new RoofApproach.Path(30)), new ThermalRoofRoute("far", c, new RoofApproach.Unreachable()) })),
        };
        var room = new ForecastShelterGeometry(new(ForecastShelterSource.Planned, "1"), interior, new Policy.Rectangle(1, 1, 3, 3), new(0, 2), false);

        var result = CoupledShelterCandidates.Generate([room], RoomTestFacts.NoRooms, read, [], new Dictionary<(string, string), ActivityTaskCandidate>(), [], [], 0, 1000);

        Assert.Equal(interior.Count, result.Options.Count(o => o.Activity.StartsWith("roof/", StringComparison.Ordinal)));
        Assert.All(result.Options, o => Assert.Equal("near", o.Pawn));
        Assert.All(result.Routes, r => Assert.Equal("near", r.Pawn));
        Assert.All(result.Routes, r => Assert.Equal(new RouteTicks.Reachable(30d), r.Ticks));
    }
}
