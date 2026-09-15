#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed dispatch for QueueSurgery: queues one exact already-inspected
    // native operation bill via the same real HealthCardUtility.CreateSurgeryBill
    // path the legacy JSON home/medical_operations tool (MedicalOperationsTool.cs)
    // drives, so an order here is exactly the bill a player's operations tab
    // would queue. There is no doctor role here: native work selection assigns
    // a practitioner from the queued bill, the same way the vanilla float menu
    // does. Patient identity/CAS reuses the shared NativePawnControlState token
    // (NativePawnControlObservation.Apply is what stamps EntityRef.Snapshot for
    // every pawn read); the health-signature and care-policy tokens are surgery's
    // own, self-computed fresh each call the same way NativeHusbandryOperations
    // self-computes its Settings/Census tokens.
    internal sealed class NativeSurgeryRecord
    {
        private readonly Pawn pawn;
        private readonly RecipeDef recipe;
        private readonly BodyPartRecord? part;
        private readonly string billId;
        private readonly Common.ObservationContext admitted;

        internal NativeSurgeryRecord(Pawn pawn, RecipeDef recipe, BodyPartRecord? part, string billId, Common.ObservationContext context)
        { this.pawn = pawn; this.recipe = recipe; this.part = part; this.billId = billId; admitted = context.Clone(); }

        private int PartIndex => part == null ? -1 : pawn.RaceProps.body.AllParts.IndexOf(part);

        // Issued describes only whether THIS call just issued the bill; Progress
        // always reports Queued from current observation, matching the recovery/
        // haul evidence contract's Issued/Progress split.
        internal Receipts.EffectEvidence Evidence(string healthToken, bool queued) => new Receipts.EffectEvidence
        {
            Surgery = new Receipts.SurgeryEffect
            {
                PatientId = pawn.GetUniqueLoadID(), RecipeDef = recipe.defName, PartIndex = PartIndex,
                BillId = billId, Queued = queued, HealthToken = healthToken,
            }
        };

        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                if (!context.Identity.Equals(admitted.Identity) || context.Tick < admitted.Tick
                    || pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Map != Find.CurrentMap)
                    throw new InvalidOperationException("Current surgery patient context cannot be inspected.");
                result.CompleteInspection = true;
                var health = NativeSurgeryOperations.HealthSignature(pawn);
                var stillQueued = pawn.BillStack != null && pawn.BillStack.Bills.Any(b => b.GetUniqueLoadID() == billId);
                var added = recipe.addsHediff != null && pawn.health.hediffSet.hediffs.Any(h => h.def == recipe.addsHediff && h.Part == part);
                var removed = recipe.removesHediff != null && !pawn.health.hediffSet.hediffs.Any(h => h.def == recipe.removesHediff && h.Part == part && h.Visible);
                if (added || removed)
                    result.Completed = new Receipts.CompletedEffect { Evidence = Evidence(health, stillQueued) };
                else if (stillQueued)
                    result.Pending = new Receipts.PendingEffect { Evidence = Evidence(health, true) };
                else
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect
                    {
                        Reason = Receipts.UnsuccessfulReason.Interrupted, Evidence = Evidence(health, false),
                        Detail = "Native surgery bill is no longer queued and no expected health change is observed.",
                    };
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Surgery inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
    }

    internal static class NativeSurgeryOperations
    {
        internal static bool Valid(Operations.QueueSurgery? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Patient) && command.HasRecipeDef && ProtoBoundary.IsIdentifier(command.RecipeDef)
            && command.HasPartIndex && command.PartIndex >= -1;

        // Mirrors MedicalOperationsTool's health-signature computation exactly:
        // one token per current hediff (load ID, def, body part index), order-
        // stable, so any hediff change (surgery completing, injury, healing)
        // invalidates it.
        internal static string HealthSignature(Pawn pawn) => string.Join(";", pawn.health.hediffSet.hediffs.OrderBy(h => h.loadID)
            .Select(h => h.GetUniqueLoadID() + ":" + h.def.defName + ":" + (h.Part == null ? -1 : pawn.RaceProps.body.AllParts.IndexOf(h.Part))));

        internal static Operations.MedicalCare CareToWire(MedicalCareCategory care) => care switch
        {
            MedicalCareCategory.NoCare => Operations.MedicalCare.NoCare,
            MedicalCareCategory.NoMeds => Operations.MedicalCare.NoMedicine,
            MedicalCareCategory.HerbalOrWorse => Operations.MedicalCare.HerbalOrWorse,
            MedicalCareCategory.NormalOrWorse => Operations.MedicalCare.NormalOrWorse,
            MedicalCareCategory.Best => Operations.MedicalCare.Best,
            _ => Operations.MedicalCare.Unspecified,
        };

        // Mirrors MedicalOperationsTool's per-recipe/part "supports" gate: a
        // recipe with a required confirmation, a hediff-level change, a
        // violation, or without any supported health postcondition is
        // inspection-only and can never be queued through this typed path.
        internal static bool Supported(Pawn pawn, RecipeDef recipe, BodyPartRecord? part)
        {
            if (recipe.addsHediff == null && recipe.removesHediff == null) return false;
            if (recipe.changesHediffLevel != null) return false;
            if (!string.IsNullOrEmpty(recipe.Worker.GetConfirmation(pawn))) return false;
            if (recipe.Worker.IsViolationOnPawn(pawn, part, Faction.OfPlayer)) return false;
            if (recipe.addsHediff != null)
            {
                if (!CompRoyalImplant.CheckForViolations(pawn, recipe.addsHediff, recipe.hediffLevelOffset).NullOrEmpty()) return false;
                if (pawn.health.hediffSet.hediffs.Any(h => h.def == recipe.addsHediff && h.Part == part)) return false;
            }
            if (recipe.removesHediff != null && !pawn.health.hediffSet.hediffs.Any(h => h.def == recipe.removesHediff && h.Part == part && h.Visible))
                return false;
            return true;
        }

        // Mirrors MedicalOperationsTool's queueing gate: practitioner, ingredient,
        // medicine-policy and existing-bill-review checks, beyond Supported's
        // per-recipe health-postcondition gate.
        private static bool Eligible(Pawn pawn, RecipeDef recipe, BodyPartRecord? part, Map map, out string reason)
        {
            reason = "";
            if (!recipe.AvailableNow || !recipe.Worker.AvailableReport(pawn).Accepted)
            { reason = "Recipe is not currently available."; return false; }
            if (!recipe.AvailableOnNow(pawn, part)) { reason = "Recipe is not available on this body part."; return false; }
            if (!Supported(pawn, recipe, part))
            { reason = "Recipe needs an unsupported confirmation or health postcondition."; return false; }
            var hasDoctor = map.mapPawns.FreeColonistsSpawned.Any(p => p != pawn && !p.Dead && !p.Downed && !p.Drafted
                && !p.InMentalState && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) && recipe.PawnSatisfiesSkillRequirements(p));
            if (!hasDoctor) { reason = "No available practitioner satisfies native skills."; return false; }
            if (recipe.PotentiallyMissingIngredients(null, map).Any())
            { reason = "Required native ingredients are unavailable."; return false; }
            var requiresMedicine = recipe.ingredients.Any(i => i.filter.AllowedThingDefs.Any(d => d.IsMedicine));
            if (requiresMedicine)
            {
                var hasMedicine = map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine).Any(t => !t.IsForbidden(pawn) && !t.Position.Fogged(map)
                    && pawn.playerSettings != null && pawn.playerSettings.medCare.AllowsMedicine(t.def) && recipe.ingredients.Any(i => i.filter.Allows(t)));
                if (!hasMedicine) { reason = "No observed medicine is permitted by the patient care policy and recipe."; return false; }
            }
            if (pawn.playerSettings == null || pawn.playerSettings.medCare <= MedicalCareCategory.NoMeds)
            { reason = "Patient medical care policy does not permit surgery medicine."; return false; }
            if (pawn.BillStack != null && pawn.BillStack.Bills.Any())
            { reason = "Existing patient bills require player review before adding an operation."; return false; }
            return true;
        }

        // Structural resolution only: exact living patient, fresh generic pawn
        // CAS token, exact recipe/body part. requireExpected additionally
        // enforces the caller's expected_health_token/expected_care preconditions
        // (Execute always requires them; Preview only when the caller supplied
        // at least one, letting an unconstrained call establish the baseline).
        private static bool Prepare(Operations.QueueSurgery command, Common.ObservationContext context, bool requireExpected,
            out NativeControlIdentity identity, out Pawn? pawn, out RecipeDef? recipe, out BodyPartRecord? part,
            out string health, out MedicalCareCategory care, out Common.Failure failure)
        {
            identity = new NativeControlIdentity(Current.Game, Find.CurrentMap, context.Identity.ColonyId, context.Identity.LoadToken);
            pawn = null; recipe = null; part = null; health = ""; care = MedicalCareCategory.NoCare;
            failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Live native pawn control hooks are required.");
            if (!NativePawnControlState.IsReady) return false;
            var map = Find.CurrentMap;
            // Matches MedicalOperationsTool.Inspect's own patient scope exactly
            // (a free player colonist), not the broader AllPawnsSpawned an
            // acting pawn like NativeRecoveryOperations resolves. A downed or
            // undrafted colonist is still a FreeColonistsSpawned member.
            pawn = map.mapPawns.FreeColonistsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Patient.EntityId);
            if (pawn == null || pawn.Dead) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Living current-map player patient is required."); return false; }
            var check = NativePawnControlState.Check(identity, pawn, command.Patient.ExpectedSnapshotToken, out _);
            if (check != NativePawnControlResult.Ready) { failure = NativeDraftProtocol.Failure(check, context); return false; }
            recipe = pawn.def.AllRecipes?.FirstOrDefault(r => r.defName == command.RecipeDef);
            if (recipe == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact recipe is not offered by this patient."); return false; }
            var allParts = pawn.RaceProps.body.AllParts;
            part = command.PartIndex < 0 ? null : command.PartIndex < allParts.Count ? allParts[command.PartIndex] : null;
            if (command.PartIndex >= 0 && part == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact body part index is unavailable."); return false; }
            if (recipe.targetsBodyPart && !recipe.Worker.GetPartsToApplyOn(pawn, recipe).Contains(part))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Recipe does not apply to this body part."); return false; }
            health = HealthSignature(pawn);
            care = pawn.playerSettings?.medCare ?? MedicalCareCategory.NoCare;
            if (requireExpected && (!command.HasExpectedHealthToken || command.ExpectedHealthToken != health
                || !command.HasExpectedCare || CareToWire(care) != command.ExpectedCare))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Patient health or care policy changed; observe before new admission."); return false; }
            return true;
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.QueueSurgery; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Surgery requires an exact patient, exact recipe defName and a whole-body or exact body part index.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, true, out var identity, out var pawn, out var recipe, out var part, out var health, out var care, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!Eligible(pawn!, recipe!, part, Find.CurrentMap, out var reason))
                    return Refuse(Common.FailureCode.InvalidRequest, reason);
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                guard = authority.Check(pre.ExpectedGeneration);
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                if (!Prepare(command, context, true, out identity, out pawn, out recipe, out part, out health, out care, out failure)
                    || !Eligible(pawn!, recipe!, part, Find.CurrentMap, out reason))
                    return new Operations.ExecuteReply { Failure = failure ?? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, reason) };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.Reply!;
                handle = admission.Handle!;
                using (authority.Owned())
                {
                    if (!NativePawnControlState.IsReady
                        || !Prepare(command, context, true, out identity, out pawn, out recipe, out part, out health, out care, out failure)
                        || !Eligible(pawn!, recipe!, part, Find.CurrentMap, out reason))
                        throw new InvalidOperationException("Surgery prerequisites changed after admission.");
                    guard = authority.Check(pre.ExpectedGeneration);
                    if (!guard.Success) throw new InvalidOperationException("Surgery authority changed before native effect.");
                    if (!Find.TickManager.Paused) throw new InvalidOperationException("Queueing requires the game to be paused.");
                    var bill = HealthCardUtility.CreateSurgeryBill(pawn!, recipe, part);
                    if (bill == null || pawn!.BillStack == null || !pawn.BillStack.Bills.Contains(bill))
                        throw new InvalidOperationException("Native surgery bill was not queued.");
                    var record = new NativeSurgeryRecord(pawn, recipe!, part, bill.GetUniqueLoadID(), context);
                    state.Surgeries.Add(pre.Attempt.Clone(), record);
                    evidence = record.Evidence(health, true);
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Surgery validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted surgery requires observation: " + error.GetType().Name) };
            }
        }

        internal static Operations.PreviewReply Preview(Operations.QueueSurgery command, Common.ObservationContext context)
        {
            if (!Valid(command))
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Surgery requires an exact patient, exact recipe defName and a whole-body or exact body part index.") };
            try
            {
                // An unconstrained call (no expected_health_token/expected_care set)
                // establishes the current baseline, the same role a plain building
                // read plays before a bed assignment's stricter CAS-matched preview.
                var requireExpected = command.HasExpectedHealthToken || command.HasExpectedCare;
                if (!Prepare(command, context, requireExpected, out _, out var pawn, out var recipe, out var part, out var health, out _, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                var accepted = Eligible(pawn!, recipe!, part, Find.CurrentMap, out var reason);
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply
                {
                    Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = accepted,
                        Reason = accepted ? "Exact native recipe and body part are currently queueable for this patient." : reason,
                        Projected = new Receipts.EffectEvidence
                        {
                            Surgery = new Receipts.SurgeryEffect
                            {
                                PatientId = pawn!.GetUniqueLoadID(), RecipeDef = recipe!.defName,
                                PartIndex = part == null ? -1 : pawn.RaceProps.body.AllParts.IndexOf(part),
                                Queued = false, HealthToken = health,
                            }
                        }
                    }
                });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Surgery preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
