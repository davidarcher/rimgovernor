#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // MaintainHerd's animal facts and eligibility rules, shared by the
    // husbandry census (NativeHusbandryObservation) and HusbandryIntent.
    // Master and following are refused unless Obedience is learned, because
    // RimWorld's own Master setter Log.ErrorOnce's (pausing the colony)
    // otherwise.
    internal static class NativeHusbandryOperations
    {
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        internal static bool Designated(Pawn animal, DesignationDef def) => animal.Map.designationManager.DesignationOn(animal, def) != null;

        // Settings token: exact designation/training state; the before and
        // after evidence of an applied husbandry order.
        internal static string Settings(Pawn animal)
        {
            var values = new List<string> { animal.GetUniqueLoadID(),
                Designated(animal, DesignationDefOf.Slaughter).ToString(),
                Designated(animal, DesignationDefOf.ReleaseAnimalToWild).ToString(),
                Designated(animal, DesignationDefOf.Tame).ToString(),
                AreaId(animal), MasterId(animal),
                (animal.playerSettings?.followDrafted ?? false).ToString(),
                (animal.playerSettings?.followFieldwork ?? false).ToString(),
                Sterilized(animal).ToString(), (SterilizeBill(animal) != null).ToString() };
            if (animal.training != null)
                values.AddRange(DefDatabase<TrainableDef>.AllDefsListForReading.OrderBy(t => t.defName, StringComparer.Ordinal)
                    .Select(t => t.defName + "=" + animal.training.GetWanted(t)));
            return "husbandry-" + Hash(string.Join("|", values));
        }

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

        internal static bool Eligible(Pawn? animal) => Observable(animal) && animal!.Faction == Faction.OfPlayer;

        // A factionless animal on the current map, the only kind the tame
        // designator can target.
        internal static bool EligibleWild(Pawn? animal) => Observable(animal) && animal!.Faction == null;

        // Native tame eligibility: the designator's own acceptance plus no
        // standing tame or hunt designation.
        internal static bool Tameable(Pawn animal) => EligibleWild(animal) && TameUtility.CanTame(animal)
            && !Designated(animal, DesignationDefOf.Tame) && !Designated(animal, DesignationDefOf.Hunt)
            && new Designator_Tame().CanDesignateThing(animal).Accepted;

        // Herd sizing facts (#875) MaintainHerd reads from the colony census:
        // age against RaceProps.lifeExpectancy, sickness, adulthood, tame
        // danger, and whether the player's primary ideo venerates the race.
        // What precepts make of slaughter and eating comes from the catalog's
        // precept effects (policy.ActionStance), not from here.
        internal static void HerdFacts(Pawn animal, RimGovernor.Protocol.Observations.AnimalState state)
        {
            state.AgeYears = animal.ageTracker.AgeBiologicalYearsFloat;
            state.Adult = animal.ageTracker.Adult;
            state.Gender = animal.gender.ToString();
            if (animal.Faction != Faction.OfPlayer) return;
            state.Sick = animal.health.hediffSet.AnyHediffMakesSickThought;
            state.Sterilized = Sterilized(animal);
            state.SterilizeQueued = SterilizeBill(animal) != null;
            var ideo = ModsConfig.IdeologyActive ? Faction.OfPlayer.ideos?.PrimaryIdeo : null;
            state.Venerated = ideo != null && ideo.IsVeneratedAnimal(animal);
        }

        // Raw slaughter facts; policy decides which animals are protected.
        internal static void SlaughterFacts(Pawn animal, RimGovernor.Protocol.Observations.AnimalState state)
        {
            state.Downed = animal.Downed;
            state.InMentalState = animal.InMentalState;
            state.Pregnant = animal.health.hediffSet.hediffs.OfType<Hediff_Pregnant>().Any();
            state.ColonistBonded = TrainableUtility.GetAllColonistBondsFor(animal).Any();
            state.SlaughterDesignatable = new Designator_Slaughter().CanDesignateThing(animal).Accepted;
        }

        // The release designator's own acceptance plus the bonded/master
        // and pregnancy exclusions slaughter applies: release is non-lethal but still
        // removes the animal from the colony.
        internal static bool SafeToRelease(Pawn animal) => !animal.Dead && !animal.Downed && !animal.InMentalState
            && animal.Faction == Faction.OfPlayer && animal.RaceProps.canReleaseToWild
            && animal.playerSettings != null && animal.playerSettings.Master == null
            && !TrainableUtility.GetAllColonistBondsFor(animal).Any()
            && !animal.health.hediffSet.hediffs.OfType<Hediff_Pregnant>().Any()
            && !Designated(animal, DesignationDefOf.Slaughter) && !Designated(animal, DesignationDefOf.ReleaseAnimalToWild)
            && new Designator_ReleaseAnimalToWild().CanDesignateThing(animal).Accepted;

        // Current Animals-tab settings, always the real GetUniqueLoadID()
        // identities NativeHusbandryObservation publishes ("" when unset).
        internal static string AreaId(Pawn animal) => animal.playerSettings?.AreaRestrictionInPawnCurrentMap?.GetUniqueLoadID() ?? "";
        internal static string MasterId(Pawn animal) => animal.playerSettings?.Master?.GetUniqueLoadID() ?? "";
        internal static bool Obedient(Pawn animal) => animal.training != null && animal.training.HasLearned(TrainableDefOf.Obedience);
        internal static bool SupportsAllowedAreas(Pawn animal) => animal.playerSettings != null && animal.playerSettings.SupportsAllowedAreas && animal.MapHeld != null;

        internal static Area_Allowed? ResolveArea(Pawn animal, string entityId) => animal.Map?.areaManager.AllAreas.OfType<Area_Allowed>()
            .FirstOrDefault(a => RefIndex.Is(a, entityId) || a.ID.ToString(System.Globalization.CultureInfo.InvariantCulture) == entityId);
        internal static Pawn? ResolveMaster(Pawn animal, string entityId) => animal.Map?.mapPawns.FreeColonistsSpawned
            .ById(entityId) is Pawn p && !p.Dead ? p : null;
    }

    // HusbandryIntent (#941): one animal's training request, slaughter, tame
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
            if (intent == null || !intent.HasAnimalId || !ProtoBoundary.IsIdentifier(intent.AnimalId) || !intent.HasOrder) return false;
            switch (intent.Order)
            {
                case Operations.HusbandryOrder.Train: return intent.HasTrainableDef && ProtoBoundary.IsIdentifier(intent.TrainableDef);
                case Operations.HusbandryOrder.AllowedArea:
                case Operations.HusbandryOrder.Master: return !intent.HasTargetId || ProtoBoundary.IsIdentifier(intent.TargetId);
                case Operations.HusbandryOrder.FollowDrafted:
                case Operations.HusbandryOrder.FollowFieldwork: return intent.HasFollow;
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
            if (!Valid(intent)) return target;
            target.Animal = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.ById(intent!.AnimalId);
            if (!NativeHusbandryOperations.Observable(target.Animal)) { target.Animal = null; return target; }
            if (intent!.Order == Operations.HusbandryOrder.Train) target.Trainable = DefDatabase<TrainableDef>.GetNamedSilentFail(intent.TrainableDef);
            if (intent.Order == Operations.HusbandryOrder.AllowedArea && intent.HasTargetId) target.Area = NativeHusbandryOperations.ResolveArea(target.Animal!, intent.TargetId);
            if (intent.Order == Operations.HusbandryOrder.Master && intent.HasTargetId) target.Master = NativeHusbandryOperations.ResolveMaster(target.Animal!, intent.TargetId);
            return target;
        }

        // Whether the animal already holds what the order asks for. A tamed
        // animal holds its tame order: the designation is consumed when the
        // taming job brings it into the colony.
        private static bool Holds(Operations.HusbandryIntent intent, Target target)
        {
            var animal = target.Animal!;
            switch (intent.Order)
            {
                case Operations.HusbandryOrder.Train: return target.Trainable != null && animal.training != null && animal.training.GetWanted(target.Trainable);
                case Operations.HusbandryOrder.Tame: return animal.Faction == Faction.OfPlayer || NativeHusbandryOperations.Designated(animal, DesignationDefOf.Tame);
                // Queued, or already done: the bill is gone once the surgery has run.
                case Operations.HusbandryOrder.Sterilize: return animal.Faction == Faction.OfPlayer
                    && (NativeHusbandryOperations.Sterilized(animal) || NativeHusbandryOperations.SterilizeBill(animal) != null);
                case Operations.HusbandryOrder.Slaughter:
                case Operations.HusbandryOrder.Release: return animal.Faction == Faction.OfPlayer && NativeHusbandryOperations.Designated(animal, DesignationFor(intent.Order)!);
                case Operations.HusbandryOrder.CancelSlaughter:
                case Operations.HusbandryOrder.CancelRelease: return animal.Faction == Faction.OfPlayer && !NativeHusbandryOperations.Designated(animal, DesignationFor(intent.Order)!);
                case Operations.HusbandryOrder.AllowedArea:
                    return animal.Faction == Faction.OfPlayer && (intent.HasTargetId
                        ? target.Area != null && NativeHusbandryOperations.AreaId(animal) == target.Area.GetUniqueLoadID()
                        : NativeHusbandryOperations.AreaId(animal) == "");
                case Operations.HusbandryOrder.Master:
                    return animal.Faction == Faction.OfPlayer && (intent.HasTargetId
                        ? target.Master != null && NativeHusbandryOperations.MasterId(animal) == target.Master.GetUniqueLoadID()
                        : NativeHusbandryOperations.MasterId(animal) == "");
                case Operations.HusbandryOrder.FollowDrafted: return animal.Faction == Faction.OfPlayer && animal.playerSettings != null && animal.playerSettings.followDrafted == intent.Follow;
                case Operations.HusbandryOrder.FollowFieldwork: return animal.Faction == Faction.OfPlayer && animal.playerSettings != null && animal.playerSettings.followFieldwork == intent.Follow;
                default: return false;
            }
        }

        private static bool Standing(Operations.HusbandryIntent? intent, Target target) => Valid(intent) && target.Animal != null && Holds(intent!, target);

        private static ApplyPreconditions Rules(Operations.HusbandryIntent? intent, Target target)
        {
            var order = intent?.Order ?? Operations.HusbandryOrder.Unspecified;
            var animal = target.Animal!;
            bool tame = order == Operations.HusbandryOrder.Tame;
            var rules = new ApplyPreconditions(Kind)
                .Require(() => Valid(intent), "order requires an animal and the order's argument")
                .Present(() => target.Animal != null && (tame ? animal.Faction == null : animal.Faction == Faction.OfPlayer),
                    "animal " + intent?.AnimalId + " is not " + (tame ? "a wild" : "a player") + " animal on the map");
            switch (order)
            {
                case Operations.HusbandryOrder.Train:
                    rules.Require(() => target.Trainable != null && animal.training != null && animal.training.CanAssignToTrain(target.Trainable).Accepted,
                            "animal cannot train " + intent!.TrainableDef)
                        .Require(() => !NativeHusbandryOperations.Designated(animal, DesignationDefOf.Slaughter) && !NativeHusbandryOperations.Designated(animal, DesignationDefOf.ReleaseAnimalToWild),
                            "animal is designated for removal");
                    break;
                case Operations.HusbandryOrder.Slaughter:
                    rules.Require(() => !animal.Dead && new Designator_Slaughter().CanDesignateThing(animal).Accepted, "native slaughter designator refused the animal");
                    break;
                case Operations.HusbandryOrder.Release:
                    rules.Require(() => NativeHusbandryOperations.SafeToRelease(animal), "animal is protected or native release eligibility refused it");
                    break;
                case Operations.HusbandryOrder.Tame:
                    rules.Require(() => NativeHusbandryOperations.Tameable(animal), "native tame eligibility refused the animal or it is designated for hunting");
                    break;
                case Operations.HusbandryOrder.Sterilize:
                    rules.Require(() => NativeHusbandryOperations.SterilizeRecipe(animal) != null,
                            "race " + animal.def.defName + " offers no whole-body sterilize recipe (no recipe on its race adds the Sterilized hediff)")
                        .Require(() => { var r = NativeHusbandryOperations.SterilizeRecipe(animal)!; return r.AvailableNow && r.Worker.AvailableReport(animal).Accepted && r.AvailableOnNow(animal, null); },
                            "sterilize recipe is not available on this animal now");
                    break;
                case Operations.HusbandryOrder.AllowedArea:
                    rules.Require(() => NativeHusbandryOperations.SupportsAllowedAreas(animal), "animal cannot carry an allowed area")
                        .Present(() => !intent!.HasTargetId || target.Area != null, "allowed area " + intent!.TargetId + " is not on the animal's map")
                        .Require(() => NativeWorkSettings.AreaSafeAndReachable(animal, target.Area), "allowed area must preserve hazard protection and native reachability");
                    break;
                case Operations.HusbandryOrder.Master:
                    rules.Require(() => NativeHusbandryOperations.Obedient(animal), "a master requires learned Obedience")
                        .Present(() => !intent!.HasTargetId || target.Master != null, "master " + intent!.TargetId + " is not a spawned free colonist on the animal's map");
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
            var target = Resolve(action.Husbandry, context);
            if (Standing(action.Husbandry, target)) return null;
            var rules = Rules(action.Husbandry, target);
            return rules.Holds ? null : rules.Failure();
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.Husbandry;
            var target = Resolve(intent, context);
            if (!Standing(intent, target))
            {
                var rules = Rules(intent, target);
                if (!rules.Holds) throw new InvalidOperationException("Husbandry prerequisites changed before apply: " + rules.Reason);
            }
            var animal = target.Animal!;
            var before = NativeHusbandryOperations.Settings(animal);
            if (!Holds(intent, target))
            {
                var designation = DesignationFor(intent.Order);
                switch (intent.Order)
                {
                    case Operations.HusbandryOrder.Train: animal.training!.SetWantedRecursive(target.Trainable, true); break;
                    case Operations.HusbandryOrder.Sterilize: HealthCardUtility.CreateSurgeryBill(animal, NativeHusbandryOperations.SterilizeRecipe(animal)!, null); break;
                    case Operations.HusbandryOrder.AllowedArea: animal.playerSettings!.AreaRestrictionInPawnCurrentMap = target.Area; break;
                    case Operations.HusbandryOrder.Master: animal.playerSettings!.Master = target.Master; break;
                    case Operations.HusbandryOrder.FollowDrafted: animal.playerSettings!.followDrafted = intent.Follow; break;
                    case Operations.HusbandryOrder.FollowFieldwork: animal.playerSettings!.followFieldwork = intent.Follow; break;
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
            var effect = new Receipts.AnimalEffect { Animal = new Receipts.SnapshotEvidence {
                EntityId = animal.GetUniqueLoadID(), BeforeToken = before, AfterToken = NativeHusbandryOperations.Settings(animal) } };
            switch (intent.Order)
            {
                case Operations.HusbandryOrder.Train: effect.TrainableDef = intent.TrainableDef; effect.Wanted = animal.training!.GetWanted(target.Trainable); break;
                case Operations.HusbandryOrder.Slaughter:
                case Operations.HusbandryOrder.CancelSlaughter: effect.SlaughterDesignated = NativeHusbandryOperations.Designated(animal, DesignationDefOf.Slaughter); break;
                case Operations.HusbandryOrder.Tame: effect.TameDesignated = NativeHusbandryOperations.Designated(animal, DesignationDefOf.Tame); break;
                case Operations.HusbandryOrder.Sterilize:
                    effect.Sterilized = NativeHusbandryOperations.Sterilized(animal);
                    effect.SterilizeQueued = NativeHusbandryOperations.SterilizeBill(animal) != null;
                    break;
                case Operations.HusbandryOrder.Release:
                case Operations.HusbandryOrder.CancelRelease: effect.ReleaseDesignated = NativeHusbandryOperations.Designated(animal, DesignationDefOf.ReleaseAnimalToWild); break;
                case Operations.HusbandryOrder.AllowedArea: effect.AllowedAreaId = NativeHusbandryOperations.AreaId(animal); break;
                case Operations.HusbandryOrder.Master: effect.MasterId = NativeHusbandryOperations.MasterId(animal); break;
                case Operations.HusbandryOrder.FollowDrafted: effect.FollowDrafted = animal.playerSettings!.followDrafted; break;
                case Operations.HusbandryOrder.FollowFieldwork: effect.FollowFieldwork = animal.playerSettings!.followFieldwork; break;
            }
            return new Receipts.EffectEvidence { Animal = effect };
        }
    }
}
