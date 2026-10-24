using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

internal static class CapacityWindowsAssertions
{
    /// <summary>The windows of a capacity the test expects read; fails when it is unread.</summary>
    internal static EquatableArray<ForecastTimeWindow> ReadWindows(this ForecastCapacityWindows windows) =>
        Assert.IsType<ForecastCapacityWindows.Read>(windows).Windows;
}
