package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// ObserveWaste reuses the dedicated ManageWaste lookup/progress bridge calls,
// which already validate the returned receipt/evidence against the exact
// expected attempt (pawn, target, job def); this reconciliation only binds
// that already-validated outcome to the executor's Observation shape,
// unlike ObserveClean's manual receipt/job cross-checks against the generic
// PawnOrder boundary.
func (b *WasteBoundary) ObserveWaste(ctx context.Context, dispatch executor.WasteDispatch, current domain.GenerationSnapshot) (executor.WasteEvidence, error) {
	p := dispatch.Attempt
	out := executor.WasteEvidence{StartedAt: b.clock.Now(), Pawn: dispatch.Admission.Pawn, Target: dispatch.Admission.Target, Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	if current.Validate() != nil || current.Native == 0 || !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	lookup, _, err := b.native.LookupWaste(ctx, attempt)
	if err != nil {
		return out, err
	}
	receipt := lookup.GetReceipt()
	if receipt == nil {
		absent, err := boundary.Unadmitted(lookup, p, current)
		if err != nil {
			return out, err
		}
		out.Observation, out.Complete, out.ObservedAt = absent, true, b.clock.Now()
		return out, nil
	}
	reply, _, err := b.native.ObserveWasteProgress(ctx, attempt, receipt)
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
	switch v := progress.Effect.(type) {
	case *r.Progress_Unknown:
		if v.Unknown == nil {
			return out, executor.ErrEvidence
		}
	case *r.Progress_Pending:
		if v.Pending == nil || !out.Complete || actual.Native != p.Snapshot.Native {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectPending
	case *r.Progress_Completed:
		if v.Completed == nil || !out.Complete || receipt == nil {
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
		reason := map[r.UnsuccessfulReason]domain.UnsuccessfulReason{r.UnsuccessfulReason_UNSUCCESSFUL_REASON_NATIVE_FAILURE: domain.NativeFailure, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_CANCELLED: domain.NativeCancelled, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_INTERRUPTED: domain.NativeInterrupted, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_EXPIRED: domain.NativeExpired, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_TARGET_DEAD: domain.TargetDead, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED: domain.OutcomeNotAchieved}[v.Unsuccessful.GetReason()]
		if reason == "" {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect, out.Observation.UnsuccessfulReason = domain.EffectUnsuccessful, reason
	default:
		return out, executor.ErrEvidence
	}
	out.ObservedAt = b.clock.Now()
	return out, ctx.Err()
}
