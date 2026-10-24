using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3814: no modelled consequence changes with a planned battery room (every battery is roofed without it; its fire
/// containment is unmodelled fire harm), so the shipped credit is zero. Priced, its floors' and removals' relief leaves the
/// development count and its ring material is charged, as #3796's seam does for any priced role.
/// </summary>
public sealed class BatteryCreditTests
{
    private const double Day = GameTime.TicksPerDay;

    /// <summary>The seam's room 1 (PlannedRoomCreditTests.Context) as a battery room, in steady rain.</summary>
    private static ForecastCoupledContext Context() => PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped) is var context
        ? context with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Battery, [2] = PlannedRole.Workshop },
            Needs = context.Needs with { RainRate = Known(1d) },
        }
        : throw new InvalidOperationException();

    [Fact]
    public void AServedBatteryRoomEarnsNothingAndLeavesTheDevelopmentCount()
    {
        var context = Context();
        var outcome = PlannedRoomCreditTests.Outcome(context);
        Assert.Equal(new ForecastRoomService(1, "WoodLog", 3 * Day), Assert.Single(outcome.ServedRooms));
        var value = ForecastValue.Of(outcome, context, Day);
        Assert.DoesNotContain(value.InvestmentContributions, r => r.Key.StartsWith("battery/", StringComparison.Ordinal));
        Assert.DoesNotContain(value.Findings, f => f.StartsWith("battery_credit", StringComparison.Ordinal));
        Assert.True(context.Priced(1));
        // The shelf and the floor of no room are left, as for any priced room.
        Assert.Equal(2, PlannedRoomCreditTests.Development(context));
        Assert.Equal([new Resource("WoodLog")], ForecastValue.ChargedMaterials(context));
    }
}
