using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3779: the trader census, session, sheet and acquisition reads are plain required records whose missing states are
// named union cases; each round-trips exactly through the wire JSON.
public sealed class TradeFamilyWireTests
{
    private static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 10 };

    private static readonly RimGovernor.Brain.GameFacts.Common.TradeTarget Target = new() { Kind = new RimGovernor.Brain.GameFacts.Common.MapTradeTarget { TraderId = "Human7" } };

    private static RimGovernor.Brain.GameFacts.Observations.TradeLine Line(string id, string def, RimGovernor.Brain.GameFacts.Observations.TradePawn? pawn, RimGovernor.Brain.GameFacts.Observations.TradeThingGrade grade) => new()
    {
        LineId = id, DefName = def, Stuff = "", ColonyCount = 3, TraderCount = 9, BuyPrice = 2.5, SellPrice = 1.25,
        TraderWillTrade = true, Currency = def == "Silver", Pawn = pawn,
        TransferCount = 0, MinimumCount = -3, MaximumCount = 9, ThingId = pawn is null ? "Thing_1" : "", Grade = grade,
        ZoneId = "",
    };

    private static RimGovernor.Brain.GameFacts.Observations.TradePawn Pawn(string id, RimGovernor.Brain.GameFacts.Observations.TradePawnGuest guest) => new()
    {
        Id = id, Gender = RimGovernor.Brain.GameFacts.Defs.Gender.Female, Skills = [new RimGovernor.Brain.GameFacts.Observations.Skill { DefName = "Shooting", Level = 6, Passion = RimGovernor.Brain.GameFacts.Observations.Passion.None }], ViolenceCapable = true,
        Guest = guest, Downed = false, ExtraHomeFactionId = "", ExtraHostFactionId = "Faction_3",
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.TradersSnapshot Census = new()
    {
        Context = Context,
        Traders =
        [
            new RimGovernor.Brain.GameFacts.Observations.Trader { Id = "Human7", Orbital = false, Kind = "Caravan_Outlander_BulkGoods", FactionId = "Faction_3", CanTrade = true, Travelling = false, GoodsStacks = 40,
                Visit = new(new("Human1"), new(2, 3), [new("MedicineHerbal", 5, 10)]) },
            new RimGovernor.Brain.GameFacts.Observations.Trader { Id = "Ship_2", Orbital = true, Kind = "Orbital_Exotic", FactionId = "", CanTrade = false, Travelling = false, GoodsStacks = 12 },
        ],
        Negotiators = [new PawnId("Human1")],
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.TradeSession Engaged = new() { Context = Context, Engagement = new RimGovernor.Brain.GameFacts.Observations.TradeEngagement.Engaged(Target, new PawnId("Human1"), true) };

    private static readonly RimGovernor.Brain.GameFacts.Observations.TradeSession Idle = new() { Context = Context, Engagement = new RimGovernor.Brain.GameFacts.Observations.TradeEngagement.Idle() };

    private static readonly RimGovernor.Brain.GameFacts.Observations.TradeSheet Counted = new()
    {
        Context = Context, SessionId = "session-1", Target = Target, Negotiator = new PawnId("Human1"), GiftMode = false,
        CanTradeNow = true, Balance = new RimGovernor.Brain.GameFacts.Observations.TradeBalance.Counted(-40, true), TraderHasEnoughSilver = true,
        Lines =
        [
            Line("silver", "Silver", null, new RimGovernor.Brain.GameFacts.Observations.TradeThingGrade.Ungraded()),
            Line("parka", "Apparel_Parka", null, new RimGovernor.Brain.GameFacts.Observations.TradeThingGrade.Graded(3, 0.8)),
            Line("prisoner", "Human", Pawn("Human9", new RimGovernor.Brain.GameFacts.Observations.TradePawnGuest.Tracked(RimGovernor.Brain.GameFacts.Defs.GuestStatus.Prisoner, true)), new RimGovernor.Brain.GameFacts.Observations.TradeThingGrade.Ungraded()),
            Line("slave", "Human", Pawn("Human10", new RimGovernor.Brain.GameFacts.Observations.TradePawnGuest.NoGuestTracker()), new RimGovernor.Brain.GameFacts.Observations.TradeThingGrade.Ungraded()),
        ],
        DealSignature = "deal", CurrencyKind = RimGovernor.Brain.GameFacts.Defs.TradeCurrency.Silver,
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.TradeSheet Uncounted = new()
    {
        Context = Context, SessionId = "session-2", Target = Target, Negotiator = new PawnId("Human1"), GiftMode = true,
        CanTradeNow = false, Balance = new RimGovernor.Brain.GameFacts.Observations.TradeBalance.NoCurrencyLine(), TraderHasEnoughSilver = false, Lines = [],
        DealSignature = "", CurrencyKind = RimGovernor.Brain.GameFacts.Defs.TradeCurrency.Favor,
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.TradeAcquisition Acquisition = new()
    {
        Context = Context,
        Consoles = [new RimGovernor.Brain.GameFacts.Observations.TradeConsole { Id = "Console_1", NegotiatorIds = ["Human1"] }],
        PassingShips = [new RimGovernor.Brain.GameFacts.Observations.PassingTradeShip { TraderKind = "Orbital_Exotic" }],
        Requests =
        [
            new RimGovernor.Brain.GameFacts.Observations.TradeRequestOption
            {
                ConsoleId = "Console_1", ConsoleCell = new(1, 2), FactionId = "Faction_3", Kind = RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Caravan, TraderKind = "Caravan_Outlander_BulkGoods",
                Goodwill = 40, GoodwillCost = 15, RelationAfterPayment = "Neutral", LastRequestTick = 0,
                CooldownRemainingTicks = 0, ArrivalMaxTicks = 60000, Eligible = true, NegotiatorIds = ["Human1"],
            },
        ],
        OrbitalAvailable = true, Pack = null,
        Arrivals = [new RimGovernor.Brain.GameFacts.Observations.TradeRequestArrival { FactionId = "Faction_3", Kind = RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Caravan, TraderKind = "Caravan_Outlander_BulkGoods", ArrivalTick = 90000 }],
        CommsWork = [new RimGovernor.Brain.GameFacts.Observations.TradeCommsWork { NegotiatorId = "Human1", ConsoleId = "Console_1", FactionId = "Faction_3" }],
    };

    private static void RoundTrips<T>(T payload)
    {
        var json = OrdersJson.Serialize(payload);
        var back = OrdersJson.Deserialize<T>(json);

        Assert.Equal(json, OrdersJson.Serialize(back));
        Assert.Equal(payload, back);
    }

    [Fact]
    public void TheTraderCensusRoundTripsExactly() => RoundTrips(Census);

    [Fact]
    public void BothSessionStatesRoundTripExactly()
    {
        RoundTrips(Engaged);
        RoundTrips(Idle);
        Assert.Contains("\"type\":\"idle\"", OrdersJson.Serialize(Idle));
    }

    [Fact]
    public void TheSheetWithEveryLineAndBalanceStateRoundTripsExactly()
    {
        RoundTrips(Counted);
        RoundTrips(Uncounted);
        Assert.Contains("\"type\":\"no_guest_tracker\"", OrdersJson.Serialize(Counted));
        Assert.Contains("\"type\":\"no_currency_line\"", OrdersJson.Serialize(Uncounted));
    }

    [Fact]
    public void TheAcquisitionRoundTripsExactly() => RoundTrips(Acquisition);

    // #3785: the pack estimate is Packed with both legs, or Unpackable; a leg is Reachable with its ticks or Unreachable.
    [Fact]
    public void EveryPackEstimateStateRoundTripsExactly()
    {
        var packed = Acquisition with
        {
            Pack = new RimGovernor.Brain.GameFacts.Observations.TradePackEstimate.Packed(84.5, 210, 6.25, 12.5, new RimGovernor.Brain.GameFacts.Observations.PackRoute.Reachable(90000), new RimGovernor.Brain.GameFacts.Observations.PackRoute.Unreachable()),
        };
        var refused = Acquisition with { Pack = new RimGovernor.Brain.GameFacts.Observations.TradePackEstimate.Unpackable() };
        RoundTrips(packed);
        RoundTrips(refused);
        Assert.Contains("\"type\":\"packed\"", OrdersJson.Serialize(packed));
        Assert.Contains("\"type\":\"unreachable\"", OrdersJson.Serialize(packed));
        Assert.Contains("\"type\":\"unpackable\"", OrdersJson.Serialize(refused));
    }
}
