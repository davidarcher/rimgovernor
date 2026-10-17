using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3236: the yard asks layout for a further yard from its runway (free stacks over net inflow) against a further
/// yard's lead time, or now when it has stranded stock; the 85% fill line is gone.
/// </summary>
public sealed class YardRunwayTests
{
    private const int Now = 200;

    private static readonly Rectangle YardRoom = new() { X = 10, Z = 10, Width = 10, Height = 10 };

    private static readonly Policy.Store Yard = new() { Site = new StoreSite { Role = StockpileRolesConstants.YardRole, Interior = YardRoom } };

    /// <summary>A yard of 100 stacks holding used, gaining perDay stacks a day over the last three days (null: no ring read).</summary>
    private static StockpileRequest Request(int used, double? perDay, params LootItem[] loot) => new()
    {
        Stores = [Yard],
        Zones =
        [
            new StockpileZone
            {
                Id = "Zone_yard",
                Role = StockpileRolesConstants.YardRole,
                Cells = PlannedRules.RectCells(YardRoom),
                Capacity = Known(100),
                Used = Known(used),
                Filter = new StockpileFilter(FilterBase.Nothing),
            },
        ],
        Loot = Known(EquatableArray.From(loot)),
        Occupancy = perDay is { } rate
            ? Known(new StoreOccupancy
            {
                CurrentHour = Now,
                Groups = new Dictionary<string, EquatableArray<OccupancySample>>
                {
                    ["Zone_yard"] = EquatableArray.From(Enumerable.Range(Now - 71, 72).Select(h => new OccupancySample { Hour = h, Stacks = (int)Math.Round(used - (rate * (Now - h) / 24)) })),
                },
            })
            : Unknown<StoreOccupancy>(),
    };

    private static LootItem Stranded() => new()
    {
        Supply = new StartingSupply { Thing = "Thing_1", Definition = "Steel", Cell = new Cell(50, 50) },
        SafeToHaul = true,
        SafetyKnown = true,
        Count = 10,
        StorageHeadroom = Known(0L),
        AcceptedBy = EquatableArray.From<string>(["Zone_yard"]),
    };

    [Fact]
    public void ASmallColonyFillingFastAsksBeforeALargeOneAtTheSameFill()
    {
        // Both yards are 90% full. Ten free stacks last two days at five a day, under the five-day lead time; at
        // one a day they last ten.
        Assert.Equal(2, StoreRules.YardDemand(Request(90, 5)));
        Assert.Equal(0, StoreRules.YardDemand(Request(90, 1)));
    }

    [Fact]
    public void AYardWithStrandedStacksAsksAtAnyFill()
    {
        Assert.Equal(2, StoreRules.YardDemand(Request(10, null, Stranded())));
    }

    [Fact]
    public void UnknownInflowAndNoStrandedStockAsksNothing()
    {
        Assert.Equal(0, StoreRules.YardDemand(Request(100, null)));
        Assert.False(StoreRules.StoreCapacity(Request(100, null), Yard).RunwayDays.IsKnown);
    }

    [Fact]
    public void ADrainingYardAsksNothing()
    {
        var request = Request(99, -3);
        Assert.Equal(0, StoreRules.YardDemand(request));
        Assert.Equal(Known(double.PositiveInfinity), StoreRules.StoreCapacity(request, Yard).RunwayDays);
    }

    [Fact]
    public void AYardWhoseZoneDoesNotStandWaits()
    {
        Assert.Equal(0, StoreRules.YardDemand(Request(90, 5, Stranded()) with { Zones = [] }));
    }

    [Fact]
    public void LeadTimeIsBuildWorkAtBaseSpeedPlusTheHorizon()
    {
        Assert.Equal(Known(ColonyDiagnosticsRules.MaintenanceHorizonDays), StoreRules.StorageLeadDays(Known(StoreRules.FurtherYardWorkTicks)));
        Assert.Equal(Known(ColonyDiagnosticsRules.MaintenanceHorizonDays + 0.5), StoreRules.StorageLeadDays(Known(GameTime.TicksPerDay / 2.0)));
        Assert.False(StoreRules.StorageLeadDays(Unknown<double>()).IsKnown);
    }
}
