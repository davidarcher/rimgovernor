package bridge

import (
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func movementReceipt(v *r.Receipt, expected MovementAttempt) error {
	if v == nil || !proto.Equal(v.Attempt, expected.Attempt) || !proto.Equal(v.AuthorizingOwner, expected.Owner) {
		return contract("movement receipt attempt or owner mismatch")
	}
	if err := buildingUnknown(v); err != nil {
		return err
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.NativeGeneration, true); err != nil {
		return err
	}
	var evidence *r.EffectEvidence
	complete := false
	issued := false
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("movement applied missing")
		}
		evidence = outcome.Applied.Observed
		complete = true
		issued = true
	case *r.Receipt_NoChange:
		if outcome.NoChange == nil || !diagnostic(outcome.NoChange.Detail) {
			return contract("movement no-change missing")
		}
		evidence = outcome.NoChange.Observed
		complete = true
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil || !diagnostic(outcome.Uncertain.Detail) {
			return contract("movement uncertainty missing")
		}
		evidence = outcome.Uncertain.LastObserved
	default:
		return contract("movement receipt outcome missing")
	}
	if evidence == nil && !complete {
		return nil
	}
	job, err := movementEvidence(evidence, expected)
	if err != nil {
		return err
	}
	if complete && ((issued && job.JobId == nil || !issued && job.JobId != nil) || job.Drafted == nil || !job.GetDrafted() || job.Verified == nil || !job.GetVerified() || job.Issued == nil || job.GetIssued() != issued || job.DraftClaimId == nil || job.DraftOwner == nil || job.ResultingSnapshotToken == nil) {
		return contract("verified owned draft facts missing")
	}
	return nil
}
func movementEvidence(evidence *r.EffectEvidence, expected MovementAttempt) (*r.JobEffect, error) {
	job := evidence.GetJob()
	if job == nil || job.PawnId == nil || job.GetPawnId() != expected.PawnID || job.TargetA == nil || !proto.Equal(job.TargetA.GetCell(), expected.Destination) {
		return nil, contract("movement pawn or target mismatch")
	}
	allowed := &r.JobEffect{PawnId: job.PawnId, JobId: job.JobId, JobDef: job.JobDef, TargetA: job.TargetA, Drafted: job.Drafted, Issued: job.Issued, Verified: job.Verified, VerifiedReason: job.VerifiedReason, DraftOwner: job.DraftOwner, DraftClaimId: job.DraftClaimId, ResultingSnapshotToken: job.ResultingSnapshotToken}
	if !proto.Equal(job, allowed) || !diagnostic(job.VerifiedReason) || job.Issued == nil || job.Verified == nil || job.Drafted == nil || job.ResultingSnapshotToken == nil {
		return nil, contract("movement effect fields missing or unsupported")
	}
	if (job.JobId == nil) != (job.JobDef == nil) || job.JobId != nil && (job.GetJobId() < 0 || job.GetJobDef() != "Goto") {
		return nil, contract("movement job mismatch")
	}
	for _, id := range []*string{job.DraftOwner, job.DraftClaimId, job.ResultingSnapshotToken} {
		if id != nil {
			if err := validID(*id); err != nil {
				return nil, err
			}
		}
	}
	if (job.DraftOwner == nil) != (job.DraftClaimId == nil) || job.DraftOwner != nil && job.GetDraftOwner() != expected.Owner.GetControllerSessionId() {
		return nil, contract("movement claim owner mismatch")
	}
	return job, nil
}
func movementProgress(v *r.Progress, expected MovementAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("movement progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil {
		if err := movementReceipt(admitted, expected); err != nil {
			return err
		}
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("movement progress predates admission")
	}
	var evidence *r.EffectEvidence
	completed := false
	pending := false
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("movement unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil {
			return contract("movement pending missing")
		}
		evidence = outcome.Pending.Evidence
		pending = true
	case *r.Progress_Completed:
		if outcome.Completed == nil {
			return contract("movement completed missing")
		}
		evidence = outcome.Completed.Evidence
		completed = true
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("movement absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || outcome.Unsuccessful.GetReason().Descriptor().Values().ByNumber(outcome.Unsuccessful.GetReason().Number()) == nil || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("movement unsuccessful reason missing")
		}
		evidence = outcome.Unsuccessful.Evidence
	default:
		return contract("movement progress state missing")
	}
	if !v.GetCompleteInspection() {
		return contract("movement terminal inspection missing")
	}

	job, err := movementEvidence(evidence, expected)
	if err != nil {
		return err
	}
	if pending && (v.Context.NativeGeneration == nil || v.Context.GetNativeGeneration() != expected.NativeGeneration || !job.GetVerified() || !job.GetDrafted() || job.DraftClaimId == nil || job.DraftOwner == nil) {
		return contract("movement pending owned evidence missing")
	}
	if completed && (admitted == nil || v.Context.NativeGeneration == nil || v.Context.GetNativeGeneration() != expected.NativeGeneration || job.Drafted == nil || !job.GetDrafted() || job.Verified == nil || !job.GetVerified() || job.DraftClaimId == nil || job.DraftOwner == nil || job.ResultingSnapshotToken == nil) {
		return contract("movement completion facts missing")
	}
	if job.GetIssued() {
		return contract("movement progress cannot issue a job")
	}
	original := draftObserved(admitted)
	// The immutable receipt may retain no post-write readback. Native progress
	// still correlates the admitted attempt; compare only original facts retained.
	if completed && original != nil && (!proto.Equal(original.TargetA, job.TargetA) || original.JobId != nil && (job.JobId == nil || original.GetJobId() != job.GetJobId()) || original.JobDef != nil && original.GetJobDef() != job.GetJobDef() || admitted.GetNoChange() != nil && job.JobId != nil) {
		return contract("movement completion lacks original job correlation")
	}
	if original != nil && original.JobId != nil && (job.JobId == nil || original.GetJobId() != job.GetJobId()) {
		return contract("movement progress job mismatch")
	}
	if original != nil && original.DraftClaimId != nil && job.DraftClaimId != nil && original.GetDraftClaimId() != job.GetDraftClaimId() {
		return contract("movement progress claim mismatch")
	}
	return nil
}
