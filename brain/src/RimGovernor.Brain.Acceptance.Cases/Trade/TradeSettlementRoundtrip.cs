using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Acceptance.Cases.CaseJson;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Automatic formation refusal and explicit existing-caravan purchase/return mechanics.</summary>
internal static class TradeSettlementRoundtrip
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "trade/settlement-roundtrip",
        Scope = "Prepared home shortage must not admit unsupported automatic caravan formation. A separately prepared existing caravan buys native settlement stock and returns through exact ExistingRoute; observed home crew and purchased cargo prove delivery. No bot-selected expedition or packing claim.",
        Start = new FixtureStart
        {
            On = new FixtureStart { On = new LabStart { Colonists = 6 }, Op = "test/food_channels_prepare" },
            Op = "test/trade_fixture", Args = new JsonObject { ["action"] = "roundtrip_setup" }
        },
        RequiredOps = ["test/trade_fixture"], Quiet = QuietMode.Required,
        Serve = new ServeSpec { Families = ["acquisition", "supply", "trade", "work"],
            NativeTimeout = TimeSpan.FromSeconds(15), Prefix = "trade-roundtrip" },
        Budget = TimeSpan.FromMinutes(6), Crew = new Crew { Size = 6 }, NoCheckpoint = true,
        Run = RunAsync
    };

    private static async Task RunAsync(ISession s, CancellationToken ct)
    {
        var h = await UnsupportedLabor.ReviewAsync(s, "unsupported_forecast_caravan_formation",
            ActionKind.CaravanDeparture, ct).ConfigureAwait(false);
        var prepared = await h.CallAsync("existing-caravan", "test/trade_fixture",
            new JsonObject { ["action"] = "session_setup", ["channel"] = "settlement" }, ct).ConfigureAwait(false);
        await TradeWorldSession.RunAsync(s, "settlement", ct, prepared, h).ConfigureAwait(false);
        var pawn = prepared["pawnId"].AsString();
        var receipt = await ApplyIntents.ApplyOneAsync(h, "existing-return", s.Identity, "existing-return",
            new JsonObject { ["formCaravan"] = new JsonObject
            {
                ["mode"] = Wire.Name(RimGovernor.Contracts.Wire.Operations.CaravanMode.ExistingRoute), ["pawnIds"] = new JsonArray(pawn),
                ["cargo"] = new JsonArray(new JsonObject { ["defName"] = "ComponentIndustrial", ["count"] = 1 }),
                ["destinationTile"] = Copy(prepared["homeTile"])
            } }, ct).ConfigureAwait(false);
        if (receipt["refused"] is not null) throw new AcceptanceException("Existing caravan return refused: " + receipt.ToJson());
        s.Report["return_receipt"] = receipt.DeepClone();
        JsonObject latest = [];
        async Task<(string Signature, bool Done)> Observe(CancellationToken token)
        {
            latest = await h.CallAsync("return-state", "test/trade_fixture",
                new JsonObject { ["action"] = "roundtrip_state", ["traderId"] = Copy(prepared["traderId"]) }, token).ConfigureAwait(false);
            var facts = await TradeKit.ObserveAsync(h, "returned-cargo", "observations_read_colony_facts",
                s.Identity, null, token).ConfigureAwait(false);
            var components = Rows(facts["resources"]).Where(r => r["defName"].AsString() == "ComponentIndustrial").Sum(r => Num(r["units"]));
            latest["homeComponents"] = components;
            return (latest.ToJson(), Texts(latest["homeCrew"]).Contains(pawn) && components >= 1);
        }
        _ = await RunAt.RunUntilAsync(h, "return-home", 60000,
            new Wait { Ceiling = TimeSpan.FromMinutes(2), Stall = Wait.StallBudget() },
            Observe, quiet: true, s.Report, ct).ConfigureAwait(false);
        var (_, complete) = await Observe(ct).ConfigureAwait(false);
        s.Report["native_return"] = latest.DeepClone();
        if (!complete) throw new AcceptanceException("Existing caravan did not return with purchased cargo: " + latest.ToJson());
    }
}
