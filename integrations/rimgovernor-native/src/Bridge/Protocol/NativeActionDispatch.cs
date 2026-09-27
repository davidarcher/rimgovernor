#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // One intent kind of Actions/Apply (#856): validated against live state,
    // then applied under native authority on the game thread.
    internal interface IActionHandler
    {
        // Null when the action applies to live state now, else the refusal.
        Common.Failure? Validate(Operations.Action action, Common.ObservationContext context);
        Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context);
        // What the action would do, without doing it.
        Receipts.EffectEvidence Preview(Operations.Action action, Common.ObservationContext context);
    }

    // Actions/Apply: every Action arm maps to exactly one handler. Actions in
    // a batch apply in order and independently; a key's first result is
    // replayed on resend while it stays in the in-memory window (never saved).
    internal static class NativeActionDispatch
    {
        private static readonly Dictionary<Operations.Action.IntentOneofCase, IActionHandler> Handlers = new Dictionary<Operations.Action.IntentOneofCase, IActionHandler>
        {
            [Operations.Action.IntentOneofCase.Trade] = new TradeActionHandler(),
            [Operations.Action.IntentOneofCase.Move] = new MoveActionHandler(),
            [Operations.Action.IntentOneofCase.Haul] = new HaulActionHandler(),
            [Operations.Action.IntentOneofCase.Melee] = new MeleeActionHandler(),
        };

        private const int ReplayCapacity = 256;
        private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Operations.ActionResult>>> replay = new Dictionary<string, LinkedListNode<KeyValuePair<string, Operations.ActionResult>>>();
        private static readonly LinkedList<KeyValuePair<string, Operations.ActionResult>> order = new LinkedList<KeyValuePair<string, Operations.ActionResult>>();

        // Fails the mod load when an Action arm has no handler.
        internal static void AssertComplete()
        {
            var missing = Enum.GetValues(typeof(Operations.Action.IntentOneofCase)).Cast<Operations.Action.IntentOneofCase>()
                .Where(c => c != Operations.Action.IntentOneofCase.None && !Handlers.ContainsKey(c)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException("Actions/Apply has no handler for: " + string.Join(", ", missing));
        }

        internal static Operations.ApplyReply Apply(Operations.ApplyRequest request)
        {
            if (!ProtoBoundary.ValidateIdentity(request.Identity, out var context, out var failure))
                return new Operations.ApplyReply { BatchFailure = failure };
            var reply = new Operations.ApplyReply();
            foreach (var action in request.Actions)
                reply.Results.Add(ApplyOne(action, context));
            return reply;
        }

        private static Operations.ActionResult ApplyOne(Operations.Action action, Common.ObservationContext context)
        {
            if (!action.HasKey || !ProtoBoundary.IsIdentifier(action.Key))
                return Refused(action.Key, Common.FailureCode.InvalidRequest, "An action requires an identifier key.");
            var slot = context.Identity.ColonyId + "/" + context.Identity.LoadToken + "/" + action.Key;
            if (replay.TryGetValue(slot, out var seen))
            {
                order.Remove(seen); order.AddFirst(seen);
                return seen.Value.Value.Clone();
            }
            var result = Decide(action, context.Clone());
            var node = order.AddFirst(new KeyValuePair<string, Operations.ActionResult>(slot, result.Clone()));
            replay[slot] = node;
            if (order.Count > ReplayCapacity) { replay.Remove(order.Last.Value.Key); order.RemoveLast(); }
            return result;
        }

        private static Operations.ActionResult Decide(Operations.Action action, Common.ObservationContext context)
        {
            if (!Handlers.TryGetValue(action.IntentCase, out var handler))
                return Refused(action.Key, Common.FailureCode.Unsupported, "No handler for this action kind.");
            try
            {
                var refusal = handler.Validate(action, context);
                if (refusal != null) return Refused(action.Key, refusal.Code, refusal.Detail);
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refused(action.Key, Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                Receipts.EffectEvidence evidence;
                using (authority.Owned()) evidence = handler.Apply(action, context);
                return new Operations.ActionResult { Key = action.Key, Applied = new Receipts.Receipt
                    { AdmittedContext = context, Applied = new Receipts.Applied { Observed = evidence } } };
            }
            catch (Exception error)
            {
                return new Operations.ActionResult { Key = action.Key, Failed = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Action failed: " + error.GetType().Name) };
            }
        }

        private static Operations.ActionResult Refused(string key, Common.FailureCode code, string reason) => new Operations.ActionResult
        { Key = key ?? "", Refused = new Operations.Refusal { Code = code, Reason = reason ?? "" } };
    }

    internal sealed class TradeActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) =>
            NativeTradeOperations.Validate(NativeTradeOperations.Normalized(action.Trade), context.Identity);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) =>
            NativeTradeOperations.Apply(NativeTradeOperations.Normalized(action.Trade), context);
        public Receipts.EffectEvidence Preview(Operations.Action action, Common.ObservationContext context) =>
            NativeTradeOperations.Preview(NativeTradeOperations.Normalized(action.Trade), context.Identity);
    }

    public sealed class NativeActionTools
    {
        public NativeActionTools() { NativeActionDispatch.AssertComplete(); }

        [Tool("rimgovernor/operations_apply", Title = "Apply intent actions", Description = "Apply a batch of idempotent intents in order on the game thread. Each is validated against live state and applied or refused independently; a resent key returns its first result.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ApplyReply.", Always = true)]
        public async Task<object> Apply(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official operations ApplyRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, "rimgovernor/operations_apply", request, Operations.ApplyRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.ApplyReply { BatchFailure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => { try { return ProtoBoundary.Encode(NativeActionDispatch.Apply(parsed)); } finally { SnapshotStream.NoteWrite(); } }, cancellationToken).ConfigureAwait(false);
        }
    }
}
