using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using D = RimGovernor.Contracts.Wire.Defs;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3779: the trader census, session, sheet and acquisition reads are plain required records whose missing states are
// named union cases; each round-trips exactly through the wire JSON.
public sealed class TradeFamilyWireTests
{
    private static readonly C.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new C.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 10 };

    private static readonly C.TradeTarget Target = new() { Kind = new C.MapTradeTarget { TraderId = "Human7" } };

    private static O.TradeLine Line(string id, string def, O.TradePawn? pawn, O.TradeThingGrade grade) => new()
    {
        LineId = id, DefName = def, Stuff = "", ColonyCount = 3, TraderCount = 9, BuyPrice = 2.5, SellPrice = 1.25,
        TraderWillTrade = true, Currency = def == "Silver", Pawn = pawn,
        TransferCount = 0, MinimumCount = -3, MaximumCount = 9, ThingId = pawn is null ? "Thing_1" : "", Grade = grade,
        ZoneId = "",
    };

    private static O.TradePawn Pawn(string id, O.TradePawnGuest guest) => new()
    {
        Id = id, Gender = D.Gender.Female, Skills = [new O.Skill { DefName = "Shooting", Level = 6 }], ViolenceCapable = true,
        Guest = guest, Downed = false, ExtraHomeFactionId = "", ExtraHostFactionId = "Faction_3",
    };

    private static readonly O.TradersSnapshot Census = new()
    {
        Context = Context,
        Traders =
        [
            new O.Trader { Id = "Human7", Orbital = false, Kind = "Caravan_Outlander_BulkGoods", FactionId = "Faction_3", CanTrade = true, Travelling = false, GoodsStacks = 40 },
            new O.Trader { Id = "Ship_2", Orbital = true, Kind = "Orbital_Exotic", FactionId = "", CanTrade = false, Travelling = false, GoodsStacks = 12 },
        ],
        Negotiators = [new PawnId("Human1")],
    };

    private static readonly O.TradeSession Engaged = new() { Context = Context, Engagement = new O.TradeEngagement.Engaged(Target, new PawnId("Human1"), true) };

    private static readonly O.TradeSession Idle = new() { Context = Context, Engagement = new O.TradeEngagement.Idle() };

    private static readonly O.TradeSheet Counted = new()
    {
        Context = Context, SessionId = "session-1", Target = Target, Negotiator = new PawnId("Human1"), GiftMode = false,
        CanTradeNow = true, Balance = new O.TradeBalance.Counted(-40, true), TraderHasEnoughSilver = true,
        Lines =
        [
            Line("silver", "Silver", null, new O.TradeThingGrade.Ungraded()),
            Line("parka", "Apparel_Parka", null, new O.TradeThingGrade.Graded(3, 0.8)),
            Line("prisoner", "Human", Pawn("Human9", new O.TradePawnGuest.Tracked(D.GuestStatus.Prisoner, true)), new O.TradeThingGrade.Ungraded()),
            Line("slave", "Human", Pawn("Human10", new O.TradePawnGuest.NoGuestTracker()), new O.TradeThingGrade.Ungraded()),
        ],
        DealSignature = "deal", CurrencyKind = D.TradeCurrency.Silver,
    };

    private static readonly O.TradeSheet Uncounted = new()
    {
        Context = Context, SessionId = "session-2", Target = Target, Negotiator = new PawnId("Human1"), GiftMode = true,
        CanTradeNow = false, Balance = new O.TradeBalance.NoCurrencyLine(), TraderHasEnoughSilver = false, Lines = [],
        DealSignature = "", CurrencyKind = D.TradeCurrency.Favor,
    };

    private static readonly O.TradeAcquisition Acquisition = new()
    {
        Context = Context,
        Consoles = [new O.TradeConsole { Id = "Console_1", NegotiatorIds = ["Human1"] }],
        PassingShips = [new O.PassingTradeShip { TraderKind = "Orbital_Exotic" }],
        Requests =
        [
            new O.TradeRequestOption
            {
                FactionId = "Faction_3", Kind = C.TradeRequestKind.Caravan, TraderKind = "Caravan_Outlander_BulkGoods",
                Goodwill = 40, GoodwillCost = 15, RelationAfterPayment = "Neutral", LastRequestTick = 0,
                CooldownRemainingTicks = 0, ArrivalMaxTicks = 60000, Eligible = true, NegotiatorIds = ["Human1"],
            },
        ],
        OrbitalAvailable = true, Pack = null,
        Arrivals = [new O.TradeRequestArrival { FactionId = "Faction_3", Kind = C.TradeRequestKind.Caravan, TraderKind = "Caravan_Outlander_BulkGoods", ArrivalTick = 90000 }],
        CommsWork = [new O.TradeCommsWork { NegotiatorId = "Human1", ConsoleId = "Console_1", FactionId = "Faction_3" }],
    };

    private static void RoundTrips<T>(T payload)
    {
        var json = WireJson.Serialize(payload);
        var back = WireJson.Deserialize<T>(json);

        Assert.Equal(json, WireJson.Serialize(back));
        Assert.Equal(payload, back);
    }

    [Fact]
    public void TheTraderCensusRoundTripsExactly() => RoundTrips(Census);

    [Fact]
    public void BothSessionStatesRoundTripExactly()
    {
        RoundTrips(Engaged);
        RoundTrips(Idle);
        Assert.Contains("\"type\":\"idle\"", WireJson.Serialize(Idle));
    }

    [Fact]
    public void TheSheetWithEveryLineAndBalanceStateRoundTripsExactly()
    {
        RoundTrips(Counted);
        RoundTrips(Uncounted);
        Assert.Contains("\"type\":\"no_guest_tracker\"", WireJson.Serialize(Counted));
        Assert.Contains("\"type\":\"no_currency_line\"", WireJson.Serialize(Uncounted));
    }

    [Fact]
    public void TheAcquisitionRoundTripsExactly() => RoundTrips(Acquisition);

    // #3785: the pack estimate is Packed with both legs, or Unpackable; a leg is Reachable with its ticks or Unreachable.
    [Fact]
    public void EveryPackEstimateStateRoundTripsExactly()
    {
        var packed = Acquisition with
        {
            Pack = new O.TradePackEstimate.Packed(84.5, 210, 6.25, 12.5, new O.PackRoute.Reachable(90000), new O.PackRoute.Unreachable()),
        };
        var refused = Acquisition with { Pack = new O.TradePackEstimate.Unpackable() };
        RoundTrips(packed);
        RoundTrips(refused);
        Assert.Contains("\"type\":\"packed\"", WireJson.Serialize(packed));
        Assert.Contains("\"type\":\"unreachable\"", WireJson.Serialize(packed));
        Assert.Contains("\"type\":\"unpackable\"", WireJson.Serialize(refused));
    }
}
