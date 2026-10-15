using RimGovernor.Launcher;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LauncherPresetTests
{
    [Fact]
    public void RhadamantPresetSelectsTheSeaIceBiome()
    {
        var preset = Assert.Single(NewColony.Presets, p => p.Name == "rhadamant");

        Assert.Equal(["SeaIce"], preset.Spec.Biomes);
        Assert.Equal(["SeaIce"], preset.Spec.ForWire("any-seed").Biomes);
    }

    [Fact]
    public void MapOptionsOfferSeaIceBesideIceSheet()
    {
        var biomes = NewColony.Options.Biomes.Select(b => b.Value).ToList();

        Assert.Contains("SeaIce", biomes);
        Assert.Contains("IceSheet", biomes);
    }
}
