using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;
using Rectangle = RimGovernor.Brain.Policy.Rectangle;
using RoomStat = RimGovernor.Brain.GameFacts.Observations.RoomStat;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RoomsTotalTests
{
    [Fact]
    public void MissingRoomsFailsTheDecoder()
    {
        var tables = new Tables(default, default, default, []);
        var failure = Assert.Throws<BridgeContractException>(() => ColonyPlanningReads.RoomsOf(tables, null!));
        Assert.Contains("rooms", failure.Message);
    }

    private static RoyalRung Rung => new()
    {
        Title = "title", Seniority = 1, FavorNeeded = 6, Throne = new(),
        BedroomMinImpressiveness = 0, BedroomThings = [],
    };

    private static TitleClaimFacts Claim(RoyalRung rung, int favor = 6) => new()
    {
        Rooms = RoomTestFacts.NoRooms,
        Royalty = new RoyaltyFacts
        {
            Ladder = [rung],
            Holders = new Dictionary<PawnId, EquatableArray<PawnRoyalHolding>>
            {
                [new PawnId("holder")] = [new PawnRoyalHolding { FactionDef = "empire", Title = null, Favor = favor, PermitPoints = 0, Permits = [], PermitCooldowns = [] }],
            },
        },
    };

    [Fact]
    public void EmptyLadderAndInsufficientFavorHaveNoClaim()
    {
        Assert.Equal(TitleClaimReason.NoFavor, TitleClaimRules.NextTitleClaim(new() { Rooms = RoomTestFacts.NoRooms }).Reason);
        Assert.Equal(TitleClaimReason.NoFavor, TitleClaimRules.NextTitleClaim(Claim(Rung, 5)).Reason);
    }

    [Fact]
    public void MissingBedroomAndMissingThroneNameTheirDeficits()
    {
        Assert.Equal(TitleClaimReason.BedroomUnmet, TitleClaimRules.NextTitleClaim(Claim(Rung with { BedroomMinImpressiveness = 10 })).Reason);
        Assert.Equal(TitleClaimReason.ThroneUnmet, TitleClaimRules.NextTitleClaim(Claim(Rung with { Throne = new() { Things = ["throne"], MinArea = 4 } })).Reason);
    }

    [Fact]
    public void SatisfiedRequirementsPermitTheClaim()
    {
        var claim = TitleClaimRules.NextTitleClaim(Claim(Rung));
        Assert.True(claim.Claim);
        Assert.Equal(TitleClaimReason.None, claim.Reason);
    }

    [Fact]
    public void MissingBedroomFurnitureFailsInsteadOfInventingAnUnknownVerdict()
    {
        var facts = Claim(Rung with { BedroomThings = [new() { AnyOf = [new Resource("dresser")], Count = 1 }] }) with
        {
            Sleeping = new()
            {
                Beds = [new SleepingBed { Native = SleepingNativeFixtures.Bed(id: "bed", room: "room", owners: [new PawnId("holder")]), Building = SleepingNativeFixtures.Building(id: "bed"), Humanlike = true }],
                Rooms = [new UpkeepRoom { StatKeys = SleepingNativeFixtures.StatKeys, Native = SleepingNativeFixtures.Room("room", RoomRole.Bedroom, new RoomQuality(4, 0, 0, 0, 10), 4), Beds = ["bed"] }],
            },
        };
        Assert.Equal("royal bedroom furniture", Assert.Throws<RequiredFactException>(() => TitleClaimRules.NextTitleClaim(facts)).Fact);
    }
    private static TitleClaimFacts Throne(double? impressiveness)
    {
        var region = RegionRules.RectRegion(new Rectangle(10, 10, 2, 2));
        var room = new PlannedRoom { Id = 1, Role = PlannedRole.Throne, Region = region };
        var row = RoomFixtures.State("throne-room", RoomRole.ThroneRoom, stats: impressiveness is { } value
            ? [new RoomStat { DefName = nameof(RoomQuality.Impressiveness), Value = value }] : []);
        return Claim(Rung with { Throne = new() { Things = ["throne"], MinArea = 4, MinImpressiveness = 10 } }) with
        {
            Plan = new LayoutPlan { Rooms = [room] },
            Rooms = RoomFixtures.Census((row, region.Cells())),
            Built = [ConstructionObservationFixtures.Built(new Building("throne", new(10, 10), Rotation.North, ""), "throne", [new(10, 10)])],
        };
    }

    [Fact]
    public void AThroneWithoutSleepingBedsUsesItsOwnNativeImpressiveness()
    {
        var facts = Throne(10);
        Assert.Empty(facts.Sleeping.Rooms);
        Assert.True(TitleClaimRules.NextTitleClaim(facts).Claim);
        Assert.Equal(TitleClaimReason.ThroneUnmet, TitleClaimRules.NextTitleClaim(Throne(9)).Reason);
    }

    [Fact]
    public void MissingThroneImpressivenessFailsTheRequiredRead() =>
        Assert.Equal("throne room impressiveness", Assert.Throws<RequiredFactException>(() => TitleClaimRules.NextTitleClaim(Throne(null))).Fact);

    [Fact]
    public void MissingNativeRoomQualityStatFailsTheRead()
    {
        var census = new RoomsSnapshot { Context = ColonyFactsCoreTests.Snapshot().Context, Rooms = [RoomFixtures.State("dining", RoomRole.DiningRoom)] };
        Assert.Throws<ObservationContractException>(() => SleepingUpkeepReads.UpkeepRooms(census, []));
    }
}
