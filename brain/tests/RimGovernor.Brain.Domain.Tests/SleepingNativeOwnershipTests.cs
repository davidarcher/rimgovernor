using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimWorld;
using Verse;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class SleepingNativeOwnershipTests
{
    [Fact]
    public void SleepingJoinsKeepTheActualPersonBedBuildingAndRoomRows()
    {
        var pawn = new PawnId("sleeper");
        var person = SleepingNativeFixtures.Person(pawn);
        var nativePawn = SleepingNativeFixtures.Pawn(pawn, "bed", 16, 26);
        var bed = SleepingNativeFixtures.Bed("bed", owners: [pawn], accessibleTo: [pawn], room: "bedroom", temperature: 21);
        var building = SleepingNativeFixtures.Building("bed", roofed: true, stuff: null, cell: new(7, 9));
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            ColonistCount = 1,
            Upkeep = UpkeepFactsTests.NoUpkeep with { People = [person], Beds = [bed] },
        };
        var observed = SleepingUpkeepReads.ColonySleeping(snapshot, Buildings.Of(building), Pawns.Of(nativePawn), Catalog());
        Assert.Same(person, Assert.Single(observed.People).Native);
        Assert.Same(nativePawn, observed.People[0].Pawn);
        Assert.Equal("bed", observed.People[0].OwnedBed);
        Assert.Equal(16, observed.People[0].ComfortableMin);
        Assert.Equal(26, observed.People[0].ComfortableMax);
        var joined = Assert.Single(observed.Beds);
        Assert.Same(bed, joined.Native);
        Assert.Same(building, joined.Building);
        Assert.Equal(new Cell(7, 9), joined.Cell);
        Assert.True(joined.Roofed);
        Assert.Null(joined.Stuff);
        Assert.True(joined.Humanlike);

        var room = SleepingNativeFixtures.Room("bedroom", RoomRole.Bedroom, new(12, 3, -1, 50, 24), 12);
        var rooms = SleepingUpkeepReads.UpkeepRooms(new RimGovernor.Brain.GameFacts.Observations.RoomsSnapshot { Context = snapshot.Context, Rooms = [room] }, observed.Beds);
        var quality = Assert.Single(rooms);
        Assert.Same(room, quality.Native);
        Assert.Equal(["bed"], quality.Beds);
        Assert.Equal(24, quality.Quality.Impressiveness);
        Assert.Equal(-1, quality.Quality.Cleanliness);
        Assert.Equal(12, quality.Cells);
    }

    [Fact]
    public void MissingOwnedBedStaysAbsentAndOwesSleeping()
    {
        var person = SleepingNativeFixtures.Person(new PawnId("sleeper"));
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            ColonistCount = 1, Upkeep = UpkeepFactsTests.NoUpkeep with { People = [person] },
        };
        var observed = SleepingUpkeepReads.ColonySleeping(snapshot, Buildings.Of(), Pawns.Of(SleepingNativeFixtures.Pawn(person.Pawn)), null);
        Assert.Null(Assert.Single(observed.People).OwnedBed);
        var review = SleepingRules.ReviewSleeping(observed, new SleepingHistory(), new Tick(600));
        Assert.Equal(person.Pawn, Assert.Single(review.Targets).Pawn);
    }

    [Fact]
    public void APersonWhosePawnRowIsMissingFailsTheObservation()
    {
        var person = SleepingNativeFixtures.Person(new PawnId("missing"));
        var snapshot = ColonyFactsCoreTests.Snapshot() with
        {
            ColonistCount = 1, Upkeep = UpkeepFactsTests.NoUpkeep with { People = [person] },
        };
        Assert.Throws<ObservationContractException>(() => SleepingUpkeepReads.ColonySleeping(snapshot, Buildings.Of(), Pawns.Of(), null));
    }

    private static DefinitionCatalog Catalog()
    {
        var bed = new ThingDef { defName = "Bed", building = new BuildingProperties { bed_humanlike = true } };
        var table = new DefStatTable(
            new Dictionary<(BuildableDef, ThingDef?), DefStatTable.Cost> { [(bed, null)] = new([], null) },
            new Dictionary<(BuildableDef, ThingDef?, CatalogStat), DefStatTable.Value> { [(bed, null, CatalogStats.BedRestEffectiveness)] = new(1, true, null) },
            new Dictionary<BuildableDef, IReadOnlyList<string>>(), new HashSet<CatalogStat>(CatalogStats.All));
        return new DefinitionCatalog([new ThingDef { defName = "Silver" }, new TerrainDef { defName = "Soil" }, bed], table, new CatalogDerived("Silver", "Wort", 10f));
    }
}
