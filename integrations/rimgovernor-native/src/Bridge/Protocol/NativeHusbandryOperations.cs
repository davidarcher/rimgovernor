#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed dispatch for MaintainHerd-*'s two direct-write animal management
    // orders: recursive training request (SetAnimalTraining) and slaughter
    // designation (SlaughterAnimal). Ports the legacy JSON
    // home/husbandry_config tool's (HusbandryTools.Configure) eligibility
    // checks behind the typed boundary. Unlike job-issuing verticals
    // (haul/recover/relieve), this is an immediate settings write with no
    // native job -- the same shape NativeWorkSettings uses for work
    // priorities.
    internal sealed class NativeHusbandryRecord
    {
        internal readonly string AnimalId;
        internal readonly bool Slaughter;
        internal readonly string? TrainableDef;
        internal NativeHusbandryRecord(string animalId, bool slaughter, string? trainableDef)
        { AnimalId = animalId; Slaughter = slaughter; TrainableDef = trainableDef; }
    }

    internal static class NativeHusbandryOperations
    {
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        private static bool Designated(Pawn animal, DesignationDef def) => Find.CurrentMap.designationManager.DesignationOn(animal, def) != null;

        // Mirrors HusbandryTools.Census: same-species player animal identity,
        // gender and fertility, so a population change invalidates a stale request.
        internal static string Census(Pawn animal)
        {
            var others = Find.CurrentMap.mapPawns.AllPawnsSpawned
                .Where(a => a.def == animal.def && a.Faction == Faction.OfPlayer && !a.Dead)
                .Select(a => a.GetUniqueLoadID() + ":" + a.gender + ":" + a.ageTracker.CurLifeStage.reproductive + ":" + a.Sterile())
                .OrderBy(id => id, StringComparer.Ordinal);
            return "census-" + Hash(string.Join(",", others));
        }

        // Mirrors HusbandryTools.Settings: exact designation/training state,
        // so a settings change (including one this same order just made)
        // invalidates a stale expected_snapshot_token.
        internal static string Settings(Pawn animal)
        {
            var values = new List<string> { animal.GetUniqueLoadID(),
                Designated(animal, DesignationDefOf.Slaughter).ToString(),
                Designated(animal, DesignationDefOf.ReleaseAnimalToWild).ToString() };
            if (animal.training != null)
                values.AddRange(DefDatabase<TrainableDef>.AllDefsListForReading.OrderBy(t => t.defName, StringComparer.Ordinal)
                    .Select(t => t.defName + "=" + animal.training.GetWanted(t)));
            return "husbandry-" + Hash(string.Join("|", values));
        }

        internal static bool Eligible(Pawn? animal) => animal != null && !animal.Destroyed && animal.Spawned
            && animal.Map == Find.CurrentMap && animal.RaceProps.Animal && animal.Faction == Faction.OfPlayer
            && !animal.Dead && !animal.Position.Fogged(animal.Map);

        // Mirrors HusbandryTools.SafeToSlaughter.
        internal static bool SafeToSlaughter(Pawn animal) => !animal.Dead && !animal.Downed && !animal.InMentalState
            && animal.Faction == Faction.OfPlayer && animal.playerSettings != null && animal.playerSettings.Master == null
            && !TrainableUtility.GetAllColonistBondsFor(animal).Any()
            && !animal.health.hediffSet.hediffs.OfType<Hediff_Pregnant>().Any()
            && !Designated(animal, DesignationDefOf.ReleaseAnimalToWild)
            && new Designator_Slaughter().CanDesignateThing(animal).Accepted;

        private static bool ValidTraining(Operations.SetAnimalTraining? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Animal) && command.HasExpectedCensusToken && ProtoBoundary.IsIdentifier(command.ExpectedCensusToken)
            && command.HasTrainableDef && ProtoBoundary.IsIdentifier(command.TrainableDef);
        private static bool ValidSlaughter(Operations.SlaughterAnimal? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Animal) && command.HasExpectedCensusToken && ProtoBoundary.IsIdentifier(command.ExpectedCensusToken);

        private static bool PrepareTraining(Operations.SetAnimalTraining command, out Pawn? animal, out TrainableDef? trainable, out Common.Failure failure)
        {
            animal = null; trainable = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Training requires an exact current animal settings/census snapshot and a native TrainableDef.");
            if (!ValidTraining(command)) return false;
            animal = Find.CurrentMap.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Animal.EntityId);
            if (animal == null || !Eligible(animal)) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact eligible player animal is unavailable."); return false; }
            if (Settings(animal) != command.Animal.ExpectedSnapshotToken || Census(animal) != command.ExpectedCensusToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Animal settings or census changed; observe before new admission."); return false; }
            trainable = DefDatabase<TrainableDef>.GetNamedSilentFail(command.TrainableDef);
            if (trainable == null || animal.training == null || !animal.training.CanAssignToTrain(trainable).Accepted
                || Designated(animal, DesignationDefOf.Slaughter) || Designated(animal, DesignationDefOf.ReleaseAnimalToWild))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native training is unavailable or the animal is designated for removal."); return false; }
            return true;
        }

        private static bool PrepareSlaughter(Operations.SlaughterAnimal command, out Pawn? animal, out Common.Failure failure)
        {
            animal = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Slaughter requires an exact current animal settings/census snapshot.");
            if (!ValidSlaughter(command)) return false;
            animal = Find.CurrentMap.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Animal.EntityId);
            if (animal == null || !Eligible(animal)) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact eligible player animal is unavailable."); return false; }
            if (Settings(animal) != command.Animal.ExpectedSnapshotToken || Census(animal) != command.ExpectedCensusToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Animal settings or census changed; observe before new admission."); return false; }
            if (!SafeToSlaughter(animal)) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Animal is protected or native slaughter eligibility refused it."); return false; }
            return true;
        }

        private static Receipts.EffectEvidence TrainingEvidence(Operations.SetAnimalTraining command, string after) => new Receipts.EffectEvidence
        {
            Animal = new Receipts.AnimalEffect
            {
                Animal = new Receipts.SnapshotEvidence { EntityId = command.Animal.EntityId, BeforeToken = command.Animal.ExpectedSnapshotToken, AfterToken = after },
                CensusToken = command.ExpectedCensusToken, TrainableDef = command.TrainableDef, Wanted = true,
            }
        };
        private static Receipts.EffectEvidence SlaughterEvidence(Operations.SlaughterAnimal command, string after) => new Receipts.EffectEvidence
        {
            Animal = new Receipts.AnimalEffect
            {
                Animal = new Receipts.SnapshotEvidence { EntityId = command.Animal.EntityId, BeforeToken = command.Animal.ExpectedSnapshotToken, AfterToken = after },
                CensusToken = command.ExpectedCensusToken, SlaughterDesignated = true,
            }
        };

        internal static Operations.PreviewReply Preview(Operations.Operation operation, Common.ObservationContext context)
        {
            try
            {
                if (operation.CommandCase == Operations.Operation.CommandOneofCase.SetAnimalTraining)
                {
                    if (!PrepareTraining(operation.SetAnimalTraining, out var animal, out _, out var failure))
                        return new Operations.PreviewReply { Failure = failure };
                    return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                        Context = context.Clone(), Accepted = true, Projected = TrainingEvidence(operation.SetAnimalTraining, Settings(animal!)) } });
                }
                if (operation.CommandCase == Operations.Operation.CommandOneofCase.SlaughterAnimal)
                {
                    if (!PrepareSlaughter(operation.SlaughterAnimal, out var animal, out var failure))
                        return new Operations.PreviewReply { Failure = failure };
                    return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                        Context = context.Clone(), Accepted = true, Projected = SlaughterEvidence(operation.SlaughterAnimal, Settings(animal!)) } });
                }
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Husbandry preview implements SetAnimalTraining and SlaughterAnimal only.") };
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Husbandry preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var operation = request.Operation; var pre = request.Precondition;
            bool training = operation.CommandCase == Operations.Operation.CommandOneofCase.SetAnimalTraining;
            bool slaughter = operation.CommandCase == Operations.Operation.CommandOneofCase.SlaughterAnimal;
            if (!training && !slaughter)
                return Refuse(Common.FailureCode.Unsupported, "Husbandry execute implements SetAnimalTraining and SlaughterAnimal only.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                Pawn? animal; Common.Failure failure;
                if (training) { if (!PrepareTraining(operation.SetAnimalTraining, out animal, out _, out failure)) return new Operations.ExecuteReply { Failure = failure }; }
                else { if (!PrepareSlaughter(operation.SlaughterAnimal, out animal, out failure)) return new Operations.ExecuteReply { Failure = failure }; }
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.Reply!;
                handle = admission.Handle!;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration);
                    if (!current.Success) throw new InvalidOperationException("Husbandry authority changed before native effect.");
                    if (training)
                    {
                        if (!PrepareTraining(operation.SetAnimalTraining, out animal, out var trainable, out failure) || animal == null)
                            throw new InvalidOperationException("Training prerequisites changed after admission.");
                        animal.training!.SetWantedRecursive(trainable, true);
                        var after = Settings(animal);
                        state.Husbandry.Add(pre.Attempt.Clone(), new NativeHusbandryRecord(animal.GetUniqueLoadID(), false, operation.SetAnimalTraining.TrainableDef));
                        evidence = TrainingEvidence(operation.SetAnimalTraining, after);
                        if (!animal.training.GetWanted(trainable)) throw new InvalidOperationException("Native training request readback did not apply.");
                    }
                    else
                    {
                        if (!PrepareSlaughter(operation.SlaughterAnimal, out animal, out failure) || animal == null)
                            throw new InvalidOperationException("Slaughter prerequisites changed after admission.");
                        if (!Designated(animal, DesignationDefOf.Slaughter))
                            Find.CurrentMap.designationManager.AddDesignation(new Designation(animal, DesignationDefOf.Slaughter));
                        var after = Settings(animal);
                        state.Husbandry.Add(pre.Attempt.Clone(), new NativeHusbandryRecord(animal.GetUniqueLoadID(), true, null));
                        evidence = SlaughterEvidence(operation.SlaughterAnimal, after);
                        if (!Designated(animal, DesignationDefOf.Slaughter)) throw new InvalidOperationException("Native slaughter designation readback did not apply.");
                    }
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Husbandry validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted husbandry order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativeHusbandryRecord record)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                var animal = Find.CurrentMap.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == record.AnimalId);
                if (animal == null || !Eligible(animal))
                {
                    result.Unknown = new Receipts.UnknownEffect { Reason = "The exact animal is no longer observable; absence does not prove the setting held." };
                    return result;
                }
                result.CompleteInspection = true;
                var after = Settings(animal);
                var census = Census(animal);
                if (record.Slaughter)
                {
                    var evidence = new Receipts.EffectEvidence { Animal = new Receipts.AnimalEffect {
                        Animal = new Receipts.SnapshotEvidence { EntityId = record.AnimalId, AfterToken = after }, CensusToken = census, SlaughterDesignated = Designated(animal, DesignationDefOf.Slaughter) } };
                    if (Designated(animal, DesignationDefOf.Slaughter)) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                    else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                        Detail = "The slaughter designation is no longer present; do not restore over player changes." };
                }
                else
                {
                    var trainable = record.TrainableDef == null ? null : DefDatabase<TrainableDef>.GetNamedSilentFail(record.TrainableDef);
                    var wanted = trainable != null && animal.training != null && animal.training.GetWanted(trainable);
                    var evidence = new Receipts.EffectEvidence { Animal = new Receipts.AnimalEffect {
                        Animal = new Receipts.SnapshotEvidence { EntityId = record.AnimalId, AfterToken = after }, CensusToken = census, TrainableDef = record.TrainableDef, Wanted = wanted } };
                    if (wanted) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                    else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                        Detail = "The training request is no longer wanted; do not restore over player changes." };
                }
            }
            catch (Exception) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Husbandry inspection unavailable." }; }
            return result;
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
