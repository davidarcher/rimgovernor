package executor

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"time"
)

type CommsTradeInspection struct {
	StartedAt, ObservedAt                                       time.Time
	Snapshot                                                    domain.GenerationSnapshot
	Tick                                                        domain.Tick
	Eligible                                                    bool
	LastRequestTick                                             int64
	RequestKnown, MatchingWork, MatchingArrival, MatchingSeller bool
}
type CommsTradeBoundary interface {
	InspectCommsTradeRequest(context.Context, Target) (CommsTradeInspection, error)
	WriteCommsTradeRequest(context.Context, Placement) (Receipt, error)
}

func (e *Executor) EnableCommsTradeRequests(boundary CommsTradeBoundary) error {
	if boundary == nil {
		return ErrEvidence
	}
	e.commsTrade = boundary
	return nil
}

// CommsTradeRequestEffect requires payment evidence, never a queued receipt.
// A lost response lacking sufficient correlation remains unknown. An accepted
// queued job that vanished without moving its CAS tick was interrupted.
func CommsTradeRequestEffect(request domain.CommsTradeRequest, view domain.ProgressView, read CommsTradeInspection) (domain.Effect, domain.UnsuccessfulReason) {
	if read.Tick < view.Tick {
		return domain.EffectUnknown, ""
	}
	if !read.RequestKnown {
		return domain.EffectUnsuccessful, domain.TargetDead
	}
	if read.LastRequestTick > request.ExpectedLastRequestTick && read.LastRequestTick >= int64(view.Tick) && (read.MatchingArrival || read.MatchingSeller) {
		return domain.EffectCompleted, ""
	}
	// Native's queue owns the arrival/retry deadline. These bounds only
	// retire a lost attempt after the complete queue and seller reads are
	// empty; they never create a second cooldown fact or authorize resend.
	expiry := int64(360000)
	if request.Kind == domain.TradeRequestOrbital {
		expiry = 5001
	}
	if read.LastRequestTick > request.ExpectedLastRequestTick && !read.MatchingArrival && !read.MatchingSeller && int64(read.Tick) > read.LastRequestTick+expiry {
		return domain.EffectUnsuccessful, domain.NativeExpired
	}
	if read.LastRequestTick == request.ExpectedLastRequestTick {
		if read.MatchingWork {
			return domain.EffectPending, ""
		}
		receipt, known := view.Receipt.Value()
		if known && receipt == domain.ReceiptAccepted {
			return domain.EffectUnsuccessful, domain.NativeInterrupted
		}
	}
	return domain.EffectUnknown, ""
}
func (e *Executor) runCommsTradeRequest(ctx context.Context, action domain.Action, progress domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: progress}
	request, ok := action.CommsTradeRequest()
	if !ok || progress.Action() != action {
		return result, ErrEvidence
	}
	view := progress.View()
	if view.Stage == domain.Completed || view.Stage == domain.Unsuccessful || view.Stage == domain.Cancelled && !view.Unresolved {
		return result, nil
	}
	snapshot := authority.Snapshot
	if snapshot.Plan != view.Plan || snapshot.Revision != view.Revision {
		if e.roundsScope == nil {
			return result, ErrAuthority
		}
		snapshot.Plan, snapshot.Revision = view.Plan, view.Revision
	}
	if err := e.guard(ctx, snapshot, generation); err != nil {
		return result, err
	}
	read, err := e.commsTrade.InspectCommsTradeRequest(ctx, Target{action, snapshot})
	if err != nil {
		return result, err
	}
	if err := e.guard(ctx, snapshot, generation); err != nil {
		return result, err
	}
	if read.Snapshot != snapshot || read.Tick < view.Tick || !e.fresh(read.StartedAt, read.ObservedAt) {
		return result, ErrHeld
	}
	if view.Unresolved {
		effect, reason := CommsTradeRequestEffect(request, view, read)
		// Unknown facts do not move the last verified pre-payment tick:
		// later queue/arrival evidence must still correlate to this attempt.
		if effect == domain.EffectUnknown {
			return result, nil
		}
		next, err := e.journal.Observe(ctx, view.Plan, domain.Observation{Action: view.Action, Attempt: view.Attempt, Snapshot: snapshot, Tick: read.Tick, Causality: domain.AfterDispatch, Effect: effect, UnsuccessfulReason: reason}, snapshot)
		if err == nil {
			result.Progress = next
		}
		return result, err
	}
	if !read.Eligible || read.LastRequestTick != request.ExpectedLastRequestTick {
		next, err := e.journal.Cancel(ctx, view.Plan, view.Action)
		if err == nil {
			result.Progress = next
		}
		return result, err
	}
	next, err := e.journal.Prepare(ctx, view.Plan, view.Action, snapshot, read.Tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	if err := e.guard(ctx, snapshot, generation); err != nil {
		return result, err
	}
	if !e.fresh(read.StartedAt, read.ObservedAt) {
		return result, ErrHeld
	}
	next, err = e.journal.Dispatch(ctx, view.Plan, view.Action, snapshot, read.Tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	placement := Placement{action, next.View().Attempt, snapshot, read.Tick}
	if err := e.guard(ctx, snapshot, generation); err != nil {
		return e.record(result, view.Plan, placement, domain.ReceiptUnknown, err)
	}
	result.NativeCalled = true
	receipt, err := e.commsTrade.WriteCommsTradeRequest(ctx, placement)
	kind := receipt.Kind
	if err != nil {
		kind = receiptAfterCallError(err)
	} else if receipt.Action != view.Action || receipt.Attempt != placement.Attempt || receipt.Snapshot != snapshot {
		kind, err = domain.ReceiptUnknown, ErrEvidence
	}
	return e.record(result, view.Plan, placement, kind, errors.Join(err, ctx.Err()))
}
