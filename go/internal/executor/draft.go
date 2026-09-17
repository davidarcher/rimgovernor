package executor

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func NewWithDraft(journal DraftJournal, building Boundary, draft DraftBoundary, clock Clock, limits Limits, routine ...RoutineScope) (*Executor, error) {
	if draft == nil {
		return nil, errors.New("draft boundary required")
	}
	e, err := New(journal, building, clock, limits, routine...)
	if err != nil {
		return nil, err
	}
	e.draftJournal = journal
	e.draft = draft
	return e, nil
}

func (e *Executor) runDraft(ctx context.Context, action domain.Action, progress domain.Progress, authority Authority, generation context.Context) (Result, error) {
	result := Result{Progress: progress}
	v := progress.View()
	if progress.Action() != action {
		return result, ErrEvidence
	}
	if v.Unresolved {
		current := e.current().Snapshot
		if current.Validate() != nil || current.Colony != v.Snapshot.Colony || current.Map != v.Snapshot.Map || current.Load != v.Snapshot.Load {
			return result, ErrAuthority
		}
		evidence, err := e.draft.ObserveDraft(ctx, draftAttempt(action, progress), current)
		if err != nil {
			return result, err
		}
		if ctx.Err() != nil {
			return result, ctx.Err()
		}
		if generation.Err() != nil || !e.current().Snapshot.Matches(current) {
			return result, ErrAuthority
		}
		if !evidence.Observation.Snapshot.Matches(current) {
			return result, ErrEvidence
		}
		return e.observeDraft(ctx, result, evidence)
	}
	switch v.Stage {
	case domain.Completed, domain.Cancelled, domain.Unsuccessful:
		return result, nil
	case domain.Pending, domain.Prepared:
	default:
		return result, ErrEvidence
	}
	expected := authority.Snapshot
	if expected.Plan != v.Plan || expected.Revision != v.Revision {
		return result, ErrAuthority
	}
	var inspection DraftInspection
	for range 2 {
		if err := e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		var err error
		inspection, err = e.draft.InspectDraft(ctx, Target{action, expected})
		if err != nil {
			return result, err
		}
		if err = e.guard(ctx, expected, generation); err != nil {
			return result, err
		}
		if !inspection.Current.Matches(expected) || !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			// A generation mismatch or a stale read is a StaleFacts hold, not
			// a silent one: record it so the journal shows why the draft
			// never reached prepare (issue #70).
			result.Refused = []policy.Refusal{{Action: v.Action, Reason: policy.StaleFacts}}
			result.Progress = e.holdRefusal(ctx, v.Plan, v.Action, result.Refused, max(v.Tick, inspection.Tick), result.Progress)
			return result, ErrHeld
		}
		decision := policy.EvaluateOwnedDraft(policy.DraftRequest{Action: action, Progress: result.Progress, Current: inspection.Current, Tick: inspection.Tick, Pawn: inspection.Pawn, Emergency: inspection.Emergency})
		result.Refused = decision.Refused
		if !decision.Admitted {
			if len(decision.Emergency.Holds) > 0 {
				result.Progress = e.holdEmergency(ctx, v.Plan, v.Action, decision.Emergency, inspection.Tick, result.Progress)
			} else {
				result.Progress = e.holdRefusal(ctx, v.Plan, v.Action, decision.Refused, max(v.Tick, inspection.Tick), result.Progress)
			}
			return result, ErrHeld
		}
		admission := store.DraftAdmission{Snapshot: expected, Tick: inspection.Tick, Pawn: inspection.Pawn.Pawn, PawnSnapshotToken: inspection.PawnSnapshotToken}
		next, err := e.draftJournal.PrepareDraft(ctx, v.Plan, v.Action, admission)
		if err != nil {
			return result, err
		}
		result.Progress = next
	}
	if err := e.guard(ctx, expected, generation); err != nil {
		return result, err
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return result, ErrHeld
	}
	next, err := e.journal.Dispatch(ctx, v.Plan, v.Action, expected, inspection.Tick)
	if err != nil {
		return result, err
	}
	result.Progress = next
	attempt := Placement{action, next.View().Attempt, expected, inspection.Tick}
	if err = e.guard(ctx, expected, generation); err != nil {
		return e.recordDraft(result, attempt, domain.ReceiptUnknown, domain.Unknown[domain.DraftClaim](), err)
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return e.recordDraft(result, attempt, domain.ReceiptUnknown, domain.Unknown[domain.DraftClaim](), ErrHeld)
	}
	result.NativeCalled = true
	receipt, err := e.draft.Draft(ctx, DraftDispatch{attempt, inspection.PawnSnapshotToken})
	kind, claim := receipt.Receipt.Kind, receipt.Claim
	if err != nil {
		kind = domain.ReceiptUnknown
		claim = domain.Unknown[domain.DraftClaim]()
	} else if receipt.Receipt.Action != v.Action || receipt.Receipt.Attempt != attempt.Attempt || receipt.Receipt.Snapshot != expected {
		err = ErrEvidence
		kind = domain.ReceiptUnknown
		claim = domain.Unknown[domain.DraftClaim]()
	}
	if _, check := next.RecordDraftReceipt(attempt.Attempt, kind, claim); check != nil {
		err = errors.Join(err, ErrEvidence)
		kind = domain.ReceiptUnknown
		claim = domain.Unknown[domain.DraftClaim]()
	}
	return e.recordDraft(result, attempt, kind, claim, errors.Join(err, ctx.Err()))
}
func draftAttempt(action domain.Action, p domain.Progress) Placement {
	v := p.View()
	return Placement{action, v.Attempt, v.Snapshot, v.Tick}
}
func (e *Executor) recordDraft(result Result, p Placement, kind domain.Receipt, claim domain.Fact[domain.DraftClaim], cause error) (Result, error) {
	ctx, cancel := context.WithTimeout(context.Background(), e.limits.JournalTimeout)
	defer cancel()
	next, err := e.draftJournal.RecordDraftReceipt(ctx, p.Snapshot.Plan, p.Action.ID(), p.Attempt, kind, claim)
	if err == nil {
		result.Progress = next
	}
	return result, errors.Join(cause, err)
}
func (e *Executor) observeDraft(ctx context.Context, result Result, evidence DraftEvidence) (Result, error) {
	v := result.Progress.View()
	wanted, ok := result.Progress.Action().OwnedDraft()
	o := evidence.Observation
	if !ok || evidence.Pawn != wanted.Pawn() || o.Action != v.Action || o.Attempt != v.Attempt || !e.fresh(evidence.StartedAt, evidence.ObservedAt) {
		return result, ErrEvidence
	}
	if o.Effect == domain.EffectUnsuccessful {
		switch o.UnsuccessfulReason {
		case domain.NativeFailure, domain.NativeCancelled, domain.NativeInterrupted, domain.NativeExpired, domain.TargetDead, domain.OutcomeNotAchieved:
		default:
			return result, ErrEvidence
		}
	} else if o.UnsuccessfulReason != "" {
		return result, ErrEvidence
	}
	switch o.Effect {
	case domain.EffectCompleted:
		drafted, known := evidence.Drafted.Value()
		_, claimKnown := evidence.Claim.Value()
		if !evidence.Complete || !known || !drafted || !claimKnown {
			return result, ErrEvidence
		}
	case domain.EffectAbsent, domain.EffectUnsuccessful:
		if !evidence.Complete {
			return result, ErrEvidence
		}
	case domain.EffectUnknown, domain.EffectPending:
	default:
		return result, ErrEvidence
	}
	// A terminal action can still owe cleanup for an initially unknown claim.
	// Keep its ordinary outcome while binding fully validated later acquisition.
	_, claimKnown := evidence.Claim.Value()
	if cleanup, known := v.DraftCleanup.Value(); !v.Unresolved && known && cleanup.Stage == domain.DraftAwaitingClaim && claimKnown {
		if !evidence.Complete {
			return result, ErrEvidence
		}
		o.Effect = domain.EffectUnknown
		o.UnsuccessfulReason = ""
	}
	next, err := e.draftJournal.ObserveDraft(ctx, v.Plan, o, o.Snapshot, evidence.Claim)
	if err == nil {
		result.Progress = next
	}
	return result, err
}
