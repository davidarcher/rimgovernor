package bridge

import (
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// pawnOrderEvidence mirrors attackEvidence, but tend/rescue are undrafted
// vanilla jobs: Drafted, DraftClaimId and DraftOwner must be absent rather
// than present and owner-correlated.
func pawnOrderEvidence(evidence *r.EffectEvidence, expected PawnOrderAttempt) (*r.JobEffect, error) {
	job := evidence.GetJob()
	if job == nil || job.PawnId == nil || job.GetPawnId() != expected.PawnID || job.TargetA == nil || job.TargetA.GetThingId() != expected.TargetID {
		return nil, contract("pawn order pawn or target mismatch")
	}
	allowed := &r.JobEffect{PawnId: job.PawnId, JobId: job.JobId, JobDef: job.JobDef, TargetA: job.TargetA, Drafted: job.Drafted, Issued: job.Issued, Verified: job.Verified, VerifiedReason: job.VerifiedReason, DraftClaimId: job.DraftClaimId, ResultingSnapshotToken: job.ResultingSnapshotToken}
	if !proto.Equal(job, allowed) || !diagnostic(job.VerifiedReason) || job.Issued == nil || job.Verified == nil || job.ResultingSnapshotToken == nil {
		return nil, contract("pawn order effect fields missing or unsupported")
	}
	if job.Drafted != nil && job.GetDrafted() || job.DraftClaimId != nil || job.DraftOwner != nil {
		return nil, contract("undrafted pawn order cannot carry draft claim facts")
	}
	if job.JobId == nil || job.JobDef == nil || job.GetJobId() < 0 || !pawnOrderJobDefAllowed(expected.Kind, job.GetJobDef()) {
		return nil, contract("pawn order job mismatch")
	}
	if err := validID(job.GetResultingSnapshotToken()); err != nil {
		return nil, err
	}
	return job, nil
}
func pawnOrderReceipt(v *r.Receipt, expected PawnOrderAttempt) error {
	if v == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("pawn order receipt attempt or owner mismatch")
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
			return contract("pawn order applied missing")
		}
		evidence = outcome.Applied.Observed
		complete = true
		issued = true
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil || !diagnostic(outcome.Uncertain.Detail) {
			return contract("pawn order uncertainty missing")
		}
		evidence = outcome.Uncertain.LastObserved
	default:
		return contract("pawn order receipt outcome missing")
	}
	if evidence == nil && !complete {
		return nil
	}
	job, err := pawnOrderEvidence(evidence, expected)
	if err != nil {
		return err
	}
	if complete && ((issued && job.JobId == nil || !issued && job.JobId != nil) || job.Verified == nil || !job.GetVerified() || job.Issued == nil || job.GetIssued() != issued) {
		return contract("verified pawn order facts missing")
	}
	return nil
}
func pawnOrderProgress(v *r.Progress, expected PawnOrderAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("pawn order progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil {
		if err := pawnOrderReceipt(admitted, expected); err != nil {
			return err
		}
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("pawn order progress predates admission")
	}
	var evidence *r.EffectEvidence
	completed := false
	pending := false
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("pawn order unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil {
			return contract("pawn order pending missing")
		}
		evidence = outcome.Pending.Evidence
		pending = true
	case *r.Progress_Completed:
		if outcome.Completed == nil {
			return contract("pawn order completed missing")
		}
		evidence = outcome.Completed.Evidence
		completed = true
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("pawn order absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || outcome.Unsuccessful.GetReason().Descriptor().Values().ByNumber(outcome.Unsuccessful.GetReason().Number()) == nil || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("pawn order unsuccessful reason missing")
		}
		evidence = outcome.Unsuccessful.Evidence
	default:
		return contract("pawn order progress state missing")
	}
	if !v.GetCompleteInspection() {
		return contract("pawn order terminal inspection missing")
	}
	job, err := pawnOrderEvidence(evidence, expected)
	if err != nil {
		return err
	}
	if pending && (v.Context.NativeGeneration == nil || v.Context.GetNativeGeneration() != expected.NativeGeneration || !job.GetVerified()) {
		return contract("pawn order pending evidence missing")
	}
	if completed && (admitted == nil || !job.GetVerified()) {
		return contract("pawn order causal completion facts missing")
	}
	if job.GetIssued() {
		return contract("pawn order progress cannot issue a job")
	}
	original := draftObserved(admitted)
	if completed && original != nil && (!proto.Equal(original.TargetA, job.TargetA) || original.JobId != nil && (job.JobId == nil || original.GetJobId() != job.GetJobId()) || original.JobDef != nil && original.GetJobDef() != job.GetJobDef()) {
		return contract("pawn order completion lacks original job correlation")
	}
	if original != nil && original.JobId != nil && (job.JobId == nil || original.GetJobId() != job.GetJobId()) {
		return contract("pawn order progress job mismatch")
	}
	return nil
}
