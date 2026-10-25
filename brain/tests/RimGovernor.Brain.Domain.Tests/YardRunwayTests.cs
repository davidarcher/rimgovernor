using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

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
                Capacity = 100,
                Used = used,
                Filter = new StockpileFilter(FilterBase.Nothing),
            },
        ],
        Loot = EquatableArray.From(loot),
        Occupancy = perDay is { } rate
            ? new RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot
            {
                CurrentHour = Now,
                Groups =
                [
                    new RimGovernor.Brain.GameFacts.Observations.StorageOccupancyGroup
                    {
                        Group = "Zone_yard",
                        Samples = EquatableArray.From(Enumerable.Range(Now - 71, 72).Select(h => new RimGovernor.Brain.GameFacts.Observations.StorageOccupancySample { Hour = h, Stacks = (int)Math.Round(used - (rate * (Now - h) / 24)) })),
                    },
                ],
            }
            : new RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot { CurrentHour = Now, Groups = [] },
    };

    private static LootItem Stranded() =>
        LootFixtures.Stack("Thing_1", "Steel", new Cell(50, 50), false,
            new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Safe { PathLength = 0 }, 10, 0, ["Zone_yard"], null);

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
    }

    [Fact]
    public void ADrainingYardAsksNothing()
    {
        Assert.Equal(0, StoreRules.YardDemand(Request(99, -3)));
    }

    [Fact]
    public void AYardWhoseZoneDoesNotStandWaits()
    {
        Assert.Equal(0, StoreRules.YardDemand(Request(90, 5, Stranded()) with { Zones = [] }));
    }

    [Fact]
    public void LeadTimeIsBuildWorkAtBaseSpeedPlusTheHorizon()
    {
        Assert.Equal(ColonyDiagnosticsRules.MaintenanceHorizonDays, StoreRules.StorageLeadDays(StoreRules.FurtherYardWorkTicks));
        Assert.Equal(ColonyDiagnosticsRules.MaintenanceHorizonDays + 0.5, StoreRules.StorageLeadDays(GameTime.TicksPerDay / 2.0));
    }
}
