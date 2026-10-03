#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
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
    }

    // Actions/Apply: every Action arm maps to exactly one handler. Actions in
    // a batch apply in order and independently; a key's first result is
    // replayed on resend while it stays in the in-memory window (never saved).
    internal static class NativeActionDispatch
    {
        private static readonly Dictionary<Operations.Action.IntentOneofCase, IActionHandler> Handlers = new Dictionary<Operations.Action.IntentOneofCase, IActionHandler>
        {
            [Operations.Action.IntentOneofCase.Trade] = new TradeActionHandler(),
            [Operations.Action.IntentOneofCase.Building] = new BuildingActionHandler(),
            [Operations.Action.IntentOneofCase.Move] = new MoveActionHandler(),
            [Operations.Action.IntentOneofCase.Haul] = new HaulActionHandler(),
            [Operations.Action.IntentOneofCase.Draft] = new DraftActionHandler(),
            [Operations.Action.IntentOneofCase.CombatOrders] = new CombatOrdersActionHandler(),
            [Operations.Action.IntentOneofCase.ApparelPolicy] = new ApparelPolicyActionHandler(),
            [Operations.Action.IntentOneofCase.Research] = new ResearchActionHandler(),
            [Operations.Action.IntentOneofCase.Naming] = new NamingActionHandler(),
            [Operations.Action.IntentOneofCase.Dialog] = new DialogActionHandler(),
            [Operations.Action.IntentOneofCase.Prisoner] = new PrisonerInteractionActionHandler(),
            [Operations.Action.IntentOneofCase.AcceptQuest] = new AcceptQuestActionHandler(),
            [Operations.Action.IntentOneofCase.Ritual] = new RitualActionHandler(),
            [Operations.Action.IntentOneofCase.Ability] = new AbilityActionHandler(),
            [Operations.Action.IntentOneofCase.FormCaravan] = new FormCaravanActionHandler(),
            [Operations.Action.IntentOneofCase.Assign] = new AssignActionHandler(),
            [Operations.Action.IntentOneofCase.WorkSettings] = new WorkSettingsActionHandler(),
            [Operations.Action.IntentOneofCase.ProductionBill] = new ProductionBillActionHandler(),
            [Operations.Action.IntentOneofCase.Husbandry] = new HusbandryActionHandler(),
            [Operations.Action.IntentOneofCase.Zone] = new ZoneActionHandler(),
            [Operations.Action.IntentOneofCase.RemoveFloor] = new FloorRemovalActionHandler(),
            [Operations.Action.IntentOneofCase.Designate] = new DesignateActionHandler(),
            [Operations.Action.IntentOneofCase.Relocate] = new RelocateActionHandler(),
            [Operations.Action.IntentOneofCase.BuildingPatch] = new BuildingPatchActionHandler(),
            [Operations.Action.IntentOneofCase.GiveJob] = new GiveJobActionHandler(),
            [Operations.Action.IntentOneofCase.Area] = new AreaActionHandler(),
            [Operations.Action.IntentOneofCase.PolicyPrune] = new PolicyPruneActionHandler(),
            [Operations.Action.IntentOneofCase.RemoveRoof] = new RemoveRoofActionHandler(),
            [Operations.Action.IntentOneofCase.AreaPlantCut] = new AreaPlantCutActionHandler(),
            [Operations.Action.IntentOneofCase.ReadingPolicy] = new ReadingPolicyActionHandler(),
            [Operations.Action.IntentOneofCase.DrugPolicy] = new DrugPolicyActionHandler(),
            [Operations.Action.IntentOneofCase.FoodPolicy] = new FoodPolicyActionHandler(),
            [Operations.Action.IntentOneofCase.AutoHomeArea] = new AutoHomeAreaActionHandler(),
            [Operations.Action.IntentOneofCase.PawnSettings] = new PawnSettingsActionHandler(),
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
                using (authority.Owned())
                using (BridgeTools.OperationIntent.Scope(action.Purpose)) evidence = handler.Apply(action, context);
                return new Operations.ActionResult { Key = action.Key, Applied = new Receipts.Receipt
                    { AdmittedContext = context, Applied = new Receipts.Applied { Observed = evidence } } };
            }
            catch (ApplyRefusedException refused)
            {
                return Refused(action.Key, refused.Code, refused.Message);
            }
            catch (Exception error)
            {
                Verse.Log.Error("[RimGovernor] Action " + action.IntentCase + " failed: " + error);
                return new Operations.ActionResult { Key = action.Key, Failed = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Action failed: " + error.GetType().Name) };
            }
        }

        private static Operations.ActionResult Refused(string key, Common.FailureCode code, string reason) => new Operations.ActionResult
        { Key = key ?? "", Refused = new Operations.Refusal { Code = code, Reason = reason ?? "" } };
    }

    // Thrown from Apply when native state already changed but the intent's
    // effect did not happen (#1156): the action is refused, not failed.
    internal sealed class ApplyRefusedException : Exception
    {
        internal Common.FailureCode Code { get; }
        internal ApplyRefusedException(Common.FailureCode code, string message) : base(message) { Code = code; }
    }

    internal sealed class TradeActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) =>
            NativeTradeOperations.Validate(action.Trade, context.Identity);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) =>
            NativeTradeOperations.Apply(action.Trade, context);
    }

    // BuildingIntent: place one ordinary blueprint (or an instant building)
    // through NativeConstructionPlan, validated against the live map. A
    // matching blueprint, frame or building already on the cell is applied
    // as it stands. Go matches what stands by geometry (#1355).
    internal sealed class BuildingActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            if (Existing(map, action.Building.Placement) != null) return null;
            return NativeConstructionPlan.Prepare(map, action.Building.Placement, context, out _, out _, out var failure) ? null : failure;
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var candidate = action.Building.Placement;
            var existing = Existing(map, candidate);
            if (existing != null)
            {
                return new Receipts.EffectEvidence { Construction = Effect(existing.Value.Thing, candidate) };
            }
            if (!NativeConstructionPlan.Prepare(map, candidate, context, out var plan, out _, out var failure))
                throw new InvalidOperationException("Placement became invalid: " + failure.Detail);
            var observed = plan.Proposed();
            var placed = plan.Place(observed) ?? throw new InvalidOperationException("Native placement returned no object.");
            observed.OriginThingId = observed.CurrentThingId = placed.GetUniqueLoadID();
            observed.Stage = Stage(placed);
            observed.Present = true; observed.Started = true; observed.Failed = false;
            return new Receipts.EffectEvidence { Construction = observed };
        }

        // Existing is the player's blueprint, frame or building of the
        // candidate's definition, stuff and rotation anchored on its cell.
        private static (Thing Thing, BuildableDef Definition)? Existing(Map map, RimGovernor.Protocol.Placement.PlacementCandidate? candidate)
        {
            if (candidate == null || !candidate.HasX || !candidate.HasZ || !candidate.HasDefName) return null;
            if (!PlacementPreviewOperation.TryResolveBuildable(candidate.DefName, out var definition, out _) || !(definition is ThingDef thingDef)) return null;
            var cell = new IntVec3(candidate.X, 0, candidate.Z);
            var player = Faction.OfPlayerSilentFail;
            if (player == null || !cell.InBounds(map)) return null;
            var stuff = thingDef.MadeFromStuff
                ? (candidate.HasStuff && candidate.Stuff.Length > 0 ? PlacementPreviewOperation.ResolveThingDef(candidate.Stuff) : GenStuff.DefaultStuffFor(thingDef))
                : null;
            var rotation = candidate.HasRotation && candidate.Rotation != RimGovernor.Protocol.Placement.Rotation.All && candidate.Rotation != RimGovernor.Protocol.Placement.Rotation.Unspecified
                ? new Rot4((int)candidate.Rotation - 1) : Rot4.North;
            foreach (var thing in map.thingGrid.ThingsListAt(cell))
            {
                if (thing.Destroyed || thing.Position != cell || thing.Faction != player) continue;
                var built = thing is Blueprint || thing is Frame ? thing.def.entityDefToBuild : thing.def;
                if (built != thingDef) continue;
                var madeOf = thing is Blueprint_Build blueprint ? blueprint.EntityToBuildStuff() : thing.Stuff;
                if (madeOf != stuff || thingDef.rotatable && thing.Rotation != rotation) continue;
                return (thing, definition);
            }
            return null;
        }

        private static Receipts.ConstructionStage Stage(Thing thing) => thing is Frame ? Receipts.ConstructionStage.Frame
            : thing is Blueprint ? Receipts.ConstructionStage.Blueprint : Receipts.ConstructionStage.Building;

        private static Receipts.ConstructionEffect Effect(Thing thing, RimGovernor.Protocol.Placement.PlacementCandidate candidate) => new Receipts.ConstructionEffect
        {
            DefName = candidate.DefName, Stuff = candidate.Stuff ?? "", Cell = new Common.Cell { X = candidate.X, Z = candidate.Z },
            Rotation = candidate.Rotation, OriginThingId = thing.GetUniqueLoadID(), CurrentThingId = thing.GetUniqueLoadID(),
            Stage = Stage(thing), Present = true, Started = true, Failed = false,
        };
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
            return await ProtoBoundary.OnMainThread(ctx, () => { try { return ProtoBoundary.Encode(NativeActionDispatch.Apply(parsed)); } finally { SnapshotStream.NoteWrite(parsed.DeferSnapshot); } }, cancellationToken).ConfigureAwait(false);
        }
    }
}
