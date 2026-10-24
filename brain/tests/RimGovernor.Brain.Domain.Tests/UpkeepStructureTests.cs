using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The structure census rows are read whole (#3833): every member is set, a stuffless def's stuff is null and the
/// flammability and repair priority are the catalog's, not the wire's. On the wire a row round-trips through
/// <c>WireJsonContext</c> exactly.
/// </summary>
public sealed class UpkeepStructureTests
{
    [Fact]
    public void JsonRoundTripIsExact()
    {
        EquatableArray<UpkeepStructure> rows =
        [
            new() { Id = "Thing_Wall3", Definition = "Wall", Stuff = "BlocksGranite", Cell = new(3, 4), Home = true, HitPoints = 280, MaxHitPoints = 300 },
            new() { Id = "Thing_Cooler4", Definition = "Cooler", Stuff = null, Cell = new(5, 6), Home = false, HitPoints = 100, MaxHitPoints = 100 },
        ];

        var json = WireJson.Serialize(rows);

        Assert.Equal(
            """[{"id":"Thing_Wall3","definition":"Wall","stuff":"BlocksGranite","cell":{"x":3,"z":4},"home":true,"hitPoints":280,"maxHitPoints":300},{"id":"Thing_Cooler4","definition":"Cooler","stuff":null,"cell":{"x":5,"z":6},"home":false,"hitPoints":100,"maxHitPoints":100}]""",
            json);
        Assert.All(rows, row => Assert.Equal(row, WireJson.Deserialize<UpkeepStructure>(WireJson.Serialize(row))));
    }
}
