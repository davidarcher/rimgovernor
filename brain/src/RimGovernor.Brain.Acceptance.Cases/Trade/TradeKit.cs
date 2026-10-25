using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>What the trade cases share: the trade fixture's typed ops, one applied action and the trade reads.</summary>
internal static class TradeKit
{
    internal const string Tool = "test/trade_fixture";

    /// <summary><c>teleport_adjacent</c>: the first colonist able to negotiate moved beside the trader.</summary>
    internal static readonly FixtureOp<string, Fx.TradeNegotiators> Teleport = new(
        Tool,
        trader => ServedOps.Args(("action", "teleport_adjacent"), ("traderId", trader)),
        ServedOps.Succeeded<Fx.TradeNegotiators>(Tool, r => r.Success));

    /// <summary><c>session_setup</c> on the channel (settlement or orbital).</summary>
    internal static readonly FixtureOp<string, Fx.TradeSessionPrepared> Session = new(
        Tool,
        channel => ServedOps.Args(("action", "session_setup"), ("channel", channel)),
        ServedOps.Succeeded<Fx.TradeSessionPrepared>(Tool, r => r.Success));

    /// <summary><c>session_inventory</c>: the buyer's and the seller's stock.</summary>
    internal static readonly FixtureOp<(string Channel, Fx.TradeSessionPrepared Prepared), Fx.TradeInventory> Inventory = new(
        Tool,
        r => ServedOps.Args(("action", "session_inventory"), ("channel", r.Channel), ("traderId", r.Prepared.Trader), ("caravanId", r.Prepared.Caravan)),
        ServedOps.Reply<Fx.TradeInventory>(Tool));

    /// <summary>A <c>request_*</c> control on the channel; the faction and negotiator are the setup's once it ran.</summary>
    internal static readonly FixtureOp<(string Action, string Channel, Fx.TradeRequestPrepared? Setup), Fx.TradeRequestPrepared> Request = new(
        Tool,
        r => ServedOps.Args(("action", r.Action), ("channel", r.Channel), ("pawnId", r.Setup?.Negotiator), ("traderId", r.Setup?.Faction)),
        ServedOps.Succeeded<Fx.TradeRequestPrepared>(Tool, r => r.Success));

    /// <summary>Applies one action under <paramref name="key"/> and returns its result.</summary>
    internal static async Task<RimGovernor.Brain.GameFacts.Operations.ActionResult> ApplyAsync(TypedSession s, string key, RimGovernor.Brain.GameOrders.Operations.ActionIntent intent, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(key, NativeOps.Apply, new RimGovernor.Brain.GameOrders.Operations.ApplyRequest
        {
            Identity = s.Identity,
            Actions = [new RimGovernor.Brain.GameOrders.Operations.Action { Key = key, Intent = intent }], DeferSnapshot = false }, cancellationToken).ConfigureAwait(false);
        return reply.Results.ToList() is [var result]
            ? result
            : throw new AcceptanceException($"{key}: expected one result, got {reply.BatchFailure} {string.Join("; ", reply.Results)}");
    }

    /// <summary>The applied result's effect; any other result fails the label.</summary>
    internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect Applied(string label, RimGovernor.Brain.GameFacts.Operations.ActionResult result) =>
        result.Outcome?.Applied?.Outcome?.Applied?.Observed?.Effect
            ?? throw new AcceptanceException($"{label}: expected an applied outcome, got {result}");

    /// <summary>The applied result's trade effect; any other result fails the label.</summary>
    internal static RimGovernor.Brain.GameFacts.Receipts.TradeEffect AppliedTrade(string label, RimGovernor.Brain.GameFacts.Operations.ActionResult result) =>
        Applied(label, result).Trade ?? throw new AcceptanceException($"{label}: expected a trade effect, got {result}");

    /// <summary>The refusal code of a refused result, null for any other result.</summary>
    internal static RimGovernor.Brain.GameFacts.Common.FailureCode? RefusedCode(RimGovernor.Brain.GameFacts.Operations.ActionResult result) => result.Outcome?.Refused?.Code;

    /// <summary>The live trade session.</summary>
    internal static async Task<RimGovernor.Brain.GameFacts.Observations.TradeSession> SessionAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        return await s.CallAsync(label, NativeOps.ReadTradeSession, new RimGovernor.Brain.GameOrders.Observations.TradeSessionRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The open session's sheet.</summary>
    internal static async Task<RimGovernor.Brain.GameFacts.Observations.TradeSheet> SheetAsync(TypedSession s, string label, string sessionId, CancellationToken cancellationToken)
    {
        return await s.CallAsync(label, NativeOps.ReadTradeSheet, new RimGovernor.Brain.GameOrders.Observations.TradeSheetRequest { Scope = s.Scope, SessionId = sessionId }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The trade-acquisition read, with an optional caravan pack to estimate.</summary>
    internal static async Task<RimGovernor.Brain.GameFacts.Observations.TradeAcquisition> AcquisitionAsync(TypedSession s, string label, RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent? pack, CancellationToken cancellationToken)
    {
        return await s.CallAsync(label, NativeOps.ReadTradeAcquisition, new RimGovernor.Brain.GameOrders.Observations.TradeAcquisitionRequest { Scope = s.Scope, Pack = pack }, cancellationToken).ConfigureAwait(false);
    }
}
