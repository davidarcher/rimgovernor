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
    // Typed dispatch for MaintainHerd-*'s direct-write animal management
    // orders: recursive training request (SetAnimalTraining) and the
    // slaughter, tame and release-to-wild designations (SlaughterAnimal,
    // TameAnimal, ReleaseAnimal). Ports the legacy JSON home/husbandry_config
    // tool's (HusbandryTools.Configure) eligibility checks behind the typed
    // boundary. Unlike job-issuing verticals (haul/recover/relieve), each is an
    // immediate settings write with no native job -- the same shape
    // NativeWorkSettings uses for work priorities. Tame targets a wild
    // (factionless) animal; the other three target a player animal.
    internal enum HusbandryKind { Train, Slaughter, Tame, Release }

    internal sealed class NativeHusbandryRecord
    {
        internal readonly string AnimalId;
        internal readonly HusbandryKind Kind;
        internal readonly string? TrainableDef;
        internal NativeHusbandryRecord(string animalId, HusbandryKind kind, string? trainableDef)
        { AnimalId = animalId; Kind = kind; TrainableDef = trainableDef; }
    }

    internal static class NativeHusbandryOperations
    {
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        private static bool Designated(Pawn animal, DesignationDef def) => animal.Map.designationManager.DesignationOn(animal, def) != null;

        // Mirrors HusbandryTools.Census: same-species player animal identity,
        // gender and fertility, so a population change invalidates a stale
        // request. A wild tame target hashes the player herd of its own race.
        internal static string Census(Pawn animal)
        {
            var others = animal.Map.mapPawns.AllPawnsSpawned
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
                Designated(animal, DesignationDefOf.ReleaseAnimalToWild).ToString(),
                Designated(animal, DesignationDefOf.Tame).ToString() };
            if (animal.training != null)
                values.AddRange(DefDatabase<TrainableDef>.AllDefsListForReading.OrderBy(t => t.defName, StringComparer.Ordinal)
                    .Select(t => t.defName + "=" + animal.training.GetWanted(t)));
            return "husbandry-" + Hash(string.Join("|", values));
        }

        private static bool Observable(Pawn? animal) => animal != null && !animal.Destroyed && animal.Spawned
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

        // Mirrors HusbandryTools.SafeToSlaughter.
        internal static bool SafeToSlaughter(Pawn animal) => !animal.Dead && !animal.Downed && !animal.InMentalState
            && animal.Faction == Faction.OfPlayer && animal.playerSettings != null && animal.playerSettings.Master == null
            && !TrainableUtility.GetAllColonistBondsFor(animal).Any()
            && !animal.health.hediffSet.hediffs.OfType<Hediff_Pregnant>().Any()
            && !Designated(animal, DesignationDefOf.ReleaseAnimalToWild)
            && new Designator_Slaughter().CanDesignateThing(animal).Accepted;

        // The release designator's own acceptance plus the bonded/master
        // exclusions slaughter applies: release is non-lethal but still
        // removes the animal from the colony.
        internal static bool SafeToRelease(Pawn animal) => !animal.Dead && !animal.Downed && !animal.InMentalState
            && animal.Faction == Faction.OfPlayer && animal.RaceProps.canReleaseToWild
            && animal.playerSettings != null && animal.playerSettings.Master == null
            && !TrainableUtility.GetAllColonistBondsFor(animal).Any()
            && !Designated(animal, DesignationDefOf.Slaughter) && !Designated(animal, DesignationDefOf.ReleaseAnimalToWild)
            && new Designator_ReleaseAnimalToWild().CanDesignateThing(animal).Accepted;

        private static bool ValidTraining(Operations.SetAnimalTraining? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Animal) && command.HasExpectedCensusToken && ProtoBoundary.IsIdentifier(command.ExpectedCensusToken)
            && command.HasTrainableDef && ProtoBoundary.IsIdentifier(command.TrainableDef);
        private static bool ValidDesignation(Operations.EntityPrecondition? animal, bool hasCensus, string census) => animal != null
            && NativeDraftProtocol.ValidEntity(animal) && hasCensus && ProtoBoundary.IsIdentifier(census);

        internal static bool TryKind(Operations.Operation operation, out HusbandryKind kind)
        {
            kind = HusbandryKind.Train;
            switch (operation.CommandCase)
            {
                case Operations.Operation.CommandOneofCase.SetAnimalTraining: kind = HusbandryKind.Train; return true;
                case Operations.Operation.CommandOneofCase.SlaughterAnimal: kind = HusbandryKind.Slaughter; return true;
                case Operations.Operation.CommandOneofCase.TameAnimal: kind = HusbandryKind.Tame; return true;
                case Operations.Operation.CommandOneofCase.ReleaseAnimal: kind = HusbandryKind.Release; return true;
                default: return false;
            }
        }

        private static Operations.EntityPrecondition Entity(Operations.Operation operation, HusbandryKind kind)
        {
            switch (kind)
            {
                case HusbandryKind.Train: return operation.SetAnimalTraining.Animal;
                case HusbandryKind.Slaughter: return operation.SlaughterAnimal.Animal;
                case HusbandryKind.Tame: return operation.TameAnimal.Animal;
                default: return operation.ReleaseAnimal.Animal;
            }
        }

        private static string CensusToken(Operations.Operation operation, HusbandryKind kind)
        {
            switch (kind)
            {
                case HusbandryKind.Train: return operation.SetAnimalTraining.ExpectedCensusToken;
                case HusbandryKind.Slaughter: return operation.SlaughterAnimal.ExpectedCensusToken;
                case HusbandryKind.Tame: return operation.TameAnimal.ExpectedCensusToken;
                default: return operation.ReleaseAnimal.ExpectedCensusToken;
            }
        }

        private static bool ValidCommand(Operations.Operation operation, HusbandryKind kind)
        {
            switch (kind)
            {
                case HusbandryKind.Train: return ValidTraining(operation.SetAnimalTraining);
                case HusbandryKind.Slaughter: return ValidDesignation(operation.SlaughterAnimal?.Animal, operation.SlaughterAnimal?.HasExpectedCensusToken == true, operation.SlaughterAnimal?.ExpectedCensusToken ?? "");
                case HusbandryKind.Tame: return ValidDesignation(operation.TameAnimal?.Animal, operation.TameAnimal?.HasExpectedCensusToken == true, operation.TameAnimal?.ExpectedCensusToken ?? "");
                default: return ValidDesignation(operation.ReleaseAnimal?.Animal, operation.ReleaseAnimal?.HasExpectedCensusToken == true, operation.ReleaseAnimal?.ExpectedCensusToken ?? "");
            }
        }

        private static DesignationDef DesignationFor(HusbandryKind kind)
        {
            switch (kind)
            {
                case HusbandryKind.Slaughter: return DesignationDefOf.Slaughter;
                case HusbandryKind.Tame: return DesignationDefOf.Tame;
                default: return DesignationDefOf.ReleaseAnimalToWild;
            }
        }

        // Re-validates the exact animal, CAS tokens and native eligibility for
        // one kind; run at preview, admission and again inside the owned
        // authority window immediately before the write.
        private static bool Prepare(Operations.Operation operation, HusbandryKind kind, Common.ObservationContext context, out Pawn? animal, out TrainableDef? trainable, out Common.Failure failure)
        {
            animal = null; trainable = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, kind == HusbandryKind.Train
                ? "Training requires an exact current animal settings/census snapshot and a native TrainableDef."
                : "Animal designation requires an exact current animal settings/census snapshot.");
            if (!ValidCommand(operation, kind)) return false;
            var entity = Entity(operation, kind);
            animal = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == entity.EntityId);
            bool eligible = kind == HusbandryKind.Tame ? EligibleWild(animal) : Eligible(animal);
            if (animal == null || !eligible)
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, kind == HusbandryKind.Tame ? "Exact wild animal is unavailable." : "Exact eligible player animal is unavailable."); return false; }
            if (Settings(animal) != entity.ExpectedSnapshotToken || Census(animal) != CensusToken(operation, kind))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Animal settings or census changed; observe before new admission."); return false; }
            switch (kind)
            {
                case HusbandryKind.Train:
                    trainable = DefDatabase<TrainableDef>.GetNamedSilentFail(operation.SetAnimalTraining.TrainableDef);
                    if (trainable == null || animal.training == null || !animal.training.CanAssignToTrain(trainable).Accepted
                        || Designated(animal, DesignationDefOf.Slaughter) || Designated(animal, DesignationDefOf.ReleaseAnimalToWild))
                    { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native training is unavailable or the animal is designated for removal."); return false; }
                    return true;
                case HusbandryKind.Slaughter:
                    if (!SafeToSlaughter(animal)) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Animal is protected or native slaughter eligibility refused it."); return false; }
                    return true;
                case HusbandryKind.Tame:
                    if (!Tameable(animal)) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native tame eligibility refused the animal or it is already designated."); return false; }
                    return true;
                default:
                    if (!SafeToRelease(animal)) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Animal is protected or native release eligibility refused it."); return false; }
                    return true;
            }
        }

        private static Receipts.EffectEvidence Evidence(string animalId, string? before, string after, string census, HusbandryKind kind, string? trainableDef, bool present)
        {
            var effect = new Receipts.AnimalEffect
            {
                Animal = new Receipts.SnapshotEvidence { EntityId = animalId, AfterToken = after },
                CensusToken = census,
            };
            if (before != null) effect.Animal.BeforeToken = before;
            switch (kind)
            {
                case HusbandryKind.Train: effect.TrainableDef = trainableDef; effect.Wanted = present; break;
                case HusbandryKind.Slaughter: effect.SlaughterDesignated = present; break;
                case HusbandryKind.Tame: effect.TameDesignated = present; break;
                default: effect.ReleaseDesignated = present; break;
            }
            return new Receipts.EffectEvidence { Animal = effect };
        }

        private static Receipts.EffectEvidence Projected(Operations.Operation operation, HusbandryKind kind, Pawn animal)
        {
            var entity = Entity(operation, kind);
            var trainable = kind == HusbandryKind.Train ? operation.SetAnimalTraining.TrainableDef : null;
            return Evidence(entity.EntityId, entity.ExpectedSnapshotToken, Settings(animal), CensusToken(operation, kind), kind, trainable, true);
        }

        internal static Operations.PreviewReply Preview(Operations.Operation operation, Common.ObservationContext context)
        {
            try
            {
                if (!TryKind(operation, out var kind))
                    return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Husbandry preview implements SetAnimalTraining, SlaughterAnimal, TameAnimal and ReleaseAnimal only.") };
                if (!Prepare(operation, kind, context, out var animal, out _, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true, Projected = Projected(operation, kind, animal!) } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Husbandry preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var operation = request.Operation; var pre = request.Precondition;
            if (!TryKind(operation, out var kind))
                return Refuse(Common.FailureCode.Unsupported, "Husbandry execute implements SetAnimalTraining, SlaughterAnimal, TameAnimal and ReleaseAnimal only.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(operation, kind, context, out var animal, out _, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.DecidedReply;
                handle = admission.AdmittedHandle;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration);
                    if (!current.Success) throw new InvalidOperationException("Husbandry authority changed before native effect.");
                    if (!Prepare(operation, kind, context, out animal, out var trainable, out failure) || animal == null)
                        throw new InvalidOperationException("Husbandry prerequisites changed after admission.");
                    var trainableDef = kind == HusbandryKind.Train ? operation.SetAnimalTraining.TrainableDef : null;
                    if (kind == HusbandryKind.Train)
                        animal.training!.SetWantedRecursive(trainable, true);
                    else if (!Designated(animal, DesignationFor(kind)))
                        ProtoBoundary.LoadedMap(context).designationManager.AddDesignation(new Designation(animal, DesignationFor(kind)));
                    state.Husbandry.Add(pre.Attempt.Clone(), new NativeHusbandryRecord(animal.GetUniqueLoadID(), kind, trainableDef));
                    evidence = Projected(operation, kind, animal);
                    bool applied = kind == HusbandryKind.Train ? animal.training!.GetWanted(trainable) : Designated(animal, DesignationFor(kind));
                    if (!applied) throw new InvalidOperationException("Native husbandry readback did not apply.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Husbandry validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted husbandry order requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativeHusbandryRecord record)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                var animal = ProtoBoundary.LoadedMap(context).mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == record.AnimalId);
                // A tame designation is consumed by the native taming job when
                // the animal joins the colony, so a player-faction readback is
                // the completed outcome rather than a lost designation.
                if (record.Kind == HusbandryKind.Tame && Eligible(animal))
                {
                    result.CompleteInspection = true;
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(record.AnimalId, null, Settings(animal!), Census(animal!), record.Kind, null, true) };
                    return result;
                }
                bool observable = record.Kind == HusbandryKind.Tame ? EligibleWild(animal) : Eligible(animal);
                if (animal == null || !observable)
                {
                    result.Unknown = new Receipts.UnknownEffect { Reason = "The exact animal is no longer observable; absence does not prove the setting held." };
                    return result;
                }
                result.CompleteInspection = true;
                bool present;
                if (record.Kind == HusbandryKind.Train)
                {
                    var trainable = record.TrainableDef == null ? null : DefDatabase<TrainableDef>.GetNamedSilentFail(record.TrainableDef);
                    present = trainable != null && animal.training != null && animal.training.GetWanted(trainable);
                }
                else present = Designated(animal, DesignationFor(record.Kind));
                var evidence = Evidence(record.AnimalId, null, Settings(animal), Census(animal), record.Kind, record.TrainableDef, present);
                if (present) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                    Detail = record.Kind == HusbandryKind.Train
                        ? "The training request is no longer wanted; do not restore over player changes."
                        : "The designation is no longer present; do not restore over player changes." };
            }
            catch (Exception) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Husbandry inspection unavailable." }; }
            return result;
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
