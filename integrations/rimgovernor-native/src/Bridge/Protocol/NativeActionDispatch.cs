#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    // One intent kind of Actions/Apply: validated against live state,
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
        private static readonly Dictionary<Type, IActionHandler> Handlers = new Dictionary<Type, IActionHandler>
        {
            [typeof(Operations.ActionIntent.TradeCase)] = new TradeActionHandler(),
            [typeof(Operations.ActionIntent.CommsTradeRequestCase)] = new CommsTradeRequestActionHandler(),
            [typeof(Operations.ActionIntent.BuildingCase)] = new BuildingActionHandler(),
            [typeof(Operations.ActionIntent.MoveCase)] = new MoveActionHandler(),
            [typeof(Operations.ActionIntent.HaulCase)] = new HaulActionHandler(),
            [typeof(Operations.ActionIntent.DraftCase)] = new DraftActionHandler(),
            [typeof(Operations.ActionIntent.CombatOrdersCase)] = new CombatOrdersActionHandler(),
            [typeof(Operations.ActionIntent.ApparelPolicyCase)] = new ApparelPolicyActionHandler(),
            [typeof(Operations.ActionIntent.ResearchCase)] = new ResearchActionHandler(),
            [typeof(Operations.ActionIntent.DialogCase)] = new DialogActionHandler(),
            [typeof(Operations.ActionIntent.PrisonerCase)] = new PrisonerInteractionActionHandler(),
            [typeof(Operations.ActionIntent.AcceptQuestCase)] = new AcceptQuestActionHandler(),
            [typeof(Operations.ActionIntent.QuestShuttleCase)] = new QuestShuttleActionHandler(),
            [typeof(Operations.ActionIntent.HackDesignationCase)] = new HackDesignationActionHandler(),
            [typeof(Operations.ActionIntent.GiveItemCase)] = new GiveItemActionHandler(),
            [typeof(Operations.ActionIntent.RitualCase)] = new RitualActionHandler(),
            [typeof(Operations.ActionIntent.GatheringCase)] = new GatheringActionHandler(),
            [typeof(Operations.ActionIntent.IdeoligionReformCase)] = new IdeoligionReformActionHandler(),
            [typeof(Operations.ActionIntent.AbilityCase)] = new AbilityActionHandler(),
            [typeof(Operations.ActionIntent.IgniteCase)] = new IgniteActionHandler(),
            [typeof(Operations.ActionIntent.FormCaravanCase)] = new FormCaravanActionHandler(),
            [typeof(Operations.ActionIntent.AssignCase)] = new AssignActionHandler(),
            [typeof(Operations.ActionIntent.WorkSettingsCase)] = new WorkSettingsActionHandler(),
            [typeof(Operations.ActionIntent.ProductionBillCase)] = new ProductionBillActionHandler(),
            [typeof(Operations.ActionIntent.HusbandryCase)] = new HusbandryActionHandler(),
            [typeof(Operations.ActionIntent.ZoneCase)] = new ZoneActionHandler(),
            [typeof(Operations.ActionIntent.RemoveFloorCase)] = new FloorRemovalActionHandler(),
            [typeof(Operations.ActionIntent.DesignateCase)] = new DesignateActionHandler(),
            [typeof(Operations.ActionIntent.RelocateCase)] = new RelocateActionHandler(),
            [typeof(Operations.ActionIntent.BuildingPatchCase)] = new BuildingPatchActionHandler(),
            [typeof(Operations.ActionIntent.GiveJobCase)] = new GiveJobActionHandler(),
            [typeof(Operations.ActionIntent.AreaCase)] = new AreaActionHandler(),
            [typeof(Operations.ActionIntent.PolicyPruneCase)] = new PolicyPruneActionHandler(),
            [typeof(Operations.ActionIntent.RemoveRoofCase)] = new RemoveRoofActionHandler(),
            [typeof(Operations.ActionIntent.BuildRoofCase)] = new BuildRoofActionHandler(),
            [typeof(Operations.ActionIntent.AreaPlantCutCase)] = new AreaPlantCutActionHandler(),
            [typeof(Operations.ActionIntent.ReadingPolicyCase)] = new ReadingPolicyActionHandler(),
            [typeof(Operations.ActionIntent.DrugPolicyCase)] = new DrugPolicyActionHandler(),
            [typeof(Operations.ActionIntent.FoodPolicyCase)] = new FoodPolicyActionHandler(),
            [typeof(Operations.ActionIntent.AutoHomeAreaCase)] = new AutoHomeAreaActionHandler(),
            [typeof(Operations.ActionIntent.PawnSettingsCase)] = new PawnSettingsActionHandler(),
            [typeof(Operations.ActionIntent.RulesAttachCase)] = new RulesAttachActionHandler(),
            [typeof(Operations.ActionIntent.RemoveProductionBillCase)] = new RemoveProductionBillActionHandler(),
        };

        private const int ReplayCapacity = 256;
        private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Operations.ActionResult>>> replay = new Dictionary<string, LinkedListNode<KeyValuePair<string, Operations.ActionResult>>>();
        private static readonly LinkedList<KeyValuePair<string, Operations.ActionResult>> order = new LinkedList<KeyValuePair<string, Operations.ActionResult>>();
        private static string replayLoad = "";

        // Fails the mod load when an Action arm has no handler.
        internal static void AssertComplete()
        {
            var missing = typeof(Operations.ActionIntent).GetNestedTypes()
                .Where(c => c.BaseType == typeof(Operations.ActionIntent) && !Handlers.ContainsKey(c)).Select(c => c.Name).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException("Actions/Apply has no handler for: " + string.Join(", ", missing));
        }

        internal static Operations.ApplyReply Apply(Operations.ApplyRequest request)
        {
            if (!ProtoBoundary.ValidateIdentity(request.Identity, out var context, out var failure))
                return new Operations.ApplyReply { BatchFailure = failure };
            var results = new List<Operations.ActionResult>();
            foreach (var action in request.Actions)
                results.Add(ApplyOne(action, context));
            return new Operations.ApplyReply { Results = [.. results] };
        }

        private static Operations.ActionResult ApplyOne(Operations.Action action, Common.ObservationContext context)
        {
            if (action.Key is null || !ProtoBoundary.IsIdentifier(action.Key))
                return Refused((action.Key ?? ""), Common.FailureCode.InvalidRequest, "An action requires an identifier key.");
            var slot = (context.Identity?.ColonyId ?? "") + "/" + (context.Identity?.LoadToken ?? "") + "/" + (action.Key ?? "");
            var load = (context.Identity?.ColonyId ?? "") + "/" + (context.Identity?.LoadToken ?? "");
            if (load != replayLoad) { replay.Clear(); order.Clear(); replayLoad = load; }
            if (replay.TryGetValue(slot, out var seen))
            {
                if (seen.List != null) { order.Remove(seen); order.AddFirst(seen); }
                return seen.Value.Value;
            }
            var result = Decide(action, context);
            var node = new LinkedListNode<KeyValuePair<string, Operations.ActionResult>>(new KeyValuePair<string, Operations.ActionResult>(slot, result));
            replay[slot] = node;
            // Stockpile creation cannot be reconstructed by adopting zones on
            // the drag: keep its receipt in this existing replay window until
            // the load changes. All other intents retain the bounded LRU.
            if ((result.Outcome?.Applied?.Outcome?.Applied?.Observed?.Effect?.Zone?.Created.Count ?? 0) == 0) order.AddFirst(node);
            if (order.Count > ReplayCapacity) { replay.Remove(order.Last!.Value.Key); order.RemoveLast(); }
            return result;
        }

        private static Operations.ActionResult Decide(Operations.Action action, Common.ObservationContext context)
        {
            if (action.Intent is null || !Handlers.TryGetValue(action.Intent.GetType(), out var handler))
                return Refused((action.Key ?? ""), Common.FailureCode.Unsupported, "No handler for this action kind.");
            try
            {
                var refusal = handler.Validate(action, context);
                if (refusal != null) return Refused((action.Key ?? ""), refusal.Code.GetValueOrDefault(), (refusal.Detail ?? ""), refusal.RefusalClass.GetValueOrDefault());
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refused((action.Key ?? ""), Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                Receipts.EffectEvidence evidence;
                using (authority.Owned())
                using (BridgeTools.OperationIntent.Scope(action.Purpose)) evidence = handler.Apply(action, context);
                return new Operations.ActionResult { Key = action.Key, Outcome = new Receipts.Receipt
                    { AdmittedContext = context, Outcome = new Receipts.Applied { Observed = evidence } } };
            }
            catch (ApplyRefusedException refused)
            {
                return Refused((action.Key ?? ""), refused.Code, refused.Message, refused.RefusalClass);
            }
            catch (Exception error)
            {
                ModLog.Error("action", "Action " + action.Intent?.GetType().Name + " failed: " + error);
                return new Operations.ActionResult { Key = action.Key, Outcome = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Action failed: " + error.GetType().Name) };
            }
        }

        // Every refusal leaves here with a class: the site's own, else the one its
        // code implies (ProtoBoundary.RefusalClassOf).
        private static Operations.ActionResult Refused(string key, Common.FailureCode code, string reason, Common.RefusalClass refusalClass = Common.RefusalClass.Unspecified) => new Operations.ActionResult
        { Key = key ?? "", Outcome = new Operations.Refusal { Code = code, Reason = reason ?? "", RefusalClass = refusalClass != Common.RefusalClass.Unspecified ? refusalClass : ProtoBoundary.RefusalClassOf(code) } };
    }

    // Thrown from Apply when native state already changed but the intent's
    // effect did not happen: the action is refused, not failed.
    internal sealed class ApplyRefusedException : Exception
    {
        internal Common.FailureCode Code { get; }
        internal Common.RefusalClass RefusalClass { get; }
        internal ApplyRefusedException(Common.FailureCode code, string message, Common.RefusalClass refusalClass = Common.RefusalClass.Unspecified) : base(message) { Code = code; RefusalClass = refusalClass; }
        internal ApplyRefusedException(Common.Failure failure) : this(failure.Code.GetValueOrDefault(), (failure.Detail ?? ""), failure.RefusalClass.GetValueOrDefault()) { }
    }

    internal sealed class TradeActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) =>
            NativeTradeOperations.Validate(action.Intent?.Trade ?? new Operations.TradeIntent(), context.Identity ?? new Common.Identity());
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) =>
            NativeTradeOperations.Apply(action.Intent?.Trade ?? new Operations.TradeIntent(), context);
    }

    // BuildingIntent: place one ordinary blueprint (or an instant building)
    // through NativeConstructionPlan, validated against the live map. A
    // matching blueprint, frame or building already on the cell is applied
    // as it stands. Go matches what stands by geometry.
    internal sealed class BuildingActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var intent = action.Intent?.Building ?? new Operations.BuildingIntent();
            var hasSkill = intent.MinimumFinishingSkill is not null;
            var hasTier = intent.Tier is not null;
            var hasTarget = intent.ExistingTargetId is not null;
            var replaceWall = intent.ReplaceWall.GetValueOrDefault();
            if (hasSkill && (intent.MinimumFinishingSkill < 0 || intent.MinimumFinishingSkill > 1000)
                || hasTier && (intent.Tier < 0 || intent.Tier > ConstructionSkillGuard.MaxTier)
                || hasTarget && !hasSkill && !hasTier
                || replaceWall && hasTarget)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Invalid construction setting.");
            if (hasSkill)
            {
                if (intent.Placement == null || !PlacementPreviewOperation.TryResolveBuildable(intent.Placement.DefName ?? "", out var definition, out _)
                    || !(definition is ThingDef qualityDef) || !qualityDef.HasComp(typeof(CompQuality)))
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Finishing minimum requires a quality-bearing building.");
            }
            if (hasSkill || hasTier)
            {
                var existing = Existing(map, intent.Placement);
                if (existing != null && !(existing.Value.Thing is Blueprint_Build || existing.Value.Thing is Frame))
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Completed building has no finishing work.");
                var set = existing == null ? null : ConstructionSkillGuard.Setting(existing.Value.Thing);
                if (hasSkill && set != null && set.Minimum != ConstructionSkillSetting.None && set.Minimum != intent.MinimumFinishingSkill)
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Construction minimum is already set.");
            }
            if (hasTarget)
            {
                var target = Existing(map, intent.Placement);
                if (target == null || target.Value.Thing.GetUniqueLoadID() != intent.ExistingTargetId
                    || !(target.Value.Thing is Blueprint_Build || target.Value.Thing is Frame)
                    || hasSkill && !ConstructionSkillGuard.Quality(target.Value.Thing))
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Construction setting target is stale.");
            }
            if (Existing(map, intent.Placement) != null) return null;
            return replaceWall ? WallReplacement(map, intent, context, out _)
                : NativeConstructionPlan.Prepare(map, intent.Placement, context, out _, out _, out var failure) ? null : failure;
        }

        // In-place wall swap (#2529): refuse with a typed code when no built wall
        // stands at the cell, the game refuses the replacement blueprint, or the
        // swap would drop the last holder of a roof (a Frame holds no roof).
        private static Common.Failure? WallReplacement(Map map, Operations.BuildingIntent intent, Common.ObservationContext context, out NativeConstructionPlan? plan)
        {
            plan = null;
            var candidate = intent.Placement;
            if (candidate == null || candidate.X is null || candidate.Z is null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Wall replacement requires a placement cell.");
            var wall = WallLayerGuard.BuiltWall(map, new IntVec3(candidate.X.GetValueOrDefault(), 0, candidate.Z.GetValueOrDefault()));
            if (wall == null) return ProtoBoundary.Fail(Common.FailureCode.NoWallToReplace, "No built wall stands at the cell.");
            if (!NativeConstructionPlan.Prepare(map, candidate, context, out plan, out _, out var failure)
                || !(plan.Definition is ThingDef built) || built.passability != Traversability.Impassable)
                return ProtoBoundary.Fail(Common.FailureCode.WallReplacementRefused, failure?.Detail ?? "The replacement is not an impassable building.");
            var blocker = RoofSupportSafety.Blocker(wall, out _);
            return blocker == null ? null : ProtoBoundary.Fail(Common.FailureCode.WallReplacementStrandsRoof, blocker);
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var map = ProtoBoundary.LoadedMap(context);
            var intent = action.Intent?.Building ?? new Operations.BuildingIntent();
            var candidate = intent.Placement;
            if (candidate is not null && Existing(map, candidate) is { } existing)
            {
                if (intent.ExistingTargetId is not null && existing.Thing.GetUniqueLoadID() != intent.ExistingTargetId)
                    throw new InvalidOperationException("Construction setting target changed.");
                if (intent.MinimumFinishingSkill is { } skill) ConstructionSkillGuard.Set(existing.Thing, skill);
                if (intent.Tier is { } existingTier) ConstructionSkillGuard.SetTier(existing.Thing, existingTier);
                return new Receipts.EffectEvidence { Effect = Effect(existing.Thing, candidate) };
            }
            if (intent.ExistingTargetId is not null) throw new InvalidOperationException("Construction setting target disappeared.");
            if (!NativeConstructionPlan.Prepare(map, candidate, context, out var plan, out _, out var failure))
                throw new InvalidOperationException("Placement became invalid: " + (failure.Detail ?? ""));
            var observed = plan.Proposed();
            var placed = plan.Place(ref observed, intent.ReplaceWall.GetValueOrDefault()) ??throw new InvalidOperationException("Native placement returned no object.");
            if (intent.MinimumFinishingSkill is { } placedSkill) ConstructionSkillGuard.Set(placed, placedSkill);
            // An instant building is placed finished: there is no site to gate, so the tier has nothing to bind (#2733).
            if (intent.Tier is { } tier && (placed is Blueprint_Build || placed is Frame)) ConstructionSkillGuard.SetTier(placed, tier);
            observed = observed with { OriginThingId = placed.GetUniqueLoadID(), CurrentThingId = placed.GetUniqueLoadID() };
            observed = observed with { Stage = Stage(placed), Present = true, Started = true, Failed = false };
            return new Receipts.EffectEvidence { Effect = observed };
        }

        // Existing is the player's blueprint, frame or building of the
        // candidate's definition, stuff and rotation anchored on its cell.
        private static (Thing Thing, BuildableDef Definition)? Existing(Map map, RimGovernor.Contracts.Wire.Placement.PlacementCandidate? candidate)
        {
            if (candidate == null || candidate.X is null || candidate.Z is null || candidate.DefName is null) return null;
            if (!PlacementPreviewOperation.TryResolveBuildable((candidate.DefName ?? ""), out var definition, out _) || !(definition is ThingDef thingDef)) return null;
            var cell = new IntVec3(candidate.X.GetValueOrDefault(), 0, candidate.Z.GetValueOrDefault());
            var player = Faction.OfPlayerSilentFail;
            if (player == null || !cell.InBounds(map)) return null;
            var stuff = thingDef.MadeFromStuff
                ? (candidate.Stuff is not null && (candidate.Stuff ?? "").Length > 0 ? PlacementPreviewOperation.ResolveThingDef((candidate.Stuff ?? "")) : GenStuff.DefaultStuffFor(thingDef))
                : null;
            var rotation = candidate.Rotation is not null && candidate.Rotation.GetValueOrDefault() != RimGovernor.Contracts.Wire.Placement.Rotation.All && candidate.Rotation.GetValueOrDefault() != RimGovernor.Contracts.Wire.Placement.Rotation.Unspecified
                ? new Rot4((int)candidate.Rotation.GetValueOrDefault() - 1) : Rot4.North;
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

        private static Receipts.ConstructionEffect Effect(Thing thing, RimGovernor.Contracts.Wire.Placement.PlacementCandidate candidate) => new Receipts.ConstructionEffect
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
            if (!ProtoBoundary.TryParse<Operations.ApplyRequest>(ctx, "rimgovernor/operations_apply", request, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Operations.ApplyReply { BatchFailure = failure });
            return await ProtoBoundary.OnMainThreadEncoded(ctx, () => HandleApply(parsed), cancellationToken).ConfigureAwait(false);
        }

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static Operations.ApplyReply HandleApply(Operations.ApplyRequest parsed)
        {
            try { return NativeActionDispatch.Apply(parsed); } finally { SnapshotStream.NoteWrite(parsed.DeferSnapshot.GetValueOrDefault()); }
        }
    }
}
