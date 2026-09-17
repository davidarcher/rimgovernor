package draft

import (
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// historicalClaim recovers cleanup responsibility, never current ownership or
// completion. Only verified admission evidence survives an ownership replacement.
func (b *DraftBoundary) historicalClaim(p executor.Placement, receipt *r.Receipt, row *n.PawnState, observed *c.ObservationContext) (domain.Fact[domain.DraftClaim], error) {
	unknown := domain.Unknown[domain.DraftClaim]()
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return unknown, err
	}
	issued := false
	switch receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		issued = true
	case *r.Receipt_NoChange:
	default:
		return unknown, executor.ErrHeld
	}
	job := boundary.ReceiptJob(receipt)
	pawn, _ := p.Action.OwnedDraft()
	if job == nil || job.GetPawnId() != string(pawn.Pawn()) || job.Drafted == nil || !job.GetDrafted() || job.Verified == nil || !job.GetVerified() || job.Issued == nil || job.GetIssued() != issued || !boundary.ValidID(job.GetDraftClaimId()) || !boundary.ValidID(job.GetResultingSnapshotToken()) {
		return unknown, executor.ErrEvidence
	}
	allowed := &r.JobEffect{PawnId: job.PawnId, Drafted: job.Drafted, Issued: job.Issued, Verified: job.Verified, VerifiedReason: job.VerifiedReason, DraftClaimId: job.DraftClaimId, ResultingSnapshotToken: job.ResultingSnapshotToken}
	if !proto.Equal(job, allowed) {
		return unknown, executor.ErrEvidence
	}
	if _, err := boundary.PawnToken(row, observed); err != nil {
		return unknown, err
	}
	if observed.GetTick() < receipt.AdmittedContext.GetTick() {
		return unknown, executor.ErrEvidence
	}
	switch state := row.GetDraftClaim().GetState().(type) {
	case *n.DraftClaimObservation_Unowned:
		if state.Unowned == nil {
			return unknown, executor.ErrHeld
		}
	case *n.DraftClaimObservation_Owned:
		owned := state.Owned
		if owned == nil || !boundary.ValidID(owned.GetClaimId()) || !proto.Equal(owned.PawnSnapshot, row.Pawn.Snapshot) {
			return unknown, executor.ErrHeld
		}
		if owned.GetClaimId() == job.GetDraftClaimId() {
			return unknown, executor.ErrHeld
		}
	default:
		return unknown, executor.ErrHeld
	}
	return domain.Known(domain.DraftClaim{Action: p.Action.ID(), Attempt: p.Attempt, Pawn: pawn.Pawn(), Claim: domain.DraftClaimID(job.GetDraftClaimId()), Session: domain.ControllerSessionID(b.session), Origin: p.Snapshot}), nil
}
