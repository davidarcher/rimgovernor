using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>trade/open</c> (Go <c>open.go</c>): the OpenTrade intent end to end against a live game: a real spawned
/// trader-caravan incident, a real eligible colonist negotiator teleported adjacent to the trader (the immediate open
/// path) and native TradeSession admission invoked through the same <c>operations_apply</c> wire contract the bridge's
/// actions writer drives. An unknown trader is refused, open applies, a fresh open attempt for the same pair reuses the
/// live session instead of conflicting, and a different pair is refused while it is held. It closes with the session
/// still open; SetTradeLines, AcceptTrade and EndTrade are driven by the routine, not claimed here. Uses a private
/// disposable fixture (<c>test/trade_fixture</c>) since a specific eligible trader-caravan incident and an adjacent
/// negotiator cannot be reliably produced by native random generation alone.
/// </summary>
internal static class TradeOpen
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "trade/open",
        Scope = "Native OpenTrade intent: a real TradeSession admission against an actual spawned trader "
            + "caravan, unknown-trader and other-pair refusals, open-or-reuse under a fresh key, a resent key replayed, and the session in the trade-session read.",
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
        foreach (var required in new[] { "rimgovernor/operations_apply", "rimgovernor/observations_read_trade_session" })
        {
            if (!Dyn.Contains(s.Names, required))
            {
                throw new AcceptanceException($"missing {required} in discovery");
            }
        }

        var incident = await h.CallAsync("spawn-trader", "test/trade_fixture", new JsonObject { ["action"] = "incident" }, cancellationToken).ConfigureAwait(false);
        if (incident["eligible"].AsBool() != true)
        {
            throw new AcceptanceException($"spawn-trader: incident was not eligible to fire: {incident.ToJson()}");
        }

        if (incident["applied"].AsBool() != true)
        {
            throw new AcceptanceException($"spawn-trader: incident did not apply: {incident.ToJson()}");
        }

        var traderId = CaseJson.Texts(incident["traderIds"]).FirstOrDefault() ?? "";
        if (traderId.Length == 0)
        {
            throw new AcceptanceException($"spawn-trader: no trader pawn was spawned: {incident.ToJson()}");
        }

        report["incident"] = incident;

        var snapshot = await h.CallAsync("snapshot-colonists", "test/trade_fixture", new JsonObject { ["action"] = "snapshot" }, cancellationToken).ConfigureAwait(false);
        var negotiatorId = "";
        var otherId = "";
        foreach (var row in CaseJson.Rows(snapshot["colonists"]))
        {
            if (row["social"].AsNumber() < 0)
            {
                continue;
            }

            if (negotiatorId.Length == 0)
            {
                negotiatorId = row["id"].AsString();
            }
            else if (otherId.Length == 0)
            {
                otherId = row["id"].AsString();
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

        // Apply sends one open intent under key and returns its result.
        async Task<JsonObject> Apply(string label, string key, string trader, string negotiator)
        {
            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject
                    {
                        ["key"] = key,
                        ["trade"] = new JsonObject
                        {
                            ["traderId"] = trader,
                            ["negotiatorId"] = negotiator,
                            ["open"] = new JsonObject { ["giftMode"] = false },
                        },
                    }),
                },
                cancellationToken).ConfigureAwait(false);
            return CaseJson.OneResult(label, reply);
        }

        // Refusal 1: a trader that is not on the map.
        var unknown = await Apply("unknown-trader", "trade-open-unknown", "Thing_NoSuchTrader", negotiatorId).ConfigureAwait(false);
        if (TradeKit.RefusedCode(unknown) != "FAILURE_CODE_NOT_FOUND")
        {
            throw new AcceptanceException($"unknown-trader: expected a FAILURE_CODE_NOT_FOUND refusal, got {unknown.ToJson()}");
        }

        // Apply: the real native TradeSession admission.
        var first = await Apply("open", "trade-open", traderId, negotiatorId).ConfigureAwait(false);
        var appliedTrade = TradeKit.AppliedTrade("open", first);
        if (appliedTrade["sessionId"].AsString().Length == 0 || appliedTrade["factionId"].AsString().Length == 0)
        {
            throw new AcceptanceException($"open: expected session and faction ids, got {appliedTrade.ToJson()}");
        }

        if (CaseJson.Flag(appliedTrade["executed"]))
        {
            throw new AcceptanceException($"open: expected executed=false for a bare open, got {appliedTrade.ToJson()}");
        }

        if (CaseJson.Flag(appliedTrade["closed"]))
        {
            throw new AcceptanceException($"open: expected closed=false for a bare open, got {appliedTrade.ToJson()}");
        }

        // The applied result is the whole outcome (trade is an intent-mode kind); the live session shows in the
        // trade-session read.
        var session = await TradeKit.ObserveAsync(h, "session", "observations_read_trade_session", identity, null, cancellationToken).ConfigureAwait(false);
        if (!CaseJson.Flag(session["open"]) || session["traderId"].AsString() != traderId || session["negotiatorId"].AsString() != negotiatorId)
        {
            throw new AcceptanceException($"session: expected {negotiatorId} trading with {traderId} in an open session, got {session.ToJson()}");
        }

        // Resend: the same key returns the identical result.
        var replay = await Apply("resend", "trade-open", traderId, negotiatorId).ConfigureAwait(false);
        if (!Dyn.DeepEqual(replay, first))
        {
            throw new AcceptanceException("resend: the same key returned a different result");
        }

        // Open-or-reuse: a fresh key for the same pair applies against the live session rather than conflicting.
        var again = await Apply("reuse", "trade-open-again", traderId, negotiatorId).ConfigureAwait(false);
        var reuseTrade = TradeKit.AppliedTrade("reuse", again);
        if (reuseTrade["sessionId"].AsString() != appliedTrade["sessionId"].AsString())
        {
            throw new AcceptanceException($"reuse: expected the live session \"{appliedTrade["sessionId"].AsString()}\", got {again.ToJson()}");
        }

        // Refusal 2: a different negotiator while the session is held.
        if (otherId.Length > 0)
        {
            var other = await Apply("other-pair", "trade-open-other", traderId, otherId).ConfigureAwait(false);
            if (TradeKit.RefusedCode(other) != "FAILURE_CODE_STALE_IDENTITY")
            {
                throw new AcceptanceException($"other-pair: expected a FAILURE_CODE_STALE_IDENTITY refusal, got {other.ToJson()}");
            }
        }
    }
}
