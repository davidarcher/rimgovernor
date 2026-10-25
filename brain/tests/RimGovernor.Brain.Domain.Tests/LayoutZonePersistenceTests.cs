using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LayoutZonePersistenceTests
{
    [Theory]
    [InlineData("", LayoutZoneKind.None)]
    [InlineData("field", LayoutZoneKind.Field)]
    [InlineData("pasture", LayoutZoneKind.Pasture)]
    [InlineData("mining", LayoutZoneKind.Mining)]
    [InlineData("wood", LayoutZoneKind.Wood)]
    [InlineData("no_go", LayoutZoneKind.NoGo)]
    [InlineData("core", LayoutZoneKind.Core)]
    public void SavedLandUseNamesRetainTheirGeometry(string name, LayoutZoneKind kind)
    {
        var json = "{\"Kind\":\"" + name + "\",\"Runs\":[{\"Z\":3,\"X\":4,\"Length\":2}],\"Ore\":true}";

        var zone = StoreJson.Deserialize<LayoutZone>(json);

        Assert.Equal(kind, zone.Kind);
        Assert.Equal(new RowRun(3, 4, 2), Assert.Single(zone.Runs));
        Assert.True(zone.Ore);
        Assert.Equal(json, StoreJson.Serialize(zone));
    }
}
