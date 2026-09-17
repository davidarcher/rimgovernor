package movement

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func (b *MovementBoundary) ObserveMovement(ctx context.Context, dispatch executor.MovementDispatch, current domain.GenerationSnapshot) (executor.MovementEvidence, error) {
	p := dispatch.Attempt
	out := executor.MovementEvidence{StartedAt: b.clock.Now(), Pawn: dispatch.Admission.Pawn, Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	if current.Validate() != nil || current.Native == 0 || !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	lookup, _, err := b.native.LookupMovementAttempt(ctx, attempt)
	if err != nil {
		return out, err
	}
	receipt := lookup.GetReceipt()
	if receipt != nil {
		if err = b.checkReceipt(receipt, dispatch); err != nil {
			return out, err
		}
	}
	reply, _, err := b.native.ObserveMovementProgress(ctx, attempt, receipt)
	if err != nil {
		return out, err
	}
	progress := reply.GetProgress()
	if progress == nil || !proto.Equal(progress.Attempt, attempt.Attempt) {
		return out, executor.ErrEvidence
	}
	actual, err := boundary.Context(progress.Context, current)
	if err != nil {
		return out, err
	}
	if progress.Context.GetTick() < int64(p.Tick) || receipt != nil && progress.Context.GetTick() < receipt.AdmittedContext.GetTick() {
		return out, executor.ErrEvidence
	}
	out.Complete = progress.CompleteInspection != nil && progress.GetCompleteInspection()
	out.Observation.Snapshot, out.Observation.Tick, out.Observation.Causality = actual, domain.Tick(progress.Context.GetTick()), domain.AfterDispatch
	var job *r.JobEffect
	switch v := progress.Effect.(type) {
	case *r.Progress_Unknown:
		if v.Unknown == nil {
			return out, executor.ErrEvidence
		}
	case *r.Progress_Pending:
		if v.Pending == nil {
			return out, executor.ErrEvidence
		}
		job = v.Pending.GetEvidence().GetJob()
		if !out.Complete || job == nil || !job.GetVerified() || !job.GetDrafted() || job.DraftClaimId == nil || actual.Native != p.Snapshot.Native {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectPending
	case *r.Progress_Completed:
		if v.Completed == nil {
			return out, executor.ErrEvidence
		}
		job = v.Completed.GetEvidence().GetJob()
		if !out.Complete || receipt == nil || job == nil || !job.GetVerified() {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectCompleted
	case *r.Progress_Absent:
		if !out.Complete || v.Absent == nil || !boundary.ValidID(v.Absent.GetInspectionToken()) {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectAbsent
	case *r.Progress_Unsuccessful:
		if !out.Complete || v.Unsuccessful == nil {
			return out, executor.ErrEvidence
		}
		job = v.Unsuccessful.GetEvidence().GetJob()
		if job == nil {
			return out, executor.ErrEvidence
		}
		reason := map[r.UnsuccessfulReason]domain.UnsuccessfulReason{r.UnsuccessfulReason_UNSUCCESSFUL_REASON_NATIVE_FAILURE: domain.NativeFailure, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_CANCELLED: domain.NativeCancelled, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_INTERRUPTED: domain.NativeInterrupted, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_EXPIRED: domain.NativeExpired, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED: domain.OutcomeNotAchieved}[v.Unsuccessful.GetReason()]
		if reason == "" {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect, out.Observation.UnsuccessfulReason = domain.EffectUnsuccessful, reason
	default:
		return out, executor.ErrEvidence
	}
	if job != nil {
		if err = movementJob(job, dispatch); err != nil {
			return out, err
		}
		if job.Issued == nil || job.GetIssued() {
			return out, executor.ErrEvidence
		}
		if original := boundary.ReceiptJob(receipt); original != nil && original.JobId != nil && job.JobId != nil && original.GetJobId() != job.GetJobId() {
			return out, executor.ErrEvidence
		}
	}
	out.ObservedAt = b.clock.Now()
	return out, ctx.Err()
}
