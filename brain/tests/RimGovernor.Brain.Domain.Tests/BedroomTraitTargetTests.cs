using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The bedroom impressiveness a trait asks for: a Greedy owner's room climbs to slightly impressive, a Jealous owner's
/// to the best other bedroom, and an all-Ascetic room is capped below decent and never upgraded.
/// </summary>
public sealed class BedroomTraitTargetTests
{
    private static readonly ImpressivenessLevels Levels = new() { Dull = 20, Mediocre = 30, Decent = 40, SlightlyImpressive = 50 };

    private static readonly PawnId Owner = new("owner");

    private static readonly PawnId Other = new("other");

    private static SleepingObservation Bedrooms(double ownerRoom, double otherRoom) => new()
    {
        People = [new SleepingPerson { Id = Owner }, new SleepingPerson { Id = Other }],
        Beds =
        [
            new SleepingBed { Id = "bed-owner", Humanlike = true, Room = Known("room-owner"), Owners = [Owner] },
            new SleepingBed { Id = "bed-other", Humanlike = true, Room = Known("room-other"), Owners = [Other] },
        ],
        Rooms = Known<EquatableArray<UpkeepRoom>>(
        [
            new UpkeepRoom { Id = "room-owner", Role = "Bedroom", Beds = ["bed-owner"], Quality = Known(new RoomQuality(0, 0, 0, 0, ownerRoom)) },
            new UpkeepRoom { Id = "room-other", Role = "Bedroom", Beds = ["bed-other"], Quality = Known(new RoomQuality(0, 0, 0, 0, otherRoom)) },
        ]),
    };

    private static RoomTarget Target(TraitEffects owner, double otherRoom = 0, TraitEffects? other = null) =>
        RoomQualityRules.RoomQualityTargets(
            Bedrooms(0, otherRoom),
            new Dictionary<PawnId, TraitEffects> { [Owner] = owner, [Other] = other ?? new TraitEffects() },
            TechTier.Camp,
            Levels)["room-owner"];

    [Fact]
    public void GreedyOwnerAsksSlightlyImpressive()
    {
        var target = Target(new TraitEffects { Greedy = true });

        Assert.Equal(Levels.SlightlyImpressive, target.Min);
        Assert.Contains("greedy", target.Reasons);
        Assert.False(target.NeverUpgrade);
    }

    [Fact]
    public void JealousOwnerAsksTheBestOtherBedroom()
    {
        var target = Target(new TraitEffects { Jealous = true }, otherRoom: 35);

        Assert.Equal(35, target.Min);
        Assert.Contains("jealous", target.Reasons);
    }

    [Fact]
    public void AsceticRoomIsCappedBelowDecentAndNeverUpgraded()
    {
        var targets = RoomQualityRules.RoomQualityTargets(
            Bedrooms(0, 35),
            new Dictionary<PawnId, TraitEffects> { [Owner] = new() { Jealous = true }, [Other] = new() { Ascetic = true } },
            TechTier.Camp,
            Levels);

        var ascetic = targets["room-other"];
        Assert.Equal(0, ascetic.Min);
        Assert.Equal(Levels.Decent, ascetic.Max);
        Assert.True(ascetic.NeverUpgrade);
        Assert.Equal(35, targets["room-owner"].Min);
    }
}
