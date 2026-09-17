#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeHuntRecord : INativeAcquisitionRecord
    {
        private readonly Pawn prey;
        private readonly Map map;
        private readonly IntVec3 cell;
        private readonly string source, resource;
        internal NativeHuntRecord(Pawn prey)
        { this.prey = prey; map = prey.Map; cell = prey.Position; source = prey.GetUniqueLoadID(); resource = prey.RaceProps.corpseDef.defName; }
        internal Receipts.AcquisitionEffect Evidence()
        {
            var corpse = prey.Corpse;
            var exact = prey.Dead && corpse != null && ReferenceEquals(corpse.InnerPawn, prey) && corpse.def.defName == resource;
            var observed = exact && !corpse!.Destroyed && corpse.Spawned && corpse.Map == map
                && !corpse.IsForbidden(Faction.OfPlayer) && !corpse.Position.Fogged(map)
                && corpse.GetRotStage() == RotStage.Fresh;
            var result = new Receipts.AcquisitionEffect { SourceId = source, ResourceDef = resource,
                Cell = new Common.Cell { X = cell.x, Z = cell.z }, Designated = NativeHuntAcquisition.Designated(prey),
                LaborFinished = exact, ProducedUnits = exact ? 1 : 0, OutputComplete = exact, OutputObserved = observed };
            if (exact) result.Outputs.Add(new Receipts.AcquisitionOutput { ThingId = corpse!.GetUniqueLoadID(), Units = 1 });
            return result;
        }
        public Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
            => NativeAcquisitionRecord.Progress(attempt, context, Evidence());
    }

    // A corpse is acquired material. Only later ordinary butchering/cooking can
    // produce edible stock; expected meat nutrition never populates this receipt.
    internal static class NativeHuntAcquisition
    {
        internal static bool Designated(Pawn prey) => prey.Spawned && prey.Map.designationManager.DesignationOn(prey, DesignationDefOf.Hunt) != null;
        internal static bool IsHunt(Operations.AcquireResource command, Common.ObservationContext context) => ProtoBoundary.ResolveMap(context)?.mapPawns.AllPawnsSpawned.Any(p => p.GetUniqueLoadID() == command.Source?.EntityId) == true;
        private static int Pending(Map map) => map.mapPawns.AllPawnsSpawned.Count(Designated);
        private static bool OrdinaryWeapon(Pawn pawn)
        {
            var weapon = pawn.equipment?.Primary;
            if (weapon?.def.IsRangedWeapon != true) return false;
            return OrdinaryVerbs(weapon.def.Verbs);
        }
        internal static bool OrdinaryVerbs(IEnumerable<VerbProperties> definitions)
        {
            var verbs = definitions.Where(v => !v.IsMeleeAttack && v.ai_IsWeapon).ToArray();
            return verbs.Length > 0 && verbs.All(v => v.defaultProjectile?.thingClass == typeof(Bullet) && v.defaultProjectile.projectile.explosionRadius == 0);
        }
        private static bool ButcherReady(Pawn prey) => prey.Map.listerThings.AllThings.OfType<Building>().Any(b =>
            b is IBillGiver giver && !b.IsForbidden(Faction.OfPlayer) && giver.CurrentlyUsableForBills()
            && giver.BillStack.Bills.OfType<Bill_Production>().Any(bill => bill.recipe.defName == "ButcherCorpseFlesh"
                && !bill.suspended && !bill.paused && bill.ingredientFilter.Allows(prey.RaceProps.corpseDef)
                && (bill.repeatMode == BillRepeatModeDefOf.Forever
                    || bill.repeatMode == BillRepeatModeDefOf.RepeatCount && bill.repeatCount > 0
                    || bill.repeatMode == BillRepeatModeDefOf.TargetCount && BillCommon.ProductCount(bill) is int count && count < bill.targetCount))
            && prey.Map.mapPawns.FreeColonistsSpawned.Any(p => !p.Downed && !p.Drafted && !p.InMentalState
                && DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking") is WorkTypeDef cooking && p.workSettings?.WorkIsActive(cooking) == true && p.CanReach(b, PathEndMode.InteractionCell, Danger.None)));
        private static bool Eligible(Pawn prey)
        {
            if (!prey.Spawned || prey.Dead || prey.Downed || prey.Faction != null || !prey.RaceProps.Animal
                || prey.RaceProps.predator || prey.RaceProps.manhunterOnDamageChance != 0 || prey.InMentalState
                || prey.RaceProps.meatDef?.IsNutritionGivingIngestible != true || prey.RaceProps.corpseDef == null
                || prey.Position.Fogged(prey.Map) || !ButcherReady(prey)) return false;
            return prey.Map.mapPawns.FreeColonistsSpawned.Any(p => !p.Downed && !p.Drafted && !p.InMentalState
                && p.workSettings?.WorkIsActive(WorkTypeDefOf.Hunting) == true && OrdinaryWeapon(p)
                && p.Position.DistanceToSquared(prey.Position) <= 10000 && HuntingSafety.RouteSafe(p, prey));
        }
        private static double Nutrition(Pawn prey) => Math.Max(0, prey.GetStatValue(StatDefOf.MeatAmount)) * prey.RaceProps.meatDef.GetStatValueAbstract(StatDefOf.Nutrition);
        private static Obs.SnapshotRef Snapshot(Pawn prey, Common.ObservationContext context) => new Obs.SnapshotRef {
            Context = context.Clone(), EntityId = prey.GetUniqueLoadID(), Token = NativePlantAcquisition.Token(context.Identity,
                prey.GetUniqueLoadID(), prey.RaceProps.corpseDef.defName, prey.Position.x, prey.Position.z,
                prey.health.summaryHealth.SummaryHealthPercent, 1, Designated(prey)) };
        internal static void Read(Obs.ColonyFactsSnapshot result, Map map, IntVec3 center, int limit)
        {
            result.PendingHunts = (uint)Pending(map);
            var candidates = map.mapPawns.AllPawnsSpawned.Where(p => p.Position.DistanceTo(center) <= 50 && Eligible(p))
                .OrderByDescending(p => p.BodySize / (1 + p.Position.DistanceTo(center) / 25)).ThenBy(p => p.thingIDNumber)
                .Take(Math.Max(0, limit - result.Acquisition.Count));
            foreach (var prey in candidates) result.Acquisition.Add(new Obs.AcquisitionFacts {
                Source = new Obs.EntityRef { Id = prey.GetUniqueLoadID(), DefName = prey.def.defName, MapId = map.uniqueID,
                    Position = new Common.Cell { X = prey.Position.x, Z = prey.Position.z }, Snapshot = Snapshot(prey, result.Context) },
                Resource = prey.RaceProps.corpseDef.defName, Tree = false, Food = true, Hunt = true,
                Yield = 1, NutritionYield = Nutrition(prey), Designated = Designated(prey) });
            result.PendingFoodNutrition += map.listerThings.AllThings.OfType<Corpse>().Where(c => c.GetRotStage() == RotStage.Fresh
                && c.InnerPawn.RaceProps.Animal && c.InnerPawn.RaceProps.meatDef?.IsNutritionGivingIngestible == true).Sum(c => Nutrition(c.InnerPawn));
            result.PendingFoodNutrition += map.mapPawns.AllPawnsSpawned.Where(p => Designated(p) && p.RaceProps.meatDef != null).Sum(Nutrition);
        }
        private static bool Prepare(Operations.AcquireResource command, Common.ObservationContext context, out Pawn? prey, out Common.Failure failure)
        {
            prey = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Hunting requires an exact safe prey snapshot, enabled hunter, butcher bill and fewer than two outstanding hunts.");
            if (!NativePlantAcquisition.Valid(command)) return false;
            var map = ProtoBoundary.ResolveMap(context);
            if (Pending(map) >= 2 || map.AllCells.Any(c => map.roofCollapseBuffer.IsMarkedToCollapse(c))) return false;
            prey = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Source.EntityId);
            return prey != null && Eligible(prey) && !Designated(prey) && prey.Position.x == command.Cell.X && prey.Position.z == command.Cell.Z
                && prey.RaceProps.corpseDef.defName == command.ResourceDefName && Snapshot(prey, context).Token == command.Source.ExpectedSnapshotToken
                && new Designator_Hunt().CanDesignateThing(prey).Accepted;
        }
        internal static Operations.PreviewReply Preview(Operations.AcquireResource command, Common.ObservationContext context)
        {
            if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
            return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
        }
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.AcquireResource;
            try
            {
                if (!Prepare(command, context, out var prey, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.Reply!;
                handle = admitted.Handle;
                var record = new NativeHuntRecord(prey!); state.Acquisition.Add(pre.Attempt.Clone(), record);
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, out var checkedPrey, out failure) || !ReferenceEquals(prey, checkedPrey)) throw new InvalidOperationException("Hunting changed before designation.");
                    new Designator_Hunt().DesignateThing(prey);
                    evidence = new Receipts.EffectEvidence { Acquisition = record.Evidence() };
                    if (!evidence.Acquisition.Designated) throw new InvalidOperationException("Hunt designation was not observed.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                if (handle != null) return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Hunting write interrupted: " + error.GetType().Name) };
                return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Hunting failed: " + error.GetType().Name) };
            }
        }
    }
}
