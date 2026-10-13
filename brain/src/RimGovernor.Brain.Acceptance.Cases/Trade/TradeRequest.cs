using System.Text.Json.Nodes;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>trade/request-caravan</c> and <c>trade/request-orbital</c> (Go <c>request.go</c>): a pinned real UseCommsConsole
/// request. An interruption has no charge; an unavailable console, the alliance cost and the cooldown refuse. The native
/// goodwill debit, cooldown tick and incident queue precede actual trader arrival.
/// </summary>
internal static class TradeRequest
{
    internal static IEnumerable<AcceptanceCase> Cases()
    {
        foreach (var channel in new[] { "caravan", "orbital" })
        {
            var orbital = channel == "orbital";
            yield return new AcceptanceCase
            {
                Name = "trade/request-" + channel,
                Scope = "Pinned real UseCommsConsole request: interruption has no charge; unavailable console, alliance cost and cooldown refuse. Native goodwill debit, cooldown tick and incident queue precede actual trader arrival. Go snapshots cannot prove the comms contact toil, goodwill mutation, vanilla queue or arrival physics. Setup provides only eligible participants and power.",
                Start = new FixtureStart { On = new LabStart(), Op = "test/trade_fixture", Args = new JsonObject { ["action"] = "request_setup", ["channel"] = channel } },
                RequiredOps = ["test/trade_fixture"],
                Quiet = QuietMode.Required,
                Budget = TimeSpan.FromMinutes(4),
                Crew = new Crew { Size = 3 },
                NoCheckpoint = true,
                Expansions = orbital ? ["ludeon.rimworld.odyssey"] : [],
                NoKeep = orbital,
                Run = (s, cancellationToken) => RunAsync(s, channel, cancellationToken),
            };
        }
    }

