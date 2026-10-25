using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // One intent kind of Actions/Apply: validated against live state,
    // then applied under native authority on the game thread.
    internal interface IActionHandler
    {
        // Null when the action applies to live state now, else the refusal.
        RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context);
        RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context);
    }

    // Actions/Apply: every Action arm maps to exactly one handler. Actions in
    // a batch apply in order and independently. Nothing is replayed: the
    // in-process hop always returns the result of a body that ran
    // (GameThreadQueue), so no reply is lost after the write.
    internal static class NativeActionDispatch
    {
        private static readonly Dictionary<Type, IActionHandler> Handlers = new Dictionary<Type, IActionHandler>
        {
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.TradeCase)] = new TradeActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.CommsTradeRequestCase)] = new CommsTradeRequestActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase)] = new BuildingActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.MoveCase)] = new MoveActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.HaulCase)] = new HaulActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.DraftCase)] = new DraftActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.CombatOrdersCase)] = new CombatOrdersActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.ApparelPolicyCase)] = new ApparelPolicyActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.ResearchCase)] = new ResearchActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.DialogCase)] = new DialogActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.PrisonerCase)] = new PrisonerInteractionActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.AcceptQuestCase)] = new AcceptQuestActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.QuestShuttleCase)] = new QuestShuttleActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.HackDesignationCase)] = new HackDesignationActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.GiveItemCase)] = new GiveItemActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.RitualCase)] = new RitualActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.GatheringCase)] = new GatheringActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.IdeoligionReformCase)] = new IdeoligionReformActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.AbilityCase)] = new AbilityActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.ConversionCase)] = new ConversionActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.FormCaravanCase)] = new FormCaravanActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.AssignCase)] = new AssignActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.WorkSettingsCase)] = new WorkSettingsActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.ProductionBillCase)] = new ProductionBillActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.HusbandryCase)] = new HusbandryActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.ZoneCase)] = new ZoneActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.RemoveFloorCase)] = new FloorRemovalActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.DesignateCase)] = new DesignateActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.RelocateCase)] = new RelocateActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingPatchCase)] = new BuildingPatchActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.PawnActivitiesCase)] = new PawnActivitiesActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.GiveJobCase)] = new GiveJobActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.AreaCase)] = new AreaActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.PolicyPruneCase)] = new PolicyPruneActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.RemoveRoofCase)] = new RemoveRoofActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildRoofCase)] = new BuildRoofActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.AreaPlantCutCase)] = new AreaPlantCutActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.ReadingPolicyCase)] = new ReadingPolicyActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.DrugPolicyCase)] = new DrugPolicyActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.FoodPolicyCase)] = new FoodPolicyActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.AutoHomeAreaCase)] = new AutoHomeAreaActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.PawnSettingsCase)] = new PawnSettingsActionHandler(),
            [typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent.RemoveProductionBillCase)] = new RemoveProductionBillActionHandler(),
        };

        // Fails the mod load when an Action arm has no handler.
        internal static void AssertComplete()
        {
            var missing = typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent).GetNestedTypes()
                .Where(c => c.BaseType == typeof(RimGovernor.Brain.GameOrders.Operations.ActionIntent) && !Handlers.ContainsKey(c)).Select(c => c.Name).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException("Actions/Apply has no handler for: " + string.Join(", ", missing));
        }

        internal static RimGovernor.Brain.GameFacts.Operations.ApplyReply Apply(RimGovernor.Brain.GameOrders.Operations.ApplyRequest request)
        {
            if (!GameThread.ValidateIdentity(request.Identity, out var context, out var failure))
                return new RimGovernor.Brain.GameFacts.Operations.ApplyReply { BatchFailure = failure };
            var results = new List<RimGovernor.Brain.GameFacts.Operations.ActionResult>();
            foreach (var action in request.Actions)
                results.Add(ApplyOne(action, context));
            return new RimGovernor.Brain.GameFacts.Operations.ApplyReply { Results = [.. results] };
        }

        private static RimGovernor.Brain.GameFacts.Operations.ActionResult ApplyOne(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            if (action.Key is null || !GameThread.IsIdentifier(action.Key))
                return Refused((action.Key ?? ""), RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "An action requires an identifier key.");
            if (action.Intent is null || !Handlers.TryGetValue(action.Intent.GetType(), out var handler))
                return Refused((action.Key ?? ""), RimGovernor.Brain.GameFacts.Common.FailureCode.Unsupported, "No handler for this action kind.");
            try
            {
                var refusal = handler.Validate(action, context);
                if (refusal != null) return Refused((action.Key ?? ""), refusal.Code, (refusal.Detail ?? ""), refusal.RefusalClass);
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refused((action.Key ?? ""), RimGovernor.Brain.GameFacts.Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                RimGovernor.Brain.GameFacts.Receipts.EffectEvidence evidence;
                using (authority.Owned())
                using (RimGovernor.Native.OperationIntent.Scope(action.Purpose)) evidence = handler.Apply(action, context);
                return new RimGovernor.Brain.GameFacts.Operations.ActionResult { Key = action.Key, Outcome = new RimGovernor.Brain.GameFacts.Receipts.Receipt
                    { AdmittedContext = context, Outcome = new RimGovernor.Brain.GameFacts.Receipts.Applied { Observed = evidence } } };
            }
            catch (ApplyRefusedException refused)
            {
                return Refused((action.Key ?? ""), refused.Code, refused.Message, refused.RefusalClass);
            }
            catch (Exception error)
            {
                ModLog.Error("action", "Action " + action.Intent?.GetType().Name + " failed: " + error);
                return new RimGovernor.Brain.GameFacts.Operations.ActionResult { Key = action.Key, Outcome = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NativeFailure, "Action failed: " + error.GetType().Name) };
            }
        }

        // Every refusal leaves here with a class: the site's own, else the one its
        // code implies (GameThread.RefusalClassOf).
        private static RimGovernor.Brain.GameFacts.Operations.ActionResult Refused(string key, RimGovernor.Brain.GameFacts.Common.FailureCode code, string reason) =>
            Refused(key, code, reason, GameThread.RefusalClassOf(code));

        private static RimGovernor.Brain.GameFacts.Operations.ActionResult Refused(string key, RimGovernor.Brain.GameFacts.Common.FailureCode code, string reason, RimGovernor.Brain.GameFacts.Common.RefusalClass refusalClass) => new RimGovernor.Brain.GameFacts.Operations.ActionResult
        { Key = key ?? "", Outcome = new RimGovernor.Brain.GameFacts.Operations.Refusal { Code = code, Reason = reason ?? "", RefusalClass = refusalClass } };
    }

    // Thrown from Apply when native state already changed but the intent's
    // effect did not happen: the action is refused, not failed.
    internal sealed class ApplyRefusedException : Exception
    {
        internal RimGovernor.Brain.GameFacts.Common.FailureCode Code { get; }
        internal RimGovernor.Brain.GameFacts.Common.RefusalClass RefusalClass { get; }
        internal ApplyRefusedException(RimGovernor.Brain.GameFacts.Common.FailureCode code, string message) : this(code, message, GameThread.RefusalClassOf(code)) { }
        internal ApplyRefusedException(RimGovernor.Brain.GameFacts.Common.FailureCode code, string message, RimGovernor.Brain.GameFacts.Common.RefusalClass refusalClass) : base(message) { Code = code; RefusalClass = refusalClass; }
        internal ApplyRefusedException(RimGovernor.Brain.GameFacts.Common.Failure failure) : this(failure.Code, (failure.Detail ?? ""), failure.RefusalClass) { }
    }

    internal sealed class TradeActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) =>
            NativeTradeOperations.Validate(action.Intent?.Trade ?? new RimGovernor.Brain.GameOrders.Operations.TradeIntent(), context.Identity);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) =>
            NativeTradeOperations.Apply(action.Intent?.Trade ?? new RimGovernor.Brain.GameOrders.Operations.TradeIntent(), context);
    }

    // BuildingIntent: place one ordinary blueprint (or an instant building)
    // through NativeConstructionPlan, validated against the live map. A
    // matching blueprint, frame or building already on the cell is applied
    // as it stands. The brain matches what stands by geometry.
    internal sealed class BuildingActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var map = GameThread.LoadedMap(context);
            var intent = action.Intent?.Building ?? throw new InvalidOperationException("Building intent is missing.");
            var hasSkill = intent.MinimumFinishingSkill is RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange.Selected;
            var hasTier = intent.Tier is RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange.Selected;
            var hasTarget = intent.ExistingTargetId is not null;
            var replaceWall = intent.ReplaceWall;
            if (intent.MinimumFinishingSkill is null || intent.Tier is null || hasSkill && (((RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange.Selected)intent.MinimumFinishingSkill).Minimum < 0 || ((RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange.Selected)intent.MinimumFinishingSkill).Minimum > 1000)
                || hasTier && (((RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange.Selected)intent.Tier).Tier < 0 || ((RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange.Selected)intent.Tier).Tier > ConstructionSkillGuard.MaxTier)
                || hasTarget && !hasSkill && !hasTier
                || replaceWall && hasTarget)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Invalid construction setting.");
            if (hasSkill)
            {
                if (intent.Placement == null || !PlacementPreviewOperation.TryResolveBuildable(intent.Placement.DefName, out var definition, out _)
                    || !(definition is ThingDef qualityDef) || !qualityDef.HasComp(typeof(CompQuality)))
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Finishing minimum requires a quality-bearing building.");
            }
            if (hasSkill || hasTier)
            {
                var existing = Existing(map, intent.Placement);
                if (existing != null && !(existing.Value.Thing is Blueprint_Build || existing.Value.Thing is Frame))
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Completed building has no finishing work.");
                var set = existing == null ? null : ConstructionSkillGuard.Setting(existing.Value.Thing);
                if (hasSkill && set != null && set.Minimum != ConstructionSkillSetting.None && set.Minimum != ((RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange.Selected)intent.MinimumFinishingSkill).Minimum)
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Construction minimum is already set.");
            }
            if (hasTarget)
            {
                var target = Existing(map, intent.Placement);
                if (target == null || target.Value.Thing.GetUniqueLoadID() != intent.ExistingTargetId
                    || !(target.Value.Thing is Blueprint_Build || target.Value.Thing is Frame)
                    || hasSkill && !ConstructionSkillGuard.Quality(target.Value.Thing))
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Construction setting target is stale.");
            }
            if (Existing(map, intent.Placement) != null) return null;
            return replaceWall ? WallReplacement(map, intent, context, out _)
                : NativeConstructionPlan.Prepare(map, intent.Placement, context, out _, out _, out var failure) ? null : failure;
        }

        // In-place wall swap (#2529): refuse with a typed code when no built wall
        // stands at the cell, the game refuses the replacement blueprint, or the
        // swap would drop the last holder of a roof (a Frame holds no roof).
        private static RimGovernor.Brain.GameFacts.Common.Failure? WallReplacement(Map map, RimGovernor.Brain.GameOrders.Operations.BuildingIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out NativeConstructionPlan? plan)
        {
            plan = null;
            var candidate = intent.Placement;
            if (candidate == null)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Wall replacement requires a placement cell.");
            var wall = WallLayerGuard.BuiltWall(map, new IntVec3(candidate.X, 0, candidate.Z));
            if (wall == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NoWallToReplace, "No built wall stands at the cell.");
            if (!NativeConstructionPlan.Prepare(map, candidate, context, out plan, out _, out var failure)
                || !(plan.Definition is ThingDef built) || built.passability != Traversability.Impassable)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.WallReplacementRefused, failure?.Detail ?? "The replacement is not an impassable building.");
            var blocker = RoofSupportSafety.Blocker(wall, out _);
            return blocker == null ? null : GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.WallReplacementStrandsRoof, blocker);
        }

        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var map = GameThread.LoadedMap(context);
            var intent = action.Intent?.Building ?? throw new InvalidOperationException("Building intent is missing.");
            var candidate = intent.Placement;
            if (candidate is not null && Existing(map, candidate) is { } existing)
            {
                if (intent.ExistingTargetId is not null && existing.Thing.GetUniqueLoadID() != intent.ExistingTargetId)
                    throw new InvalidOperationException("Construction setting target changed.");
                if (intent.MinimumFinishingSkill is RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange.Selected { Minimum: var skill }) ConstructionSkillGuard.Set(existing.Thing, skill);
                if (intent.Tier is RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange.Selected { Tier: var existingTier }) ConstructionSkillGuard.SetTier(existing.Thing, existingTier);
                return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect.ConstructionCase() };
            }
            if (intent.ExistingTargetId is not null) throw new InvalidOperationException("Construction setting target disappeared.");
            if (!NativeConstructionPlan.Prepare(map, candidate, context, out var plan, out _, out var failure))
                throw new InvalidOperationException("Placement became invalid: " + (failure.Detail ?? ""));
            var placed = plan.Place(intent.ReplaceWall) ??throw new InvalidOperationException("Native placement returned no object.");
            if (intent.MinimumFinishingSkill is RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange.Selected { Minimum: var placedSkill }) ConstructionSkillGuard.Set(placed, placedSkill);
            // An instant building is placed finished: there is no site to gate, so the tier has nothing to bind (#2733).
            if (intent.Tier is RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange.Selected { Tier: var tier } && (placed is Blueprint_Build || placed is Frame)) ConstructionSkillGuard.SetTier(placed, tier);
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect.ConstructionCase() };
        }

        // Existing is the player's blueprint, frame or building of the
        // candidate's definition, stuff and rotation anchored on its cell.
        private static (Thing Thing, BuildableDef Definition)? Existing(Map map, RimGovernor.Brain.GameOrders.Placement.PlacementCandidate? candidate)
        {
            if (candidate == null) return null;
            if (!PlacementPreviewOperation.TryResolveBuildable(candidate.DefName, out var definition, out _) || !(definition is ThingDef thingDef)) return null;
            var cell = new IntVec3(candidate.X, 0, candidate.Z);
            var player = Faction.OfPlayerSilentFail;
            if (player == null || !cell.InBounds(map)) return null;
            var stuff = thingDef.MadeFromStuff
                ? (candidate.Stuff.Length > 0 ? PlacementPreviewOperation.ResolveThingDef(candidate.Stuff) : GenStuff.DefaultStuffFor(thingDef))
                : null;
            var rotation = candidate.Rotation != RimGovernor.Brain.GameFacts.Placement.Rotation.All
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
    }

    internal static class NativeActionTools
    {

        // On the game thread: the typed core of the tool, shared with the in-process facade.
        internal static RimGovernor.Brain.GameFacts.Operations.ApplyReply HandleApply(RimGovernor.Brain.GameOrders.Operations.ApplyRequest parsed)
        {
            try { return NativeActionDispatch.Apply(parsed); } finally { SnapshotStream.NoteWrite(parsed.DeferSnapshot); }
        }
    }
}
