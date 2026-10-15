using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Normal unsupported trade labor and explicit paid native trade are separate claims.</summary>
internal static class TradeRounds
{
    internal const int Silver = 400;
    internal static FixtureStart RoutineFixture() => new()
    {
        On = new LabStart(), Op = "test/trade_fixture",
        Args = new JsonObject { ["action"] = "routine_setup", ["silver"] = Silver, ["medicine"] = 30 }
    };

    internal static AcceptanceCase Case() => new()
    {
        Name = "trade/routine",
        Scope = "Prepared medicine deficit remains unsupported for automatic negotiator labor; explicit native opening, priced medicine purchase and closed session prove actual goods and silver transfer, not autonomous selection.",
        Start = RoutineFixture(),
        Serve = new ServeSpec { Families = ["trade"], NativeTimeout = TimeSpan.FromSeconds(15), Prefix = "trade-routine" },
        Budget = TimeSpan.FromMinutes(5), Crew = new Crew { Size = 3 },
        Run = (s, ct) => RunPreparedAsync(s, true, ct)
    };

    internal static async Task RunPreparedAsync(ISession s, bool purchase, CancellationToken ct)
    {
        var prepared = s.Prepared ?? [];
        if (!Flag(prepared["success"]) || Num(prepared["colonyMedicine"]) != 0)
            throw new AcceptanceException("Invalid medicine-deficit fixture: " + prepared.ToJson());
        s.Report["prepared"] = prepared.DeepClone();
        var h = await UnsupportedLabor.ReviewAsync(s, "unsupported_shared_trade_labor", ActionKind.Trade, ct).ConfigureAwait(false);
        var seller = Rows(prepared["stocked"]).First(r => Flag(r["listed"]))["traderId"].AsString();
        var snapshot = await h.CallAsync("negotiator", "test/trade_fixture", new JsonObject { ["action"] = "snapshot" }, ct).ConfigureAwait(false);
        var pawn = Rows(snapshot["colonists"]).First(r => Num(r["social"]) >= 0)["id"].AsString();
        var moved = await h.CallAsync("adjacent", "test/trade_fixture",
            new JsonObject { ["action"] = "teleport_adjacent", ["traderId"] = seller, ["pawnId"] = pawn }, ct).ConfigureAwait(false);
        if (!Flag(moved["success"])) throw new AcceptanceException("No adjacent native negotiator.");
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "explicit-trade-authority", s.Identity, ct).ConfigureAwait(false);
        async Task<JsonObject> Apply(string label, string operation, JsonObject fields)
        {
            var receipt = await ApplyIntents.ApplyOneAsync(h, label, s.Identity, label,
                new JsonObject { ["trade"] = new JsonObject { ["traderId"] = seller, ["negotiatorId"] = pawn, [operation] = fields } }, ct).ConfigureAwait(false);
            return TradeKit.AppliedTrade(label, receipt);
        }
        Task<JsonObject> Sheet(string label, string session) => TradeKit.ObserveAsync(h, label,
            "observations_read_trade_sheet", s.Identity, new JsonObject { ["sessionId"] = session }, ct);
        var opened = await Apply("explicit-open", "open", new JsonObject { ["giftMode"] = false }).ConfigureAwait(false);
        var session = opened["sessionId"].AsString();
        var before = await Sheet("priced-before", session).ConfigureAwait(false);
        var medicine = Rows(before["lines"]).FirstOrDefault(r => r["definition"].AsMap()?["defName"].AsString() == "MedicineHerbal"
            && Num(r["maximumCount"]) >= 1 && Num(r["buyPrice"]) > 0)
            ?? throw new AcceptanceException("No priced herbal medicine: " + before.ToJson());
        s.Report["priced_medicine"] = medicine.DeepClone();
        _ = await Apply("explicit-stage", "setLines", new JsonObject { ["lines"] = new JsonArray(
            new JsonObject { ["lineId"] = medicine["lineId"].AsString(), ["absoluteCount"] = 1 }) }).ConfigureAwait(false);
        var staged = await Sheet("priced-staged", session).ConfigureAwait(false);
        if (purchase)
        {
            var result = await Apply("explicit-accept", "accept",
                new JsonObject { ["expectedDealSignature"] = staged["dealSignature"].AsString() }).ConfigureAwait(false);
            if (!Flag(result["executed"]) || !Flag(result["actuallyTraded"]) || !Flag(result["closed"])
                || Num(result["afterSilver"]) >= Num(result["beforeSilver"]))
                throw new AcceptanceException("No paid native purchase: " + result.ToJson());
            s.Report["purchase"] = result.DeepClone();
        }
        else
            _ = await Apply("explicit-cancel", "end", new JsonObject { ["kind"] = "END_TRADE_KIND_CANCEL" }).ConfigureAwait(false);
        var state = await TradeKit.ObserveAsync(h, "closed", "observations_read_trade_session", s.Identity, null, ct).ConfigureAwait(false);
        if (Flag(state["open"])) throw new AcceptanceException("Native session remained open.");
        var facts = await TradeKit.ObserveAsync(h, "inventory", "observations_read_colony_facts", s.Identity, null, ct).ConfigureAwait(false);
        var rows = Rows(facts["resources"]).ToArray();
        var silver = rows.Where(r => r["defName"].AsString() == "Silver").Sum(r => Num(r["units"]));
        var medicineUnits = rows.Where(r => r["defName"].AsString() == "MedicineHerbal").Sum(r => Num(r["units"]));
        s.Report["inventory"] = new JsonObject { ["silver"] = silver, ["medicine"] = medicineUnits };
        if (purchase ? medicineUnits != 1 || silver >= Silver : medicineUnits != 0 || silver != Silver)
            throw new AcceptanceException("Native inventory disagrees with explicit purchase/cancel: " + s.Report["inventory"].ToJson());
    }
}
