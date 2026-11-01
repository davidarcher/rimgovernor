using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3592: a barracks holding one owned bed reads as its owner's solo bedroom, so the vacant spot or bunk staged beside
/// it for a bedless colonist was removed as the owner's spare, and the bedless colonist never got a bed. A spare bed
/// stands while a colonist owns none; with everyone housed it is a compared removal (task 8.1): removed once no expected
/// sleeper needs it, kept while its service is unread.
/// </summary>
public sealed class BedReplacementBedlessTests
{
    private static readonly PawnId Owner = new("owner");

    private static readonly PawnId Other = new("other");

    private static SleepingObservation Barracks(string otherBed) => new()
    {
        People = [new SleepingPerson { Native = SleepingNativeFixtures.Person(id: Owner), Pawn = SleepingNativeFixtures.Pawn(id: Owner, ownedBed: "bed-owner") }, new SleepingPerson { Native = SleepingNativeFixtures.Person(id: Other), Pawn = SleepingNativeFixtures.Pawn(id: Other, ownedBed: otherBed) }],
        Beds =
        [
            new SleepingBed { Native = SleepingNativeFixtures.Bed(id: "bed-owner", room: "dorm", owners: [Owner]), Building = SleepingNativeFixtures.Building(id: "bed-owner", definition: new Resource("Bed")), Humanlike = true },
            new SleepingBed { Native = SleepingNativeFixtures.Bed(id: "spot", room: "dorm", owners: []), Building = SleepingNativeFixtures.Building(id: "spot", definition: new Resource(SleepingRules.SleepingSpotDefinition)), Humanlike = true },
            new SleepingBed { Native = SleepingNativeFixtures.Bed(id: "bed-other", room: "other-room", owners: otherBed == "" ? [] : [Other]), Building = SleepingNativeFixtures.Building(id: "bed-other", definition: new Resource("Bed")), Humanlike = true },
        ],
        Rooms =
        [
            new UpkeepRoom { StatKeys = SleepingNativeFixtures.StatKeys, Native = SleepingNativeFixtures.Room("dorm", RoomRole.Bedroom, new RoomQuality(0, 0, 0, 0, 0), 0), Beds = ["bed-owner", "spot"] },
            new UpkeepRoom { StatKeys = SleepingNativeFixtures.StatKeys, Native = SleepingNativeFixtures.Room("other-room", RoomRole.Bedroom, new RoomQuality(0, 0, 0, 0, 0), 0), Beds = ["bed-other"] },
        ],
    };

    private static (BedReplacement Step, bool Ok) Next(SleepingObservation obs, TransitionService? service = null) =>
        RoomQualityRules.NextBedReplacement(obs, new Dictionary<string, RoomTarget>(), [], _ => true, new BedMaterials(), new PreferencesRecord(),
            TransitionFixtures.Facts(TransitionFixtures.Mechanics("spot", SleepingRules.SleepingSpotDefinition, default, minifiable: false)),
            service ?? new TransitionService.Assessed(0, 0));

    [Fact]
    public void SpareBedStandsWhileAColonistOwnsNone()
    {
        var (step, ok) = Next(Barracks(otherBed: ""));

        Assert.False(ok && step.Step == BedReplacementStep.Remove, $"removed {step.Bed} while a colonist owns no bed");
    }

    [Fact]
    public void SpareBedIsRemovedOnceEveryoneOwnsOne()
    {
        var (step, ok) = Next(Barracks(otherBed: "bed-other"));

        Assert.True(ok);
        Assert.Equal(BedReplacementStep.Remove, step.Step);
        Assert.Equal("spot", step.Bed);
        // A sleeping spot cannot be minified: the compared removal deconstructs it.
        Assert.False(step.Uninstall);
        Assert.Null(step.Hold);
        // With no expected sleeper read, the spare's service is unassessed and it stays.
        Assert.False(Next(Barracks(otherBed: "bed-other"), new TransitionService.Unassessed("arrival tick:q1")) is ({ Step: BedReplacementStep.Remove }, true));
    }
}
