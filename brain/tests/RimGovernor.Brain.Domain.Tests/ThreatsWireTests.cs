using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The status threat census is total (#3770): every fact is a plain value, the nearest-colonist distance is a
/// <see cref="ColonistDistance"/> (nearest or no colonist) and a hunt's prey is a <see cref="ThreatPrey"/> relation.
/// It round-trips through <c>OrdersJsonContext</c> exactly.
/// </summary>
public sealed class ThreatsWireTests
{
    [Fact]
    public void ThreatCensusJsonRoundTripIsExact()
    {
        var cell = new RimGovernor.Brain.Domain.Cell { X = 5, Z = 6 };
        var threats = new ThreatsSnapshot
        {
            Pawns =
            [
                new ThreatPawn
                {
                    Pawn = new RimGovernor.Brain.GameFacts.Common.Ref { Id = "Wolf_7" },
                    Prey = new ThreatPrey(false),
                    Ours = false,
                    Passive = false,
                    NearestColonist = new ColonistDistance.Nearest(14),
                    FactionHostile = false,
                    PrisonBreak = false,
                    PredatorHunt = true,
                    Predator = true,
                    Downed = false,
                },
            ],
            HostileBuildings =
            [
                new ThreatBuilding
                {
                    Building = new EntityRef { Id = "Hive_3", DefName = "Hive", Label = "hive", Place = new EntityPlace.OnMap(1, cell) },
                    Snapshot = "tok",
                    NearestColonist = new ColonistDistance.NoColonist(),
                    Passive = true,
                    Fogged = true,
                    Occupied = new Rectangle { Minimum = cell, Maximum = cell },
                },
            ],
        };

        var json = OrdersJson.Serialize(threats);

        Assert.Equal(
            """{"pawns":[{"pawn":{"id":"Wolf_7"},"prey":{"ours":false},"ours":false,"passive":false,"nearestColonist":{"type":"nearest","cells":14},"mentalState":null,"factionHostile":false,"prisonBreak":false,"predatorHunt":true,"predator":true,"downed":false}],"hostileBuildings":[{"building":{"id":"Hive_3","defName":"Hive","label":"hive","place":{"type":"on_map","mapId":1,"cell":{"x":5,"z":6}}},"snapshot":"tok","nearestColonist":{"type":"no_colonist"},"passive":true,"fogged":true,"occupied":{"minimum":{"x":5,"z":6},"maximum":{"x":5,"z":6}}}]}""",
            json);
        Assert.Equal(threats, OrdersJson.Deserialize<ThreatsSnapshot>(json));
    }
}
