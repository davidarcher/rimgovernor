using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A closed-room prediction is one total facts record (#3834): every member is read, the Impressiveness stage included,
/// and a shape that closes no room has no row. On the wire it round-trips through <c>WireJsonContext</c> exactly.
/// </summary>
public sealed class PlannedRoomPredictionTests
{
    /// <summary>A prediction as native answers it; terrains, beds, benches and desks none and cleanliness 0 unless a test sets them.</summary>
    internal static PlannedRoomPrediction Closed(string room, string stuff, string role, double impressiveness, int stage, int cells, int openRoofCells, bool touchesMapEdge) =>
        new(room, stuff, role, impressiveness, stage, cells, openRoofCells, touchesMapEdge) { Terrains = [], Cleanliness = 0, Beds = [], Benches = [], Desks = [] };

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var prediction = Closed("3", "WoodLog", "Bedroom", 32.5, 3, 9, 2, false) with
        {
            Terrains = [new(["Floor"])],
            Cleanliness = -0.25,
            Beds = ["Thing_Bed4"],
            Benches = [new PlannedRoomBench("Thing_Table5", true)],
            Desks = [new PlannedRoomDesk(1.4)],
        };

        var json = WireJson.Serialize(prediction);

        Assert.Equal(
            """{"room":"3","stuff":"WoodLog","role":"Bedroom","impressiveness":32.5,"stage":3,"cells":9,"openRoofCells":2,"touchesMapEdge":false,"terrains":[{"tags":["Floor"]}],"cleanliness":-0.25,"beds":["Thing_Bed4"],"benches":[{"bench":"Thing_Table5","psychologicallyOutdoors":true}],"desks":[{"learningRate":1.4}]}""",
            json);
        Assert.Equal(prediction, WireJson.Deserialize<PlannedRoomPrediction>(json));
    }
}
