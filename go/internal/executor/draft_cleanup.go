package executor

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CleanupDraft consumes one fresh cleanup decision. It shares the ordinary writer
// but does not require live draft permission, including after Stop has joined it.
func (e *Executor) CleanupDraft(ctx context.Context, plan domain.PlanID, action domain.ActionID) (Result, error) {
	if e.draft == nil {
		return Result{}, ErrEvidence
	}
	ctx, cancel := context.WithTimeout(ctx, e.limits.RunTimeout)
	defer cancel()
	select {
	case e.writer <- struct{}{}:
		defer func() { <-e.writer }()
	case <-ctx.Done():
		return Result{}, ctx.Err()
	}
	if err := ctx.Err(); err != nil {
		return Result{}, err
	}
	state, err := e.journal.LoadPlan(ctx, plan)
	if err != nil {
		return Result{}, err
	}
	var result Result
	for _, p := range state.Progress {
		if p.View().Action == action {
			result.Progress = p
			break
		}
	}
	if result.Progress.Action().Kind() != domain.OwnedDraftAction {
		return result, ErrEvidence
	}
	cleanup, known := result.Progress.View().DraftCleanup.Value()
	if !known {
		return result, nil
	}
	switch cleanup.Stage {
	case domain.DraftNotAcquired, domain.DraftReleased, domain.DraftSuperseded:
		return result, nil
	}
	if cleanup.Stage == domain.DraftCleanupDispatched {
		release, known := cleanup.Release.Value()
		if !known {
			return result, ErrEvidence
		}
		result, err = e.recordCleanup(result, release, domain.DraftReleaseUncertain, nil)
		if err != nil {
			return result, err
		}
		cleanup, _ = result.Progress.View().DraftCleanup.Value()
	}
	inspection, err := e.draft.InspectDraftCleanup(ctx, draftAttempt(result.Progress.Action(), result.Progress), cleanup)
	if err != nil {
		return result, err
	}
	if err = ctx.Err(); err != nil {
		return result, err
	}
	if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
		return result, ErrHeld
	}
	arms := 0
	if inspection.Reconcile != nil {
		arms++
	}
	if inspection.ScopeSupersession != nil {
		arms++
	}
	if inspection.Supersession != nil {
		arms++
	}
	if inspection.Request != nil {
		arms++
	}
	if arms != 1 {
		return result, ErrEvidence
	}
	var next domain.Progress
	switch {
	case inspection.Reconcile != nil:
		return e.observeDraft(ctx, result, *inspection.Reconcile)
	case inspection.ScopeSupersession != nil:
		if _, err := result.Progress.ObserveDraftScopeSupersession(*inspection.ScopeSupersession); err != nil {
			return result, ErrEvidence
		}
		next, err = e.draftJournal.ObserveDraftScopeSupersession(ctx, plan, *inspection.ScopeSupersession)
	case inspection.Supersession != nil:
		next, err = e.draftJournal.ObserveDraftCleanup(ctx, plan, action, *inspection.Supersession)
	case inspection.Request != nil:
		next, err = e.draftJournal.BeginDraftCleanup(ctx, plan, action, *inspection.Request)
		if err != nil {
			return result, err
		}
		result.Progress = next
		saved, known := next.View().DraftCleanup.Value()
		release, releaseKnown := saved.Release.Value()
		if !known || !releaseKnown || release.Request != *inspection.Request || release.Sequence == 0 {
			return result, ErrEvidence
		}
		if err = ctx.Err(); err != nil {
			return e.recordCleanup(result, release, domain.DraftReleaseUncertain, err)
		}
		if !e.fresh(inspection.StartedAt, inspection.ObservedAt) {
			return e.recordCleanup(result, release, domain.DraftReleaseUncertain, ErrHeld)
		}
		result.NativeCalled = true
		receipt, callErr := e.draft.ReleaseDraft(ctx, release)
		outcome := receipt.Outcome
		if callErr != nil {
			outcome = domain.DraftReleaseUncertain
		} else if receipt.Release != release || (outcome != domain.DraftReleaseConfirmed && outcome != domain.DraftReleaseUncertain) {
			callErr = ErrEvidence
			outcome = domain.DraftReleaseUncertain
		}
		return e.recordCleanup(result, release, outcome, errors.Join(callErr, ctx.Err()))
	}
	if err == nil {
		result.Progress = next
	}
	return result, err
}
func (e *Executor) recordCleanup(result Result, release domain.DraftRelease, outcome domain.DraftCleanupOutcome, cause error) (Result, error) {
	ctx, cancel := context.WithTimeout(context.Background(), e.limits.JournalTimeout)
	defer cancel()
	v := result.Progress.View()
	next, err := e.draftJournal.RecordDraftCleanup(ctx, v.Plan, v.Action, release, outcome)
	if err == nil {
		result.Progress = next
	}
	return result, errors.Join(cause, err)
}