    private static async Task RunAsync(ISession s, string channel, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var p = s.Prepared ?? [];
        var isOrbital = channel == "orbital";
        if (!Flag(p["success"]))
        {
            throw new AcceptanceException($"request setup: {p.ToJson()}");
        }

        _ = await Authority.GrantAutoAsync(h.WireFunc(), "request-authority", identity, cancellationToken).ConfigureAwait(false);

        Task<JsonObject> State(string action) => h.CallAsync(
            action,
            "test/trade_fixture",
            new JsonObject { ["action"] = action, ["channel"] = channel, ["pawnId"] = Copy(p["pawnId"]), ["traderId"] = Copy(p["factionId"]) },
            cancellationToken);

        Task<JsonObject> Read() => TradeKit.ObserveAsync(h, "request-facts", "observations_read_trade_acquisition", identity, null, cancellationToken);

        var kind = isOrbital ? "TRADE_REQUEST_KIND_ORBITAL" : "TRADE_REQUEST_KIND_CARAVAN";

        async Task<JsonObject> Apply(string label, JsonNode? tick)
        {
            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject
                    {
                        ["key"] = label,
                        ["commsTradeRequest"] = new JsonObject
                        {
                            ["kind"] = kind,
                            ["factionId"] = Copy(p["factionId"]),
                            ["traderKind"] = Copy(p["traderKind"]),
                            ["consoleId"] = Copy(p["consoleId"]),
                            ["negotiatorId"] = Copy(p["pawnId"]),
                            ["expectedLastRequestTick"] = Copy(tick),
                        },
                    }),
                },
                cancellationToken).ConfigureAwait(false);
            var rows = reply["results"].AsSlice();
            if (rows.Count != 1)
            {
                throw new AcceptanceException($"{label}: {reply.ToJson()}");
            }

            return rows[0].AsMap() ?? [];
        }

        async Task Unchanged(string label, JsonObject before)
        {
            var after = await State("request_state").ConfigureAwait(false);
            if (Num(after["goodwill"]) != Num(before["goodwill"]) || Num(after["lastRequestTick"]) != Num(before["lastRequestTick"]))
            {
                throw new AcceptanceException($"{label} changed payment: {before.ToJson()} -> {after.ToJson()}");
            }
        }

        async Task Refusal(string label, JsonNode? tick)
        {
            var r = await Apply(label, tick).ConfigureAwait(false);
            var f = r["refused"].AsMap();
            if (f?["code"].AsString() != "FAILURE_CODE_INVALID_REQUEST")
            {
                throw new AcceptanceException($"{label} expected refusal: {r.ToJson()}");
            }

            s.Report[label] = f;
        }

        async Task Queued(string label)
        {
            var r = await Apply(label, p["lastRequestTick"]).ConfigureAwait(false);
            var effect = r["applied"].AsMap()?["applied"].AsMap()?["observed"].AsMap()?["commsTradeRequest"].AsMap();
            if (!CaseJson.Flag(effect?["workQueued"]))
            {
                throw new AcceptanceException($"not queued: {r.ToJson()}");
            }

            await Unchanged(label, p).ConfigureAwait(false);
            var facts = await Read().ConfigureAwait(false);
            if (facts["commsWork"].AsSlice().Count == 0 || facts["arrivals"].AsSlice().Count != 0)
            {
                throw new AcceptanceException($"queued work misclassified: {facts.ToJson()}");
            }
        }

        await Queued("interrupted-request").ConfigureAwait(false);
        _ = await State("request_interrupt").ConfigureAwait(false);
        await Unchanged("interrupted", p).ConfigureAwait(false);
        _ = await State("request_console_off").ConfigureAwait(false);
        await Refusal("console-unavailable", p["lastRequestTick"]).ConfigureAwait(false);
        _ = await State("request_console_on").ConfigureAwait(false);
        var low = await State("request_low_goodwill").ConfigureAwait(false);
        if (!CaseJson.Flag(low["ally"]))
        {
            throw new AcceptanceException($"low goodwill control must retain Ally: {low.ToJson()}");
        }

        await Refusal("alliance-cost", p["lastRequestTick"]).ConfigureAwait(false);
        await Unchanged("alliance-cost", low).ConfigureAwait(false);
        _ = await State("request_restore_goodwill").ConfigureAwait(false);
        await Queued("paid-request").ConfigureAwait(false);

        JsonObject? applied = null;
        JsonObject? arrival = null;
        _ = await RunAt.RunUntilAsync(
            h,
            "request-contact",
            2500,
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                var facts = await Read().ConfigureAwait(false);
                foreach (var a in Rows(facts["arrivals"]))
                {
                    if (a["factionId"].AsString() == p["factionId"].AsString() && a["traderKind"].AsString() == p["traderKind"].AsString())
                    {
                        arrival = a;
                    }
                }

                // Fixture state reads need a paused game; authoritative request rows supply the application facts during a wait.
                foreach (var r in Rows(facts["requests"]))
                {
                    if (r["factionId"].AsString() == p["factionId"].AsString() && r["traderKind"].AsString() == p["traderKind"].AsString())
                    {
                        applied = r;
                    }
                }

                ct.ThrowIfCancellationRequested();
                return (applied.ToJson(), arrival is not null && Num(applied?["lastRequestTick"]) > Num(p["lastRequestTick"]));
            },
            quiet: true,
            s.Report,
            cancellationToken).ConfigureAwait(false);
        if (Num(applied?["goodwill"]) != Num(p["goodwill"]) - Num(p["cost"]) || Num(applied?["cooldownRemainingTicks"]) <= 0)
        {
            throw new AcceptanceException($"native payment/cooldown mismatch: {applied.ToJson()} before {p.ToJson()}");
        }

        var delay = Num(arrival?["arrivalTick"]) - Num(applied?["lastRequestTick"]);
        var (min, max) = isOrbital ? (2500, 5000) : (120000, 120000);
        if (delay < min || delay > max)
        {
            throw new AcceptanceException($"native arrival outside bounds: {arrival.ToJson()}");
        }

        await Refusal("cooldown", applied?["lastRequestTick"]).ConfigureAwait(false);
        s.Report["applied"] = applied;
        s.Report["queuedArrival"] = arrival;

        // RunUntil pauses at the observed boundary. Tick budget follows the native queue; no incident is injected.
        _ = await RunAt.RunUntilAsync(
            h,
            "native-arrival",
            (ulong)(max + 3000),
            new Wait { Stall = Wait.StallBudget() },
            async ct =>
            {
                var facts = await Read().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (isOrbital)
                {
                    foreach (var ship in Rows(facts["passingShips"]))
                    {
                        if (ship["traderKind"].AsString() == p["traderKind"].AsString())
                        {
                            s.Report["arrived"] = ship;
                            return (ship.ToJson(), true);
                        }
                    }
                }

                return (facts["arrivals"].ToJson(), channel == "caravan" && facts["arrivals"].AsSlice().Count == 0);
            },
            quiet: true,
            s.Report,
            cancellationToken).ConfigureAwait(false);
        if (channel == "caravan")
        {
            var final = await State("request_state").ConfigureAwait(false);
            if (Num(final["traders"]) < 1)
            {
                throw new AcceptanceException($"queue removal without native caravan: {final.ToJson()}");
            }

            s.Report["arrived"] = final;
        }
    }
}
