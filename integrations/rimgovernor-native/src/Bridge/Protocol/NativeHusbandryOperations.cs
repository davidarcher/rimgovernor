using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    // The herd Need's animal facts and eligibility rules, shared by the
    // pawn animal details (NativePawnDetails.Animal) and HusbandryIntent.
    // Master and following are refused unless Obedience is learned, because
    // RimWorld's own Master setter Log.ErrorOnce's (pausing the colony)
    // otherwise.
    internal static class NativeHusbandryOperations
    {
        internal static bool Designated(Pawn animal, DesignationDef def) => animal.Map.designationManager.DesignationOn(animal, def) != null;

        // The Sterilized hediff, and the queued sterilize surgery bill (the
        // bill whose recipe adds it).
        internal static bool Sterilized(Pawn animal) => animal.health.hediffSet.HasHediff(HediffDefOf.Sterilized);
        internal static Bill_Medical? SterilizeBill(Pawn animal) => animal.BillStack?.Bills.OfType<Bill_Medical>()
            .FirstOrDefault(b => b.recipe.addsHediff == HediffDefOf.Sterilized);

        // The animal's own whole-body sterilize recipe, found live on its
        // race's recipes by what it adds; null when the race offers none
        // (the order then fails loudly, there is no fallback recipe).
        internal static RecipeDef? SterilizeRecipe(Pawn animal) => animal.def.AllRecipes?
            .FirstOrDefault(r => r.addsHediff == HediffDefOf.Sterilized && !r.targetsBodyPart);

        internal static bool Observable(Pawn? animal) => animal != null && !animal.Destroyed && animal.Spawned
            && ProtoBoundary.IsLoaded(animal.Map) && animal.RaceProps.Animal && !animal.Dead && !animal.Position.Fogged(animal.Map);

        internal static bool Eligible(Pawn? animal) => Observable(animal) && (animal ?? throw new System.InvalidOperationException("Required native value is unavailable: animal.")).Faction == Faction.OfPlayer;

        // A factionless animal on the current map, the only kind the tame
        // designator can target.
        internal static bool EligibleWild(Pawn? animal) => Observable(animal) && (animal ?? throw new System.InvalidOperationException("Required native value is unavailable: animal.")).Faction == null;

        // Native tame eligibility: the designator's own acceptance plus no
        // standing tame or hunt designation.
        internal static bool Tameable(Pawn animal) => EligibleWild(animal) && TameUtility.CanTame(animal)
            && !Designated(animal, DesignationDefOf.Tame) && !Designated(animal, DesignationDefOf.Hunt)
            && new Designator_Tame().CanDesignateThing(animal).Accepted;

        // Whether the player's primary ideo venerates the animal's race. What
        // precepts make of slaughter and eating comes from the catalog's
        // precept effects (policy.ActionStance), not from here.
        internal static bool Venerated(Pawn animal)
        {
            var ideo = ModsConfig.IdeologyActive ? Faction.OfPlayer.ideos?.PrimaryIdeo : null;
            return ideo != null && ideo.IsVeneratedAnimal(animal);
        }

        // The ticks until Pawn_AgeTracker reaches the next stage's minAge;
        // never in the last stage or while the animal does not age.
        internal static Observations.AnimalNextLifeStage NextLifeStage(Pawn animal)
        {
            var tracker = animal.ageTracker;
            var stages = animal.RaceProps.lifeStageAges;
            if (stages == null || tracker.CurLifeStageIndex + 1 >= stages.Count) return new Observations.AnimalNextLifeStage.LastStage();
            if (tracker.BiologicalTicksPerTick <= 0f) return new Observations.AnimalNextLifeStage.NotAging();
            return new Observations.AnimalNextLifeStage.InTicks(Math.Max(0L, (long)Math.Ceiling((stages[tracker.CurLifeStageIndex + 1].minAge * 3600000.0 - tracker.AgeBiologicalTicks) / tracker.BiologicalTicksPerTick)));
        }

        // Hediff_Pregnant.Tick's own progress rate: BodyResourceGrowthSpeed over
        // gestationPeriodDays * 60000 per tick; stalled at speed zero or for a dead animal.
        internal static Observations.AnimalPregnancy Pregnancy(Pawn animal)
        {
            var pregnancy = animal.health.hediffSet.hediffs.OfType<Hediff_Pregnant>().FirstOrDefault();
            if (pregnancy == null) return new Observations.AnimalPregnancy.NotPregnant();
            var speed = animal.Dead ? 0f : PawnUtility.BodyResourceGrowthSpeed(animal);
            if (speed <= 0f) return new Observations.AnimalPregnancy.Stalled();
            return new Observations.AnimalPregnancy.Gestating(Math.Max(0L, (long)Math.Ceiling((1.0 - pregnancy.GestationProgress) * animal.RaceProps.gestationPeriodDays * 60000.0 / speed)));
        }

        // The release designator's own acceptance and nothing more; which
        // animals to protect (bonded, mastered, pregnant, downed) is brain herd
        // policy (UpkeepAnimal.ReleaseAllowed).
        internal static bool SafeToRelease(Pawn animal) => !animal.Dead && new Designator_ReleaseAnimalToWild().CanDesignateThing(animal).Accepted;

        // Current Animals-tab settings, always the real GetUniqueLoadID()
        // identities the pawn animal details publish ("" when unset).
        internal static string AreaId(Pawn animal) => animal.playerSettings?.AreaRestrictionInPawnCurrentMap?.GetUniqueLoadID() ?? "";
        internal static string MasterId(Pawn animal) => animal.playerSettings?.Master?.GetUniqueLoadID() ?? "";
        internal static bool Obedient(Pawn animal) => animal.training != null && animal.training.HasLearned(TrainableDefOf.Obedience);
        internal static bool SupportsAllowedAreas(Pawn animal) => animal.playerSettings != null && animal.playerSettings.SupportsAllowedAreas && animal.MapHeld != null;

        internal static Area_Allowed? ResolveArea(Pawn animal, string entityId) => animal.Map?.areaManager.AllAreas.OfType<Area_Allowed>()
            .FirstOrDefault(a => RefIndex.Is(a, entityId) || a.ID.ToString(System.Globalization.CultureInfo.InvariantCulture) == entityId);
        internal static Pawn? ResolveMaster(Pawn animal, string entityId) => animal.Map?.mapPawns.FreeColonistsSpawned
            .ById(entityId) is Pawn p && !p.Dead ? p : null;
    }

    // HusbandryIntent: one animal's training request, slaughter, tame
    // or release-to-wild designation (or its cancel), a sterilize surgery bill
    // (queued on the animal, whose recipe is found live), or Animals-tab setting;
    // an immediate settings write with no native job. Native checks the
    // animal and the order's own eligibility live when it applies; an order
    // that already holds applies again. Taming and release work is ordinary
    // handler labor afterwards.
    internal sealed class HusbandryActionHandler : IActionHandler
    {
        private const string Kind = "Husbandry";

        private sealed class Target
        {
            internal Pawn? Animal;
            internal TrainableDef? Trainable;
            internal Area_Allowed? Area;
            internal Pawn? Master;
        }

        private static bool Valid(Operations.HusbandryIntent? intent)
        {
            if (intent == null || intent.AnimalId is null || !ProtoBoundary.IsIdentifier(intent.AnimalId)) return false;
            switch (intent.Order)
            {
                case Operations.HusbandryOrder.Train: return intent.TrainableDef is not null && ProtoBoundary.IsIdentifier(intent.TrainableDef);
                case Operations.HusbandryOrder.AllowedArea:
                case Operations.HusbandryOrder.Master: return intent.TargetId is null || ProtoBoundary.IsIdentifier(intent.TargetId);
                case Operations.HusbandryOrder.FollowDrafted:
                case Operations.HusbandryOrder.FollowFieldwork: return true;
                case Operations.HusbandryOrder.Slaughter:
                case Operations.HusbandryOrder.Tame:
                case Operations.HusbandryOrder.Release:
                case Operations.HusbandryOrder.Sterilize:
                case Operations.HusbandryOrder.CancelSlaughter:
                case Operations.HusbandryOrder.CancelRelease: return true;
                default: return false;
            }
        }

        private static DesignationDef? DesignationFor(Operations.HusbandryOrder order)
        {
            switch (order)
            {
                case Operations.HusbandryOrder.Slaughter:
                case Operations.HusbandryOrder.CancelSlaughter: return DesignationDefOf.Slaughter;
                case Operations.HusbandryOrder.Tame: return DesignationDefOf.Tame;
                case Operations.HusbandryOrder.Release:
                case Operations.HusbandryOrder.CancelRelease: return DesignationDefOf.ReleaseAnimalToWild;
                default: return null;
            }
        }

        private static Target Resolve(Operations.HusbandryIntent? intent, Common.ObservationContext context)
        {
            var target = new Target();
            if (intent == null || !Valid(intent)) return target;
            target.Animal = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(intent.AnimalId);
            if (target.Animal == null || !NativeHusbandryOperations.Observable(target.Animal)) { target.Animal = null; return target; }
            if (intent.Order == Operations.HusbandryOrder.Train) target.Trainable = DefDatabase<TrainableDef>.GetNamedSilentFail((intent.TrainableDef ?? ""));
            if (intent.Order == Operations.HusbandryOrder.AllowedArea && intent.TargetId is not null) target.Area = NativeHusbandryOperations.ResolveArea(target.Animal, (intent.TargetId ?? ""));
            if (intent.Order == Operations.HusbandryOrder.Master && intent.TargetId is not null) target.Master = NativeHusbandryOperations.ResolveMaster(target.Animal, (intent.TargetId ?? ""));
            return target;
        }

        // Whether the animal already holds what the order asks for. A tamed
        // animal holds its tame order: the designation is consumed when the
        // taming job brings it into the colony.
        private static bool Holds(Operations.HusbandryIntent intent, Target target)
        {
            var animal = target.Animal;
            if (animal == null) return false;
            switch (intent.Order)
            {
                case Operations.HusbandryOrder.Train: return target.Trainable != null && animal.training != null && animal.training.GetWanted(target.Trainable);
                case Operations.HusbandryOrder.Tame: return animal.Faction == Faction.OfPlayer || NativeHusbandryOperations.Designated(animal, DesignationDefOf.Tame);
                // Queued, or already done: the bill is gone once the surgery has run.
                case Operations.HusbandryOrder.Sterilize: return animal.Faction == Faction.OfPlayer
                    && (NativeHusbandryOperations.Sterilized(animal) || NativeHusbandryOperations.SterilizeBill(animal) != null);
                case Operations.HusbandryOrder.Slaughter:
                case Operations.HusbandryOrder.Release: return animal.Faction == Faction.OfPlayer && NativeHusbandryOperations.Designated(animal, (DesignationFor(intent.Order) ?? throw new InvalidOperationException("The husbandry order has no designation.")));
                case Operations.HusbandryOrder.CancelSlaughter:
                case Operations.HusbandryOrder.CancelRelease: return animal.Faction == Faction.OfPlayer && !NativeHusbandryOperations.Designated(animal, (DesignationFor(intent.Order) ?? throw new InvalidOperationException("The husbandry order has no designation.")));
                case Operations.HusbandryOrder.AllowedArea:
                    return animal.Faction == Faction.OfPlayer && (intent.TargetId is not null
                        ? target.Area != null && NativeHusbandryOperations.AreaId(animal) == target.Area.GetUniqueLoadID()
                        : NativeHusbandryOperations.AreaId(animal) == "");
                case Operations.HusbandryOrder.Master:
                    return animal.Faction == Faction.OfPlayer && (intent.TargetId is not null
                        ? target.Master != null && NativeHusbandryOperations.MasterId(animal) == target.Master.GetUniqueLoadID()
                        : NativeHusbandryOperations.MasterId(animal) == "");
                case Operations.HusbandryOrder.FollowDrafted: return animal.Faction == Faction.OfPlayer && animal.playerSettings != null && animal.playerSettings.followDrafted == intent.Follow;
                case Operations.HusbandryOrder.FollowFieldwork: return animal.Faction == Faction.OfPlayer && animal.playerSettings != null && animal.playerSettings.followFieldwork == intent.Follow;
                default: return false;
            }
        }

        private static bool Standing(Operations.HusbandryIntent? intent, Target target) => intent != null && Valid(intent) && target.Animal != null && Holds(intent, target);

        private static ApplyPreconditions Rules(Operations.HusbandryIntent? intent, Target target)
        {
            var order = intent?.Order;
            var animal = target.Animal;
            bool tame = order == Operations.HusbandryOrder.Tame;
            var rules = new ApplyPreconditions(Kind)
                .Require(() => Valid(intent), "order requires an animal and the order's argument")
                .Present(() => animal != null && (tame ? animal.Faction == null : animal.Faction == Faction.OfPlayer),
                    "animal " + (intent?.AnimalId ?? "") + " is not " + (tame ? "a wild" : "a player") + " animal on the map");
            if (!rules.Holds || animal == null || intent == null) return rules;
            switch (order)
            {
                case Operations.HusbandryOrder.Train:
                    rules.Require(() => target.Trainable != null && animal.training != null && animal.training.CanAssignToTrain(target.Trainable).Accepted,
                            "animal cannot train " + (intent.TrainableDef ?? ""))
                        .Require(() => !NativeHusbandryOperations.Designated(animal, DesignationDefOf.Slaughter) && !NativeHusbandryOperations.Designated(animal, DesignationDefOf.ReleaseAnimalToWild),
                            "animal is designated for removal");
                    break;
                case Operations.HusbandryOrder.Slaughter:
                    rules.Require(() => !animal.Dead && new Designator_Slaughter().CanDesignateThing(animal).Accepted, "native slaughter designator refused the animal");
                    break;
                case Operations.HusbandryOrder.Release:
                    rules.Require(() => NativeHusbandryOperations.SafeToRelease(animal), "native release designator refused the animal");
                    break;
                case Operations.HusbandryOrder.Tame:
                    rules.Require(() => NativeHusbandryOperations.Tameable(animal), "native tame eligibility refused the animal or it is designated for hunting");
                    break;
                case Operations.HusbandryOrder.Sterilize:
                    rules.Require(() => NativeHusbandryOperations.SterilizeRecipe(animal) != null,
                            "race " + animal.def.defName + " offers no whole-body sterilize recipe (no recipe on its race adds the Sterilized hediff)")
                        .Require(() => { var r = NativeHusbandryOperations.SterilizeRecipe(animal); return r != null && r.AvailableNow && r.Worker.AvailableReport(animal).Accepted && r.AvailableOnNow(animal, null); },
                            "sterilize recipe is not available on this animal now");
                    break;
                case Operations.HusbandryOrder.AllowedArea:
                    rules.Require(() => NativeHusbandryOperations.SupportsAllowedAreas(animal), "animal cannot carry an allowed area")
                        .Present(() => intent != null && (intent.TargetId is null || target.Area != null), "allowed area " + (intent.TargetId ?? "") + " is not on the animal's map")
                        .Require(() => NativeWorkSettings.AreaReachable(animal, target.Area), "allowed area must be native-reachable");
                    break;
                case Operations.HusbandryOrder.Master:
                    rules.Require(() => NativeHusbandryOperations.Obedient(animal), "a master requires learned Obedience")
                        .Present(() => intent != null && (intent.TargetId is null || target.Master != null), "master " + (intent.TargetId ?? "") + " is not a spawned free colonist on the animal's map");
                    break;
                case Operations.HusbandryOrder.FollowDrafted:
                case Operations.HusbandryOrder.FollowFieldwork:
                    rules.Require(() => NativeHusbandryOperations.Obedient(animal) && animal.playerSettings != null, "following requires learned Obedience");
                    break;
            }
            return rules;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var target = Resolve(action.Intent?.Husbandry, context);
            if (Standing(action.Intent?.Husbandry, target)) return null;
            var rules = Rules(action.Intent?.Husbandry, target);
            return rules.Holds ? null : rules.Failure();
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.Intent?.Husbandry ?? throw new InvalidOperationException("Husbandry intent missing");
            var target = Resolve(intent, context);
            if (!Standing(intent, target))
            {
                var rules = Rules(intent, target);
                if (!rules.Holds) throw new InvalidOperationException("Husbandry prerequisites changed before apply: " + rules.Reason);
            }
            var animal = target.Animal ?? throw new InvalidOperationException("The husbandry animal is unavailable.");
            if (!Holds(intent, target))
            {
                var designation = DesignationFor(intent.Order);
                switch (intent.Order)
                {
                    case Operations.HusbandryOrder.Train: (animal.training ?? throw new InvalidOperationException("The animal has no training tracker.")).SetWantedRecursive(target.Trainable, true); break;
                    case Operations.HusbandryOrder.Sterilize: HealthCardUtility.CreateSurgeryBill(animal, (NativeHusbandryOperations.SterilizeRecipe(animal) ?? throw new InvalidOperationException("The animal has no sterilize recipe.")), null); break;
                    case Operations.HusbandryOrder.AllowedArea: (animal.playerSettings ?? throw new InvalidOperationException("The animal has no player settings.")).AreaRestrictionInPawnCurrentMap = target.Area; break;
                    case Operations.HusbandryOrder.Master: (animal.playerSettings ?? throw new InvalidOperationException("The animal has no player settings.")).Master = target.Master; break;
                    case Operations.HusbandryOrder.FollowDrafted: (animal.playerSettings ?? throw new InvalidOperationException("The animal has no player settings.")).followDrafted = intent.Follow; break;
                    case Operations.HusbandryOrder.FollowFieldwork: (animal.playerSettings ?? throw new InvalidOperationException("The animal has no player settings.")).followFieldwork = intent.Follow; break;
                    case Operations.HusbandryOrder.CancelSlaughter:
                    case Operations.HusbandryOrder.CancelRelease:
                        animal.Map.designationManager.RemoveDesignation(animal.Map.designationManager.DesignationOn(animal, designation));
                        break;
                    default:
                        animal.Map.designationManager.AddDesignation(new Designation(animal, designation));
                        break;
                }
                if (!Holds(intent, target)) throw new InvalidOperationException("Native husbandry readback did not apply.");
            }
            Receipts.AnimalEffect effect = intent.Order switch
            {
                Operations.HusbandryOrder.Train => new Receipts.AnimalEffect.TrainingCase(intent.TrainableDef ?? throw new InvalidOperationException("Training has no def."), (animal.training ?? throw new InvalidOperationException("The animal has no training tracker.")).GetWanted(target.Trainable)),
                Operations.HusbandryOrder.Slaughter or Operations.HusbandryOrder.CancelSlaughter => new Receipts.AnimalEffect.SlaughterCase(NativeHusbandryOperations.Designated(animal, DesignationDefOf.Slaughter)),
                Operations.HusbandryOrder.Tame => new Receipts.AnimalEffect.TameCase(NativeHusbandryOperations.Designated(animal, DesignationDefOf.Tame)),
                Operations.HusbandryOrder.Release or Operations.HusbandryOrder.CancelRelease => new Receipts.AnimalEffect.ReleaseCase(NativeHusbandryOperations.Designated(animal, DesignationDefOf.ReleaseAnimalToWild)),
                Operations.HusbandryOrder.Sterilize when NativeHusbandryOperations.Sterilized(animal) => new Receipts.AnimalEffect.SterilizedCase(),
                Operations.HusbandryOrder.Sterilize when NativeHusbandryOperations.SterilizeBill(animal) != null => new Receipts.AnimalEffect.SterilizationQueuedCase(),
                Operations.HusbandryOrder.AllowedArea => new Receipts.AnimalEffect.AreaCase(NativeHusbandryOperations.AreaId(animal)),
                Operations.HusbandryOrder.Master => new Receipts.AnimalEffect.MasterCase(NativeHusbandryOperations.MasterId(animal)),
                Operations.HusbandryOrder.FollowDrafted => new Receipts.AnimalEffect.FollowDraftedCase((animal.playerSettings ?? throw new InvalidOperationException("The animal has no player settings.")).followDrafted),
                Operations.HusbandryOrder.FollowFieldwork => new Receipts.AnimalEffect.FollowFieldworkCase((animal.playerSettings ?? throw new InvalidOperationException("The animal has no player settings.")).followFieldwork),
                _ => throw new InvalidOperationException("Unsupported husbandry order.")
            };
            return new Receipts.EffectEvidence { Effect = effect };
        }
    }
}
