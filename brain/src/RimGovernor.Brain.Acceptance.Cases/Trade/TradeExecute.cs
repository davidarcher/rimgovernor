using System.Text.Json.Nodes;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>trade/execute</c> (Go <c>execute.go</c>): a whole native trade on <c>test/trade_fixture</c>: the negotiator
/// teleported beside a spawned caravan trader opens a session, stages one buy line and one sell line and accepts the
/// signed deal. It asserts the accept executed and closed the session with colony silver moved, then reopens the session
/// to see the trader's stock and the colony's stock moved by the staged counts. The reopened session stages a buy the
/// colony cannot afford: the accept is refused and the session ends with a cancel.
/// </summary>
internal static class TradeExecute
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "trade/execute",
        Scope = "Native trade execution: open beside a spawned caravan trader, set one buy and one sell line, accept the signed deal, "
            + "then stock and silver moved and the session closed; an unaffordable deal's accept is refused.",
        Start = new LabStart(),
        Quiet = QuietMode.Required,
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        foreach (var required in new[] { "rimgovernor/operations_apply", "rimgovernor/observations_read_trade_session", "rimgovernor/observations_read_trade_sheet" })
        {
            if (!Dyn.Contains(s.Names, required))
            {
                throw new AcceptanceException($"missing {required} in discovery");
            }
        }

        // routine_setup gives the colony home-area silver and steel to sell and brings an outlander bulk caravan (which
        // buys steel) beside the colony.
        var incident = await h.CallAsync(
            "setup",
            "test/trade_fixture",
            new JsonObject { ["action"] = "routine_setup", ["silver"] = 400, ["steel"] = 150, ["medicine"] = 0 },
            cancellationToken).ConfigureAwait(false);
        var traderId = Texts(incident["arrival"].AsMap()?["traderIds"]).FirstOrDefault() ?? "";
        if (traderId.Length == 0)
        {
            throw new AcceptanceException($"setup: no trader caravan arrived: {incident.ToJson()}");
        }

        report["setup"] = incident;

        var snapshot = await h.CallAsync("snapshot-colonists", "test/trade_fixture", new JsonObject { ["action"] = "snapshot" }, cancellationToken).ConfigureAwait(false);
        var negotiatorId = "";
        foreach (var row in Rows(snapshot["colonists"]))
        {
            if (Num(row["social"]) >= 0)
            {
                negotiatorId = row["id"].AsString();
                break;
            }
        }

        if (negotiatorId.Length == 0)
        {
            throw new AcceptanceException($"snapshot-colonists: no socially-eligible colonist available: {snapshot.ToJson()}");
        }

        var teleport = await h.CallAsync(
            "teleport-adjacent",
            "test/trade_fixture",
            new JsonObject { ["action"] = "teleport_adjacent", ["traderId"] = traderId, ["pawnId"] = negotiatorId },
            cancellationToken).ConfigureAwait(false);
        if (teleport["success"].AsBool() != true)
        {
            throw new AcceptanceException($"teleport-adjacent refused: {teleport.ToJson()}");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> Apply(string label, JsonObject step)
        {
            var trade = new JsonObject { ["traderId"] = traderId, ["negotiatorId"] = negotiatorId };
            foreach (var (key, value) in step)
            {
                trade[key] = value?.DeepClone();
            }

            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = "trade-execute-" + label, ["trade"] = trade }),
                },
                cancellationToken).ConfigureAwait(false);
            return OneResult(label, reply);
        }

        async Task<string> Open(string label)
        {
            var result = await Apply(label, new JsonObject { ["open"] = new JsonObject { ["giftMode"] = false } }).ConfigureAwait(false);
            var trade = TradeKit.AppliedTrade(label, result);
            return trade["sessionId"].AsString().Length == 0
                ? throw new AcceptanceException($"{label}: expected a session id, got {result.ToJson()}")
                : trade["sessionId"].AsString();
        }

        Task<JsonObject> Sheet(string label, string sessionId) =>
            TradeKit.ObserveAsync(h, label, "observations_read_trade_sheet", identity, new JsonObject { ["sessionId"] = sessionId }, cancellationToken);

        static JsonObject? RowById(JsonObject sheet, string id) => Rows(sheet["lines"]).FirstOrDefault(row => row["lineId"].AsString() == id);

        static bool Plain(JsonObject row) =>
            CaseJson.Flag(row["traderWillTrade"]) && !CaseJson.Flag(row["currency"]) && !CaseJson.Flag(row["pawn"]) && row["lineId"].AsString().Length > 0;

        async Task SetLines(string label, JsonArray lines)
        {
            var result = await Apply(label, new JsonObject { ["setLines"] = new JsonObject { ["lines"] = lines } }).ConfigureAwait(false);
            _ = TradeKit.AppliedTrade(label, result);
        }

        // Step 1: open, stage one buy and one sell, accept.
        var sessionId = await Open("open").ConfigureAwait(false);
        var before = await Sheet("sheet", sessionId).ConfigureAwait(false);
        JsonObject? buy = null;
        JsonObject? sell = null;
        foreach (var row in Rows(before["lines"]))
        {
            if (!Plain(row))
            {
                continue;
            }

            if (Num(row["traderCount"]) > 0 && Num(row["maximumCount"]) >= 1 && Num(row["buyPrice"]) > 0
                && (buy is null || Num(row["buyPrice"]) < Num(buy["buyPrice"])))
            {
                buy = row;
            }

            if (Num(row["colonyCount"]) > 0 && Num(row["minimumCount"]) <= -1 && Num(row["sellPrice"]) > 0
                && (sell is null || -Num(row["minimumCount"]) * Num(row["sellPrice"]) > -Num(sell["minimumCount"]) * Num(sell["sellPrice"])))
            {
                sell = row;
            }
        }

        if (buy is null || sell is null || buy["lineId"].AsString() == sell["lineId"].AsString())
        {
            throw new AcceptanceException($"sheet: expected a distinct buy row and sell row, got buy={buy.ToJson()} sell={sell.ToJson()}");
        }

        // Sell enough to cover the one bought unit, within the colony's stock.
        var sellCount = (int)Math.Min(Math.Ceiling(Num(buy["buyPrice"]) / Num(sell["sellPrice"])) + 1, -Num(sell["minimumCount"]));
        var buyId = buy["lineId"].AsString();
        var sellId = sell["lineId"].AsString();
        report["buy"] = buy;
        report["sell"] = sell;
        report["sellCount"] = sellCount;
        await SetLines(
            "lines",
            [
                new JsonObject { ["lineId"] = buyId, ["absoluteCount"] = 1 },
                new JsonObject { ["lineId"] = sellId, ["absoluteCount"] = -sellCount },
            ]).ConfigureAwait(false);
        var staged = await Sheet("staged", sessionId).ConfigureAwait(false);
        if (!CaseJson.Flag(staged["colonyCanAfford"]))
        {
            throw new AcceptanceException($"staged: the colony cannot afford the staged deal: {staged.ToJson()}");
        }

        var accepted = await Apply("accept", new JsonObject { ["accept"] = new JsonObject { ["expectedDealSignature"] = staged["dealSignature"].AsString() } }).ConfigureAwait(false);
        var deal = TradeKit.AppliedTrade("accept", accepted);
        report["accept"] = deal;
        if (!CaseJson.Flag(deal["executed"]) || !CaseJson.Flag(deal["actuallyTraded"]) || !CaseJson.Flag(deal["closed"]))
        {
            throw new AcceptanceException($"accept: expected an executed, traded, closed deal, got {deal.ToJson()}");
        }

        if (Num(deal["beforeSilver"]) == Num(deal["afterSilver"]))
        {
            throw new AcceptanceException($"accept: colony silver did not move: {deal.ToJson()}");
        }

        var session = await TradeKit.ObserveAsync(h, "session-closed", "observations_read_trade_session", identity, null, cancellationToken).ConfigureAwait(false);
        if (CaseJson.Flag(session["open"]) || session["traderId"].AsString().Length > 0)
        {
            throw new AcceptanceException($"session-closed: expected no live trade after accept, got {session.ToJson()}");
        }

        // Stock moved: a fresh session shows the trader's bought row and the colony's sold row down by the staged counts.
        sessionId = await Open("reopen").ConfigureAwait(false);
        var after = await Sheet("after", sessionId).ConfigureAwait(false);

        JsonObject? ByDef(JsonObject sheet, JsonObject row)
        {
            var want = row["definition"].AsMap();
            foreach (var other in Rows(sheet["lines"]))
            {
                if (Dyn.DeepEqual(other["definition"].AsMap(), want) && other["stuff"].AsString() == row["stuff"].AsString())
                {
                    return other;
                }
            }

            return null;
        }

        var boughtRow = ByDef(after, buy);
        if (Num(boughtRow?["traderCount"]) != Num(buy["traderCount"]) - 1)
        {
            throw new AcceptanceException($"after: expected the trader's {buyId} down by one from {buy["traderCount"].ToJson()}, got {boughtRow.ToJson()}");
        }

        var soldRow = ByDef(after, sell);
        if (Num(soldRow?["colonyCount"]) != Num(sell["colonyCount"]) - sellCount)
        {
            throw new AcceptanceException($"after: expected the colony's {sellId} down by {sellCount} from {sell["colonyCount"].ToJson()}, got {soldRow.ToJson()}");
        }

        // Step 2: a buy the colony cannot afford is refused and the session stays open until a cancel ends it.
        JsonObject? dear = null;
        foreach (var row in Rows(after["lines"]))
        {
            if (Plain(row) && Num(row["maximumCount"]) >= 1
                && (dear is null || Num(row["maximumCount"]) * Num(row["buyPrice"]) > Num(dear["maximumCount"]) * Num(dear["buyPrice"])))
            {
                dear = row;
            }
        }

        if (dear is null)
        {
            throw new AcceptanceException($"after: no row left to buy: {after.ToJson()}");
        }

        var dearId = dear["lineId"].AsString();
        await SetLines("dear-lines", [new JsonObject { ["lineId"] = dearId, ["absoluteCount"] = (int)Num(dear["maximumCount"]) }]).ConfigureAwait(false);
        var dearSheet = await Sheet("dear", sessionId).ConfigureAwait(false);
        if (CaseJson.Flag(dearSheet["colonyCanAfford"]))
        {
            throw new AcceptanceException($"dear: the colony can afford the whole of {dearId}; no unaffordable deal to stage: {RowById(dearSheet, dearId).ToJson()}");
        }

        var refused = await Apply("dear-accept", new JsonObject { ["accept"] = new JsonObject { ["expectedDealSignature"] = dearSheet["dealSignature"].AsString() } }).ConfigureAwait(false);
        var refusal = refused["refused"].AsMap();

        // Validation catches the shortfall first (INVALID_REQUEST); a deal that slips past it and the game declines is NATIVE_FAILURE.
        var code = refusal?["code"].AsString() ?? "";
        if (refusal is null || (code != "FAILURE_CODE_INVALID_REQUEST" && code != "FAILURE_CODE_NATIVE_FAILURE"))
        {
            throw new AcceptanceException($"dear-accept: expected a refusal, got {refused.ToJson()}");
        }

        report["dearRefusal"] = refusal;
        var ended = await Apply("cancel", new JsonObject { ["end"] = new JsonObject { ["kind"] = "END_TRADE_KIND_CANCEL" } }).ConfigureAwait(false);
        _ = TradeKit.AppliedTrade("cancel", ended);
    }
}
