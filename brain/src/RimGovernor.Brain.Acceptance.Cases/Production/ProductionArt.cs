using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Prepared art proves native packed identity, pricing and paid transfer, not autonomous manufacture.</summary>
internal static class ProductionArt
{
    private const string Op = "test/art";
    internal static AcceptanceCase Case() => new()
    {
        Name = "production/art",
        Scope = "Prepared finished art retains native room/quality observations and exact packed-item paid sale. Normal negotiator labor is explicitly unsupported; no automatic sculpture production or installation claim.",
        Start = new FixtureStart { Op = Op, Args = new JsonObject { ["action"] = "prepare" }, On = new LabStart { Colonists = 2 } },
        RequiredOps = [Op, "test/trade_fixture"], QuietWorld = true,
        Serve = new ServeSpec { Families = ["art", "trade"], NativeTimeout = TimeSpan.FromSeconds(15), Prefix = "art" },
        Budget = TimeSpan.FromMinutes(5), Crew = new Crew { Size = 3 },
        Reason = "Native packed-item identity, native trade pricing and physical inventory transfer cannot be proved by a policy test.",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken ct)
    {
        var prepared = s.Prepared ?? [];
        if (!Flag(prepared["success"])) throw new AcceptanceException("Invalid art fixture: " + prepared.ToJson());
        s.Report["fixture"] = prepared.DeepClone();
        var arrival = await s.Harness.CallAsync("art-trader", Op, new JsonObject { ["action"] = "trader" }, ct).ConfigureAwait(false);
        var trader = arrival["traderId"].AsString();
        if (!Flag(arrival["success"]) || trader.Length == 0 || arrival["buyable"].AsSlice().Count == 0)
            throw new AcceptanceException("No native buyer for prepared art: " + arrival.ToJson());
        var h = await UnsupportedLabor.ReviewAsync(s, "unsupported_shared_trade_labor", ActionKind.Trade, ct).ConfigureAwait(false);
        var before = await h.CallAsync("art-before", Op, new JsonObject { ["action"] = "audit" }, ct).ConfigureAwait(false);
        var packed = Rows(before["packed"]).Select(r => r["id"].AsString()).ToHashSet(StringComparer.Ordinal);
        if (packed.Count == 0) throw new AcceptanceException("Prepared packed art disappeared before explicit trade.");
        var pawn = prepared["artist"].AsString();
        var adjacent = await h.CallAsync("art-adjacent", "test/trade_fixture",
            new JsonObject { ["action"] = "teleport_adjacent", ["traderId"] = trader, ["pawnId"] = pawn }, ct).ConfigureAwait(false);
        if (!Flag(adjacent["success"])) throw new AcceptanceException("Unable to prepare adjacent negotiator.");
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "art-explicit-authority", s.Identity, ct).ConfigureAwait(false);
        async Task<JsonObject> Apply(string label, string operation, JsonObject fields)
        {
            var receipt = await ApplyIntents.ApplyOneAsync(h, label, s.Identity, label,
                new JsonObject { ["trade"] = new JsonObject { ["traderId"] = trader, ["negotiatorId"] = pawn, [operation] = fields } }, ct).ConfigureAwait(false);
            return TradeKit.AppliedTrade(label, receipt);
        }
        var opened = await Apply("art-open", "open", new JsonObject { ["giftMode"] = false }).ConfigureAwait(false);
        Task<JsonObject> Sheet(string label) => TradeKit.ObserveAsync(h, label, "observations_read_trade_sheet", s.Identity,
            new JsonObject { ["sessionId"] = opened["sessionId"].AsString() }, ct);
        var sheet = await Sheet("art-priced").ConfigureAwait(false);
        var art = Rows(sheet["lines"]).FirstOrDefault(r => packed.Contains(r["thingId"].AsString())
            && Flag(r["traderWillTrade"]) && Num(r["minimumCount"]) <= -1 && Num(r["sellPrice"]) > 0)
            ?? throw new AcceptanceException("No priced row matches exact prepared packed art: " + sheet.ToJson());
        var soldId = art["thingId"].AsString();
        s.Report["priced_art"] = art.DeepClone();
        _ = await Apply("art-stage", "setLines", new JsonObject { ["lines"] = new JsonArray(
            new JsonObject { ["lineId"] = art["lineId"].AsString(), ["absoluteCount"] = -1 }) }).ConfigureAwait(false);
        var staged = await Sheet("art-staged").ConfigureAwait(false);
        var accepted = await Apply("art-accept", "accept", new JsonObject { ["expectedDealSignature"] = staged["dealSignature"].AsString() }).ConfigureAwait(false);
        if (!Flag(accepted["executed"]) || !Flag(accepted["actuallyTraded"]) || !Flag(accepted["closed"])
            || Num(accepted["afterSilver"]) <= Num(accepted["beforeSilver"]))
            throw new AcceptanceException("Art sale did not execute for payment: " + accepted.ToJson());
        var after = await h.CallAsync("art-after", Op, new JsonObject { ["action"] = "audit" }, ct).ConfigureAwait(false);
        if (Rows(after["packed"]).Any(r => r["id"].AsString() == soldId)
            || !Rows(after["traderArt"]).Any(r => r["id"].AsString() == soldId)
            || Num(after["colonySilver"]) <= Num(before["colonySilver"]))
            throw new AcceptanceException("Native art/silver inventory disagrees with paid exact-item sale: " + after.ToJson());
        var session = await TradeKit.ObserveAsync(h, "art-session-closed", "observations_read_trade_session", s.Identity, null, ct).ConfigureAwait(false);
        if (Flag(session["open"])) throw new AcceptanceException("Art trade session remained open.");
        s.Report["before"] = before; s.Report["after"] = after; s.Report["sale"] = accepted;
    }
}
