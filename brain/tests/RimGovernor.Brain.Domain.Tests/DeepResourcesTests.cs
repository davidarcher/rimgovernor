using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The deep resource census is one total facts record (#3767): every member is <c>required</c>, a drill's deposit is
/// a relation (null once nothing valuable remains under it), and the record round-trips through <c>OrdersJsonContext</c>
/// exactly.
/// </summary>
public sealed class DeepResourcesTests
{
    /// <summary>A map with no discovered lump, no scanner and no drill: the census every test without deep resources carries.</summary>
    internal static readonly DeepResources NoDeepResources = new() { Lumps = [], GroundScanners = [], LongRangeScanners = [], Drills = [] };

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var deep = new DeepResources
        {
            Lumps = [new DeepResourceLump { Definition = "Plasteel", Count = 300, Centre = new Cell(10, 12), CellCount = 3 }],
            GroundScanners = [new MineralScanner { Definition = "GroundPenetratingScanner", Position = new Cell(2, 3), Powered = true, Working = false, TicksToNextFind = 42000 }],
            LongRangeScanners = [],
            Drills =
            [
                new DeepDrill { Id = "Thing_DeepDrill7", Definition = "DeepDrill", Position = new Cell(10, 12), Deposit = new DrillDeposit { Resource = "Plasteel", Remaining = 100 }, Designated = false },
                new DeepDrill { Id = "Thing_DeepDrill9", Definition = "DeepDrill", Position = new Cell(10, 4), Deposit = null, Designated = true },
            ],
        };

        var json = OrdersJson.Serialize(deep);

        Assert.Equal(
            """{"lumps":[{"definition":"Plasteel","count":300,"centre":{"x":10,"z":12},"cellCount":3}],"groundScanners":[{"definition":"GroundPenetratingScanner","position":{"x":2,"z":3},"powered":true,"working":false,"ticksToNextFind":42000}],"longRangeScanners":[],"drills":[{"id":"Thing_DeepDrill7","definition":"DeepDrill","position":{"x":10,"z":12},"deposit":{"resource":"Plasteel","remaining":100},"designated":false},{"id":"Thing_DeepDrill9","definition":"DeepDrill","position":{"x":10,"z":4},"deposit":null,"designated":true}]}""",
            json);
        Assert.Equal(deep, OrdersJson.Deserialize<DeepResources>(json));
    }
}
