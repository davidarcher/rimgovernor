using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>trade/settlement-roundtrip</c> (Go <c>settlement_roundtrip.go</c>): autonomous acquisition in one Core world, a home
/// nutrition shortage and a nearby legal settlement cause packing, departure, world travel, live-priced purchase and
/// home return. No fixture creates a Project, caravan, trade or return order.
/// </summary>
internal static class TradeSettlementRoundtrip
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "trade/settlement-roundtrip",
        Scope = "Autonomous acquisition in one Core world: a home nutrition shortage and a nearby legal settlement cause normal controller packing, departure, world travel, live-priced purchase and home return. A Go snapshot cannot prove native packing, travel, settlement transfer or home entry. Only native departed crew, paid seller stock transferred to caravan cargo, and the same crew home with previously absent purchased meals establish success. No fixture creates a Project, caravan, trade or return order.",
        Start = new FixtureStart
        {
            On = new FixtureStart { On = new LabStart { Colonists = 6 }, Op = "test/food_channels_prepare" },
            Op = "test/trade_fixture",
            Args = new JsonObject { ["action"] = "roundtrip_setup" },
        },
        RequiredOps = ["test/trade_fixture"],
        Quiet = QuietMode.Required,
        Serve = new ServeSpec
        {
            Families = ["acquisition", "supply", "trade", "work"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "trade-roundtrip",
        },
        Budget = TimeSpan.FromMinutes(8),
        Crew = new Crew { Size = 6 },

        // Native purchase/departure observations are required in order, so a suffix resumed after those observations
        // cannot claim the complete chain.
        NoCheckpoint = true,
        Run = RunAsync,
    };

    private static List<string> Ids(JsonNode? raw) => [.. raw.AsSlice().Select(value => value.AsString())];

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var prepared = s.Prepared ?? [];
        if (!Flag(prepared["success"]) || Num(prepared["homeMeals"]) != 0 || Num(prepared["sellerMeals"]) <= 0 || Ids(prepared["crew"]).Count != 6)
        {
            throw new AcceptanceException($"invalid roundtrip precondition: {prepared.ToJson()}");
        }

        var seller = prepared["traderId"].AsString();
        var sellerBefore = Num(prepared["sellerMeals"]);
        var settlementTile = Num(prepared["settlementTile"]);
        var homeCrew = Ids(prepared["crew"]);
        List<string> departedCrew = [];
        var caravanId = "";
        bool purchased = false, delivered = false;
        Exception? observeError = null;

        Task<JsonObject> Read(Harness h) =>
            h.CallAsync("roundtrip-native-state", "test/trade_fixture", new JsonObject { ["action"] = "roundtrip_state", ["traderId"] = seller }, cancellationToken);

        _ = await SustainedFood.ObserveAsync(
            ObservedSession.Of(s),
            new SustainedObservation
            {
                Watch = new WatchConfig
                {
                    Watch = TimeSpan.FromMinutes(6),
                    Window = 2 * 60000,
                    PollTicks = 250,
                    Concern = PolicyFunctions.EnsureFoodSupply.Value,

                    // The watch's early exit is synchronous; the native read it needs is awaited to completion here
                    // (Go made the same blocking call from inside Until).
                    Until = _ =>
                    {
                        JsonObject state;
                        try
                        {
                            state = Read(s.Harness).GetAwaiter().GetResult();
                        }
                        catch (AcceptanceException e)
                        {
                            observeError = e;
                            return true;
                        }

                        var present = Ids(state["homeCrew"]);
                        foreach (var raw in state["caravans"].AsSlice())
                        {
                            var caravan = raw.AsMap() ?? [];
                            var crew = Ids(caravan["crew"]);
                            if (caravanId.Length == 0 && crew.Count > 0 && !crew.Exists(id => !homeCrew.Contains(id) || present.Contains(id)))
                            {
                                caravanId = caravan["id"].AsString();
                                departedCrew = crew;
                                s.Report["native_departure"] = state.DeepClone();
                            }

                            if (caravan["id"].AsString() == caravanId && caravanId.Length > 0 && Num(caravan["tile"]) == settlementTile
                                && Num(caravan["meals"]) > 0 && Num(state["sellerMeals"]) < sellerBefore && Num(state["sellerSilver"]) > 10000)
                            {
                                purchased = true;
                                s.Report["native_purchase"] = state.DeepClone();
                            }
                        }

                        if (purchased && departedCrew.Count > 0 && !departedCrew.Exists(id => !present.Contains(id)) && Num(state["homeMeals"]) > 0)
                        {
                            delivered = true;
                            s.Report["native_delivery"] = state.DeepClone();
                        }

                        return delivered;
                    },
                },
                Audit = async (h, report, ct) =>
                {
                    if (observeError is not null)
                    {
                        throw observeError;
                    }

                    var state = await Read(h).ConfigureAwait(false);
                    report["native_final"] = state.DeepClone();
                    var present = Ids(state["homeCrew"]);
                    if (!purchased || !delivered || departedCrew.Count == 0 || departedCrew.Exists(id => !present.Contains(id)) || Num(state["homeMeals"]) <= 0)
                    {
                        throw new AcceptanceException($"roundtrip incomplete: departed=[{string.Join(' ', departedCrew)}] purchased={purchased} delivered={delivered} native={state.ToJson()}");
                    }
                },
            },
            cancellationToken).ConfigureAwait(false);
    }
}
