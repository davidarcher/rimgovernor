using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3592: a barracks holding one owned bed reads as its owner's solo bedroom, so the vacant spot or bunk staged beside
/// it for a bedless colonist was removed as the owner's spare, and the bedless colonist never got a bed. A spare bed
/// stands while a colonist owns none; with everyone housed it is removed as before.
/// </summary>
public sealed class BedReplacementBedlessTests
{
    private static readonly PawnId Owner = new("owner");

    private static readonly PawnId Other = new("other");

    private static SleepingObservation Barracks(string otherBed) => new()
    {
        People = [new SleepingPerson { Id = Owner, OwnedBed = Known("bed-owner") }, new SleepingPerson { Id = Other, OwnedBed = Known(otherBed) }],
        Beds =
        [
            new SleepingBed { Id = "bed-owner", Definition = new Resource("Bed"), Humanlike = true, Room = Known("dorm"), Owners = [Owner] },
            new SleepingBed { Id = "spot", Definition = new Resource(SleepingRules.SleepingSpotDefinition), Humanlike = true, Room = Known("dorm"), Owners = [] },
            new SleepingBed { Id = "bed-other", Definition = new Resource("Bed"), Humanlike = true, Room = Known("other-room"), Owners = otherBed == "" ? [] : [Other] },
        ],
        Rooms = Known<EquatableArray<UpkeepRoom>>(
        [
            new UpkeepRoom { Id = "dorm", Role = RoomRole.Bedroom, Beds = ["bed-owner", "spot"], Quality = new RoomQuality(0, 0, 0, 0, 0), Cells = 0 },
            new UpkeepRoom { Id = "other-room", Role = RoomRole.Bedroom, Beds = ["bed-other"], Quality = new RoomQuality(0, 0, 0, 0, 0), Cells = 0 },
        ]),
    };

    private static (BedReplacement Step, bool Ok) Next(SleepingObservation obs) =>
        RoomQualityRules.NextBedReplacement(obs, new Dictionary<string, RoomTarget>(), [], _ => true, new BedMaterials(), new PreferencesRecord());

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
    }
}
