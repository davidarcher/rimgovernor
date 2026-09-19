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
        private bool withdrawn;
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
        internal bool Matches(Operations.CancelAcquisition command, Map current) => current == map
            && command.Source.EntityId == source && command.ResourceDefName == resource
            && command.Cell.X == cell.x && command.Cell.Z == cell.z;
        internal void Withdraw()
        {
            if (prey.Spawned && prey.Map == map)
            {
                var designation = map.designationManager.DesignationOn(prey, DesignationDefOf.Hunt);
                if (designation != null) map.designationManager.RemoveDesignation(designation);
            }
            // Removing a designation alone leaves an already-running Hunt job
            // alive, and its unsafe route can stop every subsequent clock window.
            foreach (var hunter in map.mapPawns.FreeColonistsSpawned.ToList())
                if (hunter.CurJobDef == JobDefOf.Hunt && hunter.CurJob.targetA.Thing == prey)
                    hunter.jobs.EndCurrentJob(JobCondition.InterruptForced);
            withdrawn = true;
        }
        public Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var evidence = Evidence();
            if (withdrawn && !evidence.LaborFinished && !evidence.Designated)
                return new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true,
                    Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                        Evidence = new Receipts.EffectEvidence { Acquisition = evidence }, Detail = "Owned hunt was withdrawn before an observed kill." } };
            return NativeAcquisitionRecord.Progress(attempt, context, evidence);
        }
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
            return verbs.Length > 0 && verbs.All(v => v.defaultProjectile?.thingClass == typeof(Bullet) && v.defaultProjectile.projectile.explosionRadius == 0 && v.defaultProjectile.projectile.damageDef?.workerClass != typeof(DamageWorker_Flame));
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
        // Food prey is wild, non-predatory and edible. Revenge is a policy cost;
        // RouteSafe still rejects hazards and non-ordinary death actions.
        private static bool SafePrey(Pawn prey) => prey.Faction == null && prey.RaceProps.Animal
            && !prey.RaceProps.predator && !prey.InMentalState
            && prey.RaceProps.meatDef?.IsNutritionGivingIngestible == true && prey.RaceProps.corpseDef != null;
        // PestDefinitions are the wild animals hunted for what they destroy,
        // not for meat (#247): an alphabeaver pack defoliates the map and
        // no census otherwise answers it (wild, factionless, not hostile,
        // not a predator). Go recognises the same names in the wild-animal
        // census (policy.PestDefinition), so the two lists move together.
        internal static readonly HashSet<string> PestDefinitions = new HashSet<string>(StringComparer.Ordinal) { "Alphabeaver" };
        // Pest is the pest rule: a wild, living, undowned animal of a pest
        // definition. It waives the docility (manhunterOnDamageChance) and
        // butcher-bill rules of SafePrey: the point is the kill, and the
        // pack turning manhunter on a hit is the emergency census's threat
        // to answer, not a reason to leave the trees to them.
        internal static bool Pest(Pawn prey) => prey.Faction == null && prey.RaceProps.Animal && !prey.InMentalState
            && prey.RaceProps.corpseDef != null && PestDefinitions.Contains(prey.def.defName);
        // Meleeable prey (#260): safe prey no bigger than the hunter, which
        // flees rather than fights back, so a colonist with a melee weapon
        // or bare hands can run it down. This is the wiki's day-one interim
        // food; it never covers a pest or anything the safe-prey rule rejects.
        internal static bool Meleeable(Pawn prey) => SafePrey(prey) && (prey.Downed || prey.RaceProps.manhunterOnDamageChance == 0 && prey.BodySize <= 1.0f);
        private static bool MeleeArmed(Pawn p, Pawn prey) => Meleeable(prey) && (p.equipment?.Primary == null || p.equipment.Primary.def.IsMeleeWeapon);
        // Hunter is the colonist rule: hunting enabled, an ordinary bullet
        // weapon (or a melee weapon or bare hands against meleeable prey),
        // within 100 cells over a safe route. A pest is hunted wherever it
        // is on the map (the pack arrives at the edge and works inward);
        // the route still has to be safe.
        private static bool Hunter(Pawn p, Pawn prey) => !p.Downed && !p.Drafted && !p.InMentalState
            && p.workSettings?.WorkIsActive(WorkTypeDefOf.Hunting) == true && (OrdinaryWeapon(p) || MeleeArmed(p, prey))
            && (Pest(prey) || p.Position.DistanceToSquared(prey.Position) <= 10000) && HuntingSafety.RouteSafe(p, prey);
        // Ineligible names the first hunt rule the prey (or every colonist
        // against it) fails, null when the census would offer it: what the
        // test fixtures report when a staged animal never becomes a row.
        internal static string? Ineligible(Pawn prey)
        {
            if (!prey.Spawned) return "not spawned";
            if (prey.Dead) return "dead";
            if (prey.Position.Fogged(prey.Map)) return "fogged";
            if (!Pest(prey))
            {
                if (!SafePrey(prey)) return "not safe prey";
                if (!ButcherReady(prey)) return "no butcher bill";
            }
            var colonists = prey.Map.mapPawns.FreeColonistsSpawned.ToList();
            if (colonists.Count == 0) return "no colonist";
            var reasons = colonists.Select(p =>
                p.Downed ? "downed" : p.Drafted ? "drafted" : p.InMentalState ? "mental state"
                : p.workSettings?.WorkIsActive(WorkTypeDefOf.Hunting) != true ? "hunting inactive"
                : !OrdinaryWeapon(p) && !MeleeArmed(p, prey) ? "no ordinary ranged weapon (" + (p.equipment?.Primary?.def.defName ?? "unarmed") + ")" + (Meleeable(prey) ? "" : " and the prey is not meleeable")
                : !(Pest(prey) || p.Position.DistanceToSquared(prey.Position) <= 10000) ? "too far"
                : !HuntingSafety.RouteSafe(p, prey) ? "no safe route" : (string?)null).ToList();
            if (reasons.Any(r => r == null)) return null;
            return "no hunter: " + string.Join("; ", colonists.Zip(reasons, (p, r) => p.GetUniqueLoadID() + " " + r));
        }
        private static bool Eligible(Pawn prey)
        {
            if (!prey.Spawned || prey.Dead || prey.Position.Fogged(prey.Map)) return false;
            if (!Pest(prey) && (!SafePrey(prey) || !ButcherReady(prey))) return false;
            return prey.Map.mapPawns.FreeColonistsSpawned.Any(p => Hunter(p, prey));
        }
        private static double Nutrition(Pawn prey) => Math.Max(0, prey.GetStatValue(StatDefOf.MeatAmount)) * prey.RaceProps.meatDef.GetStatValueAbstract(StatDefOf.Nutrition);
        private static Obs.SnapshotRef Snapshot(Pawn prey, Common.ObservationContext context) => new Obs.SnapshotRef {
            Context = context.Clone(), EntityId = prey.GetUniqueLoadID(), Token = NativePlantAcquisition.Token(context.Identity,
                prey.GetUniqueLoadID(), prey.RaceProps.corpseDef.defName, prey.Position.x, prey.Position.z,
                prey.health.summaryHealth.SummaryHealthPercent, 1, Designated(prey)) };
        internal static void Read(Obs.ColonyFactsSnapshot result, Map map, IntVec3 center, int limit)
        {
            result.PendingHunts = (uint)Pending(map);
            // Food prey within 100 cells of the colony (the hunter rule's own
            // reach; tribal8's game grazes 60-100 cells out, #260), then every pest on
            // the map: a pest row is a hunt of one unit of nothing edible
            // (food false, no nutrition), so the food and wood selections
            // pass it over and only the pest goal takes it.
            var candidates = map.mapPawns.AllPawnsSpawned.Where(p => !Pest(p) && p.Position.DistanceTo(center) <= 100 && Eligible(p))
                .OrderByDescending(p => p.BodySize / (1 + p.Position.DistanceTo(center) / 25)).ThenBy(p => p.thingIDNumber)
                .Concat(map.mapPawns.AllPawnsSpawned.Where(p => Pest(p) && Eligible(p)).OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber))
                .Take(Math.Max(0, limit - result.Acquisition.Count));
            foreach (var prey in candidates) result.Acquisition.Add(new Obs.AcquisitionFacts {
                Source = new Obs.EntityRef { Id = prey.GetUniqueLoadID(), DefName = prey.def.defName, MapId = map.uniqueID,
                    Position = new Common.Cell { X = prey.Position.x, Z = prey.Position.z }, Snapshot = Snapshot(prey, result.Context) },
                Resource = prey.RaceProps.corpseDef.defName, Tree = false, Food = !Pest(prey), Hunt = true,
                Yield = 1, NutritionYield = Pest(prey) ? 0 : Nutrition(prey), Designated = Designated(prey),
                RevengeChance = prey.RaceProps.manhunterOnDamageChance,
                HerdSize = (uint)map.mapPawns.AllPawnsSpawned.Count(p => !p.Dead && p.def == prey.def && p.Position.DistanceToSquared(prey.Position) <= 625),
                MeleeOnly = Meleeable(prey), Downed = prey.Downed,
                WeaponRange = map.mapPawns.FreeColonistsSpawned.Where(p => Hunter(p, prey) && OrdinaryWeapon(p))
                    .SelectMany(p => p.equipment.Primary.def.Verbs).Where(v => !v.IsMeleeAttack && v.ai_IsWeapon)
                    .Select(v => (double)v.range).DefaultIfEmpty(0).Max() });
            result.PendingFoodNutrition += map.mapPawns.AllPawnsSpawned.Where(p => Designated(p) && p.RaceProps.meatDef != null).Sum(Nutrition);
        }
        internal const string Kind = "Hunt";
        // A withdrawal may only use this controller action's preceding hunt
        // record. The captured admission cell stays valid after the prey moves.
        internal static NativeHuntRecord? WithdrawalRecord(NativeOperationState state, Common.AttemptKey attempt)
        {
            if (attempt.AttemptId <= 1) return null;
            var prior = attempt.Clone(); prior.AttemptId--;
            return state.Acquisition.TryGetValue(prior, out var record) ? record as NativeHuntRecord : null;
        }
        internal static Operations.ExecuteReply Cancel(NativeOperationState state, Operations.ExecuteRequest request,
            Common.ObservationContext context, NativeHuntRecord record)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.CancelAcquisition;
            try
            {
                if (!NativePlantAcquisition.ValidCancel(command) || !record.Matches(command, ProtoBoundary.LoadedMap(context)))
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cancellation requires the original hunt target.") };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                state.Acquisition.Add(pre.Attempt.Clone(), record);
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success) throw new InvalidOperationException("Authority moved before hunt cancellation.");
                    record.Withdraw();
                    evidence = new Receipts.EffectEvidence { Acquisition = record.Evidence() };
                    if (evidence.Acquisition.Designated) throw new InvalidOperationException("Hunt designation survived cancellation.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Hunt cancellation failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted hunt cancellation requires observation: " + error.GetType().Name) };
            }
        }
        // Prepare is the apply-time precondition list for hunt
        // (action-contracts.md): Eligible plus the request's cell, resource
        // and designation rules, one rule at a time.
        private static bool Prepare(Operations.AcquireResource command, Common.ObservationContext context, out Pawn? prey, out Common.Failure failure)
        {
            prey = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Hunting requires an exact safe prey (or pest) snapshot, enabled hunter, butcher bill (food prey) and fewer than two outstanding hunts.");
            if (!NativePlantAcquisition.Valid(command)) return false;
            var map = ProtoBoundary.LoadedMap(context);
            var found = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Source.EntityId);
            var rules = new ApplyPreconditions(Kind)
                .Require(() => Pending(map) < 2, "two hunts are already outstanding on this map")
                .Require(() => !map.AllCells.Any(c => map.roofCollapseBuffer.IsMarkedToCollapse(c)), "a roof collapse is pending on this map")
                .Present(() => found != null && found.Spawned, "the exact animal is no longer spawned on this map")
                .Require(() => !found!.Dead, "the animal is dead")
                .Require(() => Pest(found!) || SafePrey(found!), "the animal is neither safe wild prey nor a recognised pest")
                .Require(() => found!.Position.x == command.Cell.X && found.Position.z == command.Cell.Z, "the animal is not at the expected cell")
                .Require(() => found!.RaceProps.corpseDef.defName == command.ResourceDefName, "the animal's corpse is not the expected resource")
                .Require(() => !found!.Position.Fogged(map), "the animal's cell is fogged")
                .Require(() => !Designated(found!), "the animal is already designated for hunting")
                .Require(() => new Designator_Hunt().CanDesignateThing(found!).Accepted, "the native hunt designator refuses the animal")
                .Require(() => Pest(found!) || ButcherReady(found!), "no usable butcher bill with an assigned cook accepts the corpse")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Hunter(p, found!)), "no free colonist with hunting enabled and an ordinary ranged weapon (or a melee weapon or bare hands against meleeable prey) has a safe route to the animal")
                .Token(NativeDraftProtocol.TokenSent(command.Source), () => Snapshot(found!, context).Token == command.Source.ExpectedSnapshotToken, "the animal snapshot changed since it was read");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            prey = found;
            return true;
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
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
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
                if (handle != null) return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Hunting write interrupted: " + error.GetType().Name) };
                return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Hunting failed: " + error.GetType().Name) };
            }
        }
    }
}
