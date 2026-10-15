using System.Text.Json.Nodes;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>trade/settlement-session</c> and <c>trade/orbital-session</c> (Go <c>world_session.go</c>): Core settlement and
/// orbital trade through the shared open/read/stage/accept/end contract, cancel without buying, native inventory and
/// silver transfer and a closed session.
/// </summary>
internal static class TradeWorldSession
{
    internal static IEnumerable<AcceptanceCase> Cases()
    {
        foreach (var channel in new[] { "settlement", "orbital" })
        {
            yield return new AcceptanceCase
            {
                Name = "trade/" + channel + "-session",
                Scope = "Core " + channel + " trade: shared open/read/stage/accept/end, cancel without buying, native inventory/silver transfer and closed session. A Go snapshot cannot prove vanilla settlement inventory ownership or orbital comms and drop-pod delivery. Opening and staging supply no goods; settlement purchases remain away from home.",
                Start = new FixtureStart { On = new LabStart(), Op = "test/trade_fixture", Args = new JsonObject { ["action"] = "session_setup", ["channel"] = channel } },
                RequiredOps = ["test/trade_fixture"],
                Quiet = QuietMode.Required,
                Budget = AcceptanceCase.LabBudget,
                Crew = new Crew { Size = 3 },

                // Two ordered sessions keep local baseline state and cannot resume mid-deal.
                NoCheckpoint = true,
                Run = (s, cancellationToken) => RunAsync(s, channel, cancellationToken),
            };
        }
    }

