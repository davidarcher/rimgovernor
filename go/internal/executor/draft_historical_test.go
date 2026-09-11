package executor

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestTerminalDraftHistoricalClaimPreservesOriginalFailure(t *testing.T) {
	f, d := newDraftFixture(t)
	d.call = func(context.Context, DraftDispatch) (DraftReceipt, error) {
		return DraftReceipt{}, context.DeadlineExceeded
	}
	if _, err := f.run(); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatal(err)
	}
	v := f.progress(t)
	p, err := f.store.ObserveDraft(context.Background(), f.plan.ID(), domain.Observation{Action: v.Action, Attempt: v.Attempt, Snapshot: v.Snapshot, Tick: 101, Causality: domain.AfterDispatch, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure}, v.Snapshot, domain.Unknown[domain.DraftClaim]())
	if err != nil {
		t.Fatal(err)
	}
	before := p.View()
	var change func(*DraftEvidence)
	unavailable := false
	d.cleanup = func(attempt Placement, _ domain.DraftCleanup) (DraftCleanupInspection, error) {
		if unavailable {
			return DraftCleanupInspection{}, ErrHeld
		}
		evidence, err := d.ObserveDraft(context.Background(), attempt, attempt.Snapshot)
		evidence.Observation.Effect = domain.EffectUnsuccessful
		evidence.Observation.UnsuccessfulReason = domain.NativeCancelled
		evidence.Drafted = domain.Known(false)
		if change != nil {
			change(&evidence)
		}
		return DraftCleanupInspection{StartedAt: evidence.StartedAt, ObservedAt: evidence.ObservedAt, Reconcile: &evidence}, err
	}
	unavailable = true
	if _, err := f.executor.CleanupDraft(context.Background(), f.plan.ID(), f.action.ID()); !errors.Is(err, ErrHeld) {
		t.Fatal(err)
	}
	unavailable = false
	for _, modify := range []func(*DraftEvidence){
		func(e *DraftEvidence) { e.Complete = false },
		func(e *DraftEvidence) { e.Observation.UnsuccessfulReason = "bogus" },
		func(e *DraftEvidence) { claim, _ := e.Claim.Value(); claim.Attempt++; e.Claim = domain.Known(claim) },
	} {
		change = modify
		if _, err := f.executor.CleanupDraft(context.Background(), f.plan.ID(), f.action.ID()); err == nil {
			t.Fatal("invalid historical evidence accepted")
		}
		if got := f.progress(t); got != before {
			t.Fatal("invalid evidence changed progress", got)
		}
	}
	change = nil
	result, err := f.executor.CleanupDraft(context.Background(), f.plan.ID(), f.action.ID())
	after := result.Progress.View()
	cleanup, _ := after.DraftCleanup.Value()
	if err != nil || cleanup.Stage != domain.DraftCleanupRequired || after.Stage != before.Stage || after.Effect != before.Effect || after.UnsuccessfulReason != before.UnsuccessfulReason || after.Unresolved != before.Unresolved || d.releases != 0 {
		t.Fatal(after, err)
	}
}
