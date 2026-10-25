using RimGovernor.Brain.GameAccess;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class TargetReadOwnershipTests
{
    [Fact]
    public void BoundsRetainTheFreshNativeMapSize()
    {
        var size = new RimGovernor.Brain.GameFacts.Observations.MapSize { Width = 10, Height = 20 };
        var read = new MapBounds { Observed = size };
        Assert.Same(size, read.Observed);
        Assert.Equal(new RimGovernor.Brain.Policy.Bounds(10, 20), read.Bounds);
    }

    [Fact]
    public void SculptureViewRetainsNativePackedItemAndQuality()
    {
        var quality = new RimGovernor.Brain.GameFacts.Observations.PackedQuality.Rated { Category = RimGovernor.Brain.GameFacts.Defs.QualityCategory.Good };
        var packed = new RimGovernor.Brain.GameFacts.Observations.PackedBuilding { Id = "sculpture", DefName = "SculptureSmall", Quality = quality, MarketValue = 300 };
        var item = new RimGovernor.Brain.GameFacts.Observations.StockItem { Id = "minified", Cell = new(1, 2), Packed = packed };
        var sculpture = new RimGovernor.Brain.Policy.PackedSculpture { Observed = item };

        Assert.Same(item, sculpture.Observed);
        Assert.Same(packed, sculpture.Packed);
        Assert.Same(quality, sculpture.Packed.Quality);
        Assert.Equal(("minified", "SculptureSmall", (int)RimGovernor.Brain.GameFacts.Defs.QualityCategory.Good, 300d),
            (sculpture.Id, sculpture.Def, sculpture.Quality, sculpture.MarketValue));
        var unrated = sculpture with { Observed = item with { Packed = packed with { Quality = new RimGovernor.Brain.GameFacts.Observations.PackedQuality.Unrated() } } };
        Assert.IsType<RimGovernor.Brain.GameFacts.Observations.PackedQuality.Unrated>(unrated.Packed.Quality);
        Assert.Equal(-1, unrated.Quality);
    }

    [Fact]
    public void FreshTargetReadsRetainBuildingStateAndNativeEmptyCrop()
    {
        var row = PowerBuildingFixtures.Trader("bed", "Bed", new(1, 2), 0, 0, false);
        row = row with { Settings = row.Settings with { Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.Bed(true, false) } };
        var bed = new BedUseTarget { Observed = row };
        Assert.Same(row, bed.Observed);
        Assert.True(bed.Medical);
        Assert.False(bed.Prisoners);
        var changed = bed with { Observed = row with { Settings = row.Settings with { Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.Bed(false, true) } } };
        Assert.False(changed.Medical);
        Assert.True(changed.Prisoners);

        var grower = row with { Settings = row.Settings with { Control = new RimGovernor.Brain.GameFacts.Observations.BuildingControl.Grower(null) } };
        var target = new GrowerCropTarget { Observed = grower };
        Assert.Same(grower, target.Observed);
        Assert.Null(target.Crop);
        Assert.Throws<ObservationContractException>(() => (new BedUseTarget { Observed = grower }).Medical);
        Assert.Throws<ObservationContractException>(() => (new GrowerCropTarget { Observed = row }).Crop);
    }
}
