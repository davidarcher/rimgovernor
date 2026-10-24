using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests.Game;

// #3837: the buildings the babies' play uses are read from the BabyPlayDef rows' giver classes, the way a load
// carries them; a Core-only load has none, and a giver class the read does not know throws.
public sealed class PlayBuildingsTests
{
    [Fact]
    public void TheToyGiverPlaysAtTheToyBoxAndTheOthersAtNothing()
    {
        var catalog = Catalog(
            new ThingDef { defName = nameof(ThingDefOf.ToyBox) },
            Play("PlayToys", typeof(BabyPlayGiver_PlayToys)),
            Play("PlayStatic", typeof(BabyPlayGiver_PlayStatic)),
            Play("PlayWalking", typeof(BabyPlayGiver_PlayWalking)));

        Assert.Equal([nameof(ThingDefOf.ToyBox)], catalog.PlayBuildings());
        // The planning views give the toy box the Toy role the nursery and playroom furnish by.
        Assert.Equal([FurnitureRole.Toy.Value], catalog.GameRoomRoles(nameof(ThingDefOf.ToyBox)));
    }

    [Fact]
    public void ACoreOnlyLoadHasNoPlayBuilding()
    {
        var catalog = Catalog();

        Assert.Empty(catalog.PlayBuildings());
    }

    [Fact]
    public void AGiverClassTheReadDoesNotKnowThrows()
    {
        var catalog = Catalog(Play("PlayModded", typeof(BabyPlayGiver)));

        Assert.Throws<CatalogException>(catalog.PlayBuildings);
    }

    [Fact]
    public void AToyGiverWithoutTheToyBoxRowThrows()
    {
        var catalog = Catalog(Play("PlayToys", typeof(BabyPlayGiver_PlayToys)));

        Assert.Throws<CatalogException>(catalog.PlayBuildings);
    }

    private static BabyPlayDef Play(string name, Type giver) => new() { defName = name, workerClass = giver };

    private static DefinitionCatalog Catalog(params Def[] extra) =>
        new(
            [
                new ThingDef { defName = "Silver" },
                new TerrainDef { defName = "Soil" },
                .. extra,
            ],
            DefStatTable.Empty,
            new CatalogDerived("Silver", "Wort", 10f));
}