    internal static async Task RunAsync(ISession s, string channel, CancellationToken cancellationToken, JsonObject? preparation = null, Harness? harness = null)
    {
        var h = harness ?? s.Harness;
        var identity = s.Identity;
        var prepared = preparation ?? s.Prepared ?? [];
        if (!Flag(prepared["success"]))
        {
            throw new AcceptanceException($"session_setup: {prepared.ToJson()}");
        }

        var seller = prepared["traderId"].AsString();
        var caravan = prepared["caravanId"].AsString();
        var pawn = prepared["pawnId"].AsString();
        var target = channel == "settlement"
            ? new JsonObject { ["settlement"] = new JsonObject { ["settlementId"] = seller, ["caravanId"] = caravan } }
            : new JsonObject { ["orbitalShip"] = new JsonObject { ["shipId"] = seller } };
        s.Report["prepared"] = prepared;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "acquire", identity, cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> Apply(string label, JsonObject step)
        {
            var intent = new JsonObject { ["target"] = target.DeepClone(), ["negotiatorId"] = pawn };
            foreach (var (key, value) in step)
            {
                intent[key] = value?.DeepClone();
            }

            var reply = await h.WireAsync(
                label,
                "operations_apply",
                new JsonObject
                {
                    ["identity"] = identity.DeepClone(),
                    ["actions"] = new JsonArray(new JsonObject { ["key"] = "world-session-" + label, ["trade"] = intent }),
                },
                cancellationToken).ConfigureAwait(false);
            return OneResult(label, reply);
        }

        Task<JsonObject> Read(string label, string operation, JsonObject? extra) =>
            TradeKit.ObserveAsync(h, label, operation, identity, extra, cancellationToken);

        Task<JsonObject> Inventory(string label) => h.CallAsync(
            label,
            "test/trade_fixture",
            new JsonObject { ["action"] = "session_inventory", ["channel"] = channel, ["traderId"] = seller, ["caravanId"] = caravan },
            cancellationToken);

        async Task Unchanged(string label, JsonObject before)
        {
            var after = await Inventory(label).ConfigureAwait(false);
            foreach (var field in new[] { "silver", "medicine", "components", "sellerComponents", "homeComponents" })
            {
                if (Num(before[field]) != Num(after[field]))
                {
                    throw new AcceptanceException($"{label} moved {field} before acceptance: before={before.ToJson()} after={after.ToJson()}");
                }
            }
        }

        async Task Closed(string label)
        {
            var state = await Read(label, "observations_read_trade_session", null).ConfigureAwait(false);
            if (Flag(state["open"]) || state["sessionId"].AsString().Length > 0)
            {
                throw new AcceptanceException($"{label}: session still open: {state.ToJson()}");
            }
        }

        async Task<string> Open(string label)
        {
            var result = await Apply(label, new JsonObject { ["open"] = new JsonObject { ["giftMode"] = false } }).ConfigureAwait(false);
            _ = TradeKit.AppliedTrade(label, result);
            var sessionId = "";
            async Task<(string Signature, bool Done)> Observe(CancellationToken ct)
            {
                var state = await Read(label + "-session", "observations_read_trade_session", null).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                sessionId = state["sessionId"].AsString();
                return (sessionId, Flag(state["open"]) && sessionId.Length > 0);
            }

            // Orbital opening orders a real UseCommsConsole job; its receipt is not an open session.
            var (_, ready) = await Observe(cancellationToken).ConfigureAwait(false);
            if (ready)
            {
                return sessionId;
            }

            _ = await RunAt.RunUntilAsync(h, label + "-contact", 2500, new Wait { Stall = Wait.StallBudget() }, Observe, quiet: true, s.Report, cancellationToken).ConfigureAwait(false);
            return sessionId;
        }

        Task<JsonObject> Sheet(string label, string session) =>
            Read(label, "observations_read_trade_sheet", new JsonObject { ["sessionId"] = session });

        async Task Stage(string label, JsonArray lines)
        {
            var result = await Apply(label, new JsonObject { ["setLines"] = new JsonObject { ["lines"] = lines } }).ConfigureAwait(false);
            _ = TradeKit.AppliedTrade(label, result);
        }

        static JsonObject Line(string id, int count) => new() { ["lineId"] = id, ["absoluteCount"] = count };

        static JsonObject? Find(JsonObject sheet, string name) =>
            Rows(sheet["lines"]).FirstOrDefault(row => row["definition"].AsMap()?["defName"].AsString() == name);

        async Task Cancel(string label)
        {
            var result = await Apply(label, new JsonObject { ["end"] = new JsonObject { ["kind"] = "END_TRADE_KIND_CANCEL" } }).ConfigureAwait(false);
            _ = TradeKit.AppliedTrade(label, result);
            await Closed(label + "-closed").ConfigureAwait(false);
        }

        var before = await Inventory("before").ConfigureAwait(false);
        if (Num(before["silver"]) != 2000 || Num(before["medicine"]) != 6 || Num(before["components"]) != 0 || Num(before["sellerComponents"]) != 20)
        {
            throw new AcceptanceException($"invalid pinned inventory: {before.ToJson()}");
        }

        var sessionId = await Open("browse-open").ConfigureAwait(false);
        var offers = await Sheet("browse-sheet", sessionId).ConfigureAwait(false);
        var component = Find(offers, "ComponentIndustrial");
        var medicine = Find(offers, "MedicineIndustrial");
        if (component is null || Num(component["buyPrice"]) <= 0 || Num(component["maximumCount"]) < 1 || medicine is null || Num(medicine["minimumCount"]) > -1)
        {
            throw new AcceptanceException($"missing live priced stock: {offers.ToJson()}");
        }

        await Unchanged("opened-no-delivery", before).ConfigureAwait(false);
        await Stage("browse-stage", [Line(component["lineId"].AsString(), 1)]).ConfigureAwait(false);
        await Unchanged("staged-no-delivery", before).ConfigureAwait(false);
        await Cancel("browse-cancel").ConfigureAwait(false);
        await Unchanged("cancel-no-purchase", before).ConfigureAwait(false);

        sessionId = await Open("purchase-open").ConfigureAwait(false);
        offers = await Sheet("purchase-sheet", sessionId).ConfigureAwait(false);
        component = Find(offers, "ComponentIndustrial");
        var componentId = component?["lineId"].AsString() ?? "";

        async Task<JsonObject> Accept(string label)
        {
            var staged = await Sheet(label + "-sheet", sessionId).ConfigureAwait(false);
            return await Apply(label, new JsonObject { ["accept"] = new JsonObject { ["expectedDealSignature"] = staged["dealSignature"].AsString() } }).ConfigureAwait(false);
        }

        await Stage("purchase-stage", [Line(componentId, 1)]).ConfigureAwait(false);
        var accepted = await Accept("purchase-accept").ConfigureAwait(false);
        var effect = TradeKit.AppliedTrade("purchase-accept", accepted);
        s.Report["purchase"] = effect;
        await Closed("purchase-closed").ConfigureAwait(false);

        // Orbital goods are incoming drop pods until vanilla releases their contents.
        JsonObject after = [];
        async Task<(string Signature, bool Done)> ObserveDelivery(CancellationToken ct)
        {
            after = await Inventory("delivery-inventory").ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return (after.ToJson(), Num(after["components"]) == 1);
        }

        var (_, delivered) = await ObserveDelivery(cancellationToken).ConfigureAwait(false);
        if (!delivered)
        {
            _ = await RunAt.RunUntilAsync(h, "native-delivery", 2500, new Wait { Stall = Wait.StallBudget() }, ObserveDelivery, quiet: true, s.Report, cancellationToken).ConfigureAwait(false);
        }

        s.Report["before"] = before;
        s.Report["after"] = after;
        if (Num(after["sellerComponents"]) != 19 || Num(after["medicine"]) != 6 || Num(after["silver"]) >= 2000 || Num(after["silver"]) < 1000
            || Num(after["silver"]) != Num(effect["afterSilver"]))
        {
            throw new AcceptanceException($"native purchase inventory/silver mismatch: {after.ToJson()} effect={effect.ToJson()}");
        }

        if (channel == "settlement" && Num(after["homeComponents"]) != 0)
        {
            throw new AcceptanceException($"away purchase counted as home delivery: {after.ToJson()}");
        }
    }
}
