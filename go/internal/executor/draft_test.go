package executor

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"testing"
	"time"
)

type draftFake struct {
	f                            *fixture
	inspections, calls, releases int
	inspect                      func(*DraftInspection)
	call                         func(context.Context, DraftDispatch) (DraftReceipt, error)
	cleanup                      func(Placement, domain.DraftCleanup) (DraftCleanupInspection, error)
	release                      func(context.Context, domain.DraftRelease) (DraftCleanupReceipt, error)
}

func (d *draftFake) InspectDraft(_ context.Context, t Target) (DraftInspection, error) {
	d.inspections++
	now := d.f.clock.Now()
	emergency, err := policy.NewEmergencySnapshot(t.Snapshot, 101, policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}})
	if err != nil {
		return DraftInspection{}, err
	}
	v := DraftInspection{Current: t.Snapshot, Tick: 100, StartedAt: now, ObservedAt: now, PawnSnapshotToken: "token", Emergency: emergency, Pawn: policy.DraftPawnFacts{Pawn: "pawn", Drafted: domain.Known(false), Unowned: domain.Known(true), NativeCanTry: domain.Known(true)}}
	if d.inspect != nil {
		d.inspect(&v)
	}
	return v, nil
}
func (d *draftFake) claim(p Placement) domain.DraftClaim {
	id, _ := d.f.store.Identity(context.Background())
	return domain.DraftClaim{Action: p.Action.ID(), Attempt: p.Attempt, Pawn: "pawn", Claim: "claim", Session: domain.ControllerSessionID(id), Origin: p.Snapshot}
}
func (d *draftFake) Draft(ctx context.Context, p DraftDispatch) (DraftReceipt, error) {
	d.calls++
	if d.call != nil {
		return d.call(ctx, p)
	}
	return DraftReceipt{Receipt: Receipt{p.Attempt.Action.ID(), p.Attempt.Attempt, p.Attempt.Snapshot, domain.ReceiptAccepted}, Claim: domain.Known(d.claim(p.Attempt))}, nil
}
func (d *draftFake) ObserveDraft(_ context.Context, p Placement, current domain.GenerationSnapshot) (DraftEvidence, error) {
	now := d.f.clock.Now()
	return DraftEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: 101, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}, StartedAt: now, ObservedAt: now, Complete: true, Pawn: "pawn", Drafted: domain.Known(true), Claim: domain.Known(d.claim(p))}, nil
}
func (d *draftFake) InspectDraftCleanup(_ context.Context, p Placement, c domain.DraftCleanup) (DraftCleanupInspection, error) {
	if d.cleanup != nil {
		return d.cleanup(p, c)
	}
	claim, _ := c.Claim.Value()
	now := d.f.clock.Now()
	return DraftCleanupInspection{StartedAt: now, ObservedAt: now, Request: &domain.DraftReleaseRequest{Claim: claim, PawnSnapshotToken: "fresh-token", Observed: p.Snapshot, Tick: 102}}, nil
}
func (d *draftFake) ReleaseDraft(ctx context.Context, r domain.DraftRelease) (DraftCleanupReceipt, error) {
	d.releases++
	if d.release != nil {
		return d.release(ctx, r)
	}
	return DraftCleanupReceipt{r, domain.DraftReleaseConfirmed}, nil
}
func newDraftFixture(t *testing.T) (*fixture, *draftFake) {
	f := newFixture(t)
	intent, _ := domain.NewOwnedDraft("pawn")
	action, _ := domain.NewOwnedDraftAction("draft-action", intent)
	plan, _ := domain.NewPlan("draft-plan", 1, []domain.Action{action})
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	f.action = action
	f.plan = plan
	f.authority.Snapshot.Plan = plan.ID()
	d := &draftFake{f: f}
	var err error
	f.executor, err = NewWithDraft(f.store, f.env, d, f.clock, Limits{time.Second, 2 * time.Second, time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, d
}
func TestDraftCompletionAndCleanupAfterStop(t *testing.T) {
	f, d := newDraftFixture(t)
	r, err := f.run()
	if err != nil || d.calls != 1 || d.inspections != 2 || !r.Progress.View().Unresolved {
		t.Fatal(r, err, d)
	}
	if d.f.env.placements != 0 {
		t.Fatal("building writer used")
	}
	r, err = f.run()
	if err != nil || r.Progress.View().Stage != domain.Completed {
		t.Fatal(r, err)
	}
	if err = f.executor.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
	r, err = f.executor.CleanupDraft(context.Background(), f.plan.ID(), f.action.ID())
	c, _ := r.Progress.View().DraftCleanup.Value()
	if err != nil || c.Stage != domain.DraftReleased || d.releases != 1 {
		t.Fatal(r, err)
	}
	if _, err = f.run(); !errors.Is(err, ErrStopped) {
		t.Fatal(err)
	}
}
func TestDraftSecondInspectionHoldsWithoutDispatch(t *testing.T) {
	f, d := newDraftFixture(t)
	d.inspect = func(v *DraftInspection) {
		if d.inspections == 2 {
			v.Pawn.NativeCanTry = domain.Known(false)
		}
	}
	r, err := f.run()
	if !errors.Is(err, ErrHeld) || d.calls != 0 || r.Progress.View().Stage != domain.Prepared {
		t.Fatal(r, err)
	}
}
func TestDraftEmergencyBlocksDispatchAndPersistsHold(t *testing.T) {
	f, d := newDraftFixture(t)
	d.inspect = func(v *DraftInspection) {
		v.Emergency, _ = policy.NewEmergencySnapshot(v.Current, v.Tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(true), Bleeding: domain.Known(true), NeedsTend: domain.Known(true)}}})
	}
	r, err := f.run()
	if !errors.Is(err, ErrHeld) || d.calls != 0 {
		t.Fatal(r, err)
	}
	held, ok := f.progress(t).FreshHeldReason()
	if !ok || len(held) != 1 || held[0] != domain.HeldCriticalMedical {
		t.Fatal("emergency hold was not persisted as a held reason", held)
	}
}

// A colony-wide threat unrelated to the pawn being drafted must never block
// drafting a healthy, uninvolved pawn -- that is exactly what a player wants
// to do in a fight, not something to hold on. This is the reason draft's
// emergency handling cannot simply mirror the other action families'
// unconditional EvaluateEmergency gate.
func TestDraftIgnoresUnrelatedThreatForHealthyPawn(t *testing.T) {
	f, d := newDraftFixture(t)
	d.inspect = func(v *DraftInspection) {
		v.Emergency, _ = policy.NewEmergencySnapshot(v.Current, v.Tick, policy.EmergencyFacts{
			ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true),
			Colonists: []policy.EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}},
			Threats:   []policy.EmergencyThreat{{ID: "hostile", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}},
		})
	}
	r, err := f.run()
	if err != nil || d.calls != 1 {
		t.Fatal(r, err, d)
	}
}
func TestDraftCancellationJournalsUncertaintyAndSerializesCleanup(t *testing.T) {
	f, d := newDraftFixture(t)
	entered := make(chan struct{})
	finish := make(chan struct{})
	d.call = func(ctx context.Context, _ DraftDispatch) (DraftReceipt, error) {
		close(entered)
		<-ctx.Done()
		<-finish
		return DraftReceipt{}, ctx.Err()
	}
	done := make(chan error, 1)
	go func() { _, err := f.run(); done <- err }()
	<-entered
	if _, err := f.executor.Cancel(context.Background(), f.plan.ID(), f.action.ID()); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	if _, err := f.executor.CleanupDraft(ctx, f.plan.ID(), f.action.ID()); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatal(err)
	}
	close(finish)
	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	v := f.progress(t)
	c, _ := v.DraftCleanup.Value()
	if v.Stage != domain.Cancelled || !v.Unresolved || c.Stage != domain.DraftAwaitingClaim || d.releases != 0 {
		t.Fatal(v)
	}
	// Positive replacement does not invent ordinary completion or a claim.
	d.cleanup = func(p Placement, _ domain.DraftCleanup) (DraftCleanupInspection, error) {
		now := f.clock.Now()
		world := p.Snapshot
		world.Load = "replacement"
		world.Native = 0
		return DraftCleanupInspection{StartedAt: now, ObservedAt: now, ScopeSupersession: &domain.DraftScopeSupersession{Action: p.Action.ID(), Attempt: p.Attempt, Origin: p.Snapshot, Observed: world, Tick: 0}}, nil
	}
	r, err := f.executor.CleanupDraft(context.Background(), f.plan.ID(), f.action.ID())
	c, _ = r.Progress.View().DraftCleanup.Value()
	if err != nil || c.Stage != domain.DraftSuperseded || !r.Progress.View().Unresolved {
		t.Fatal(r, err)
	}
}
func TestDraftRestartDispatchedCleanupRequiresFreshReadAndRejectsLateSequence(t *testing.T) {
	f, d := newDraftFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	state, _ := f.store.LoadPlan(context.Background(), f.plan.ID())
	p := state.Progress[0]
	c, _ := p.View().DraftCleanup.Value()
	claim, _ := c.Claim.Value()
	p, err := f.store.BeginDraftCleanup(context.Background(), f.plan.ID(), f.action.ID(), domain.DraftReleaseRequest{Claim: claim, PawnSnapshotToken: "old", Observed: p.View().Snapshot, Tick: 101})
	if err != nil {
		t.Fatal(err)
	}
	c, _ = p.View().DraftCleanup.Value()
	old, _ := c.Release.Value()
	if err := f.store.Close(); err != nil {
		t.Fatal(err)
	}
	f.store, err = store.Open(context.Background(), f.path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = f.store.Close() })
	// A new executor has no authority; durable pending cleanup still reconciles.
	f.executor, err = NewWithDraft(f.store, f.env, d, f.clock, Limits{time.Second, time.Second, time.Second})
	if err != nil {
		t.Fatal(err)
	}
	d.cleanup = func(_ Placement, c domain.DraftCleanup) (DraftCleanupInspection, error) {
		if c.Stage != domain.DraftCleanupUncertain {
			t.Fatal(c)
		}
		return DraftCleanupInspection{}, ErrHeld
	}
	if _, err = f.executor.CleanupDraft(context.Background(), f.plan.ID(), f.action.ID()); !errors.Is(err, ErrHeld) || d.releases != 0 {
		t.Fatal(err)
	}
	d.cleanup = nil
	d.release = func(_ context.Context, r domain.DraftRelease) (DraftCleanupReceipt, error) {
		if r.Sequence != old.Sequence+1 {
			t.Fatal(r)
		}
		return DraftCleanupReceipt{old, domain.DraftReleaseConfirmed}, nil
	}
	r, err := f.executor.CleanupDraft(context.Background(), f.plan.ID(), f.action.ID())
	c, _ = r.Progress.View().DraftCleanup.Value()
	if !errors.Is(err, ErrEvidence) || c.Stage != domain.DraftCleanupUncertain {
		t.Fatal(r, err)
	}
}

func TestDraftCleanupRejectsAmbiguousAndWrongActionEvidence(t *testing.T) {
	f, d := newDraftFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	for _, ambiguous := range []bool{false, true} {
		d.cleanup = func(p Placement, c domain.DraftCleanup) (DraftCleanupInspection, error) {
			now := f.clock.Now()
			world := p.Snapshot
			world.Load = "replacement"
			value := DraftCleanupInspection{StartedAt: now, ObservedAt: now, ScopeSupersession: &domain.DraftScopeSupersession{Action: "another-action", Attempt: p.Attempt, Origin: p.Snapshot, Observed: world, Tick: 0}}
			if ambiguous {
				claim, _ := c.Claim.Value()
				value.Request = &domain.DraftReleaseRequest{Claim: claim, PawnSnapshotToken: "token", Observed: p.Snapshot, Tick: 102}
			}
			return value, nil
		}
		if _, err := f.executor.CleanupDraft(context.Background(), f.plan.ID(), f.action.ID()); !errors.Is(err, ErrEvidence) {
			t.Fatal(err)
		}
	}
	c, _ := f.progress(t).DraftCleanup.Value()
	if c.Stage != domain.DraftCleanupRequired || d.releases != 0 {
		t.Fatal(c)
	}
}

func TestDraftUnknownReceiptReopensAndBindsClaimWhileDisabled(t *testing.T) {
	f, d := newDraftFixture(t)
	d.call = func(context.Context, DraftDispatch) (DraftReceipt, error) {
		return DraftReceipt{}, context.DeadlineExceeded
	}
	if _, err := f.run(); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatal(err)
	}
	if err := f.store.Close(); err != nil {
		t.Fatal(err)
	}
	var err error
	f.store, err = store.Open(context.Background(), f.path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = f.store.Close() })
	f.executor, err = NewWithDraft(f.store, f.env, d, f.clock, Limits{time.Second, time.Second, time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.executor.UpdateAuthority(Authority{Snapshot: f.authority.Snapshot}); err != nil {
		t.Fatal(err)
	}
	result, err := f.run()
	c, _ := result.Progress.View().DraftCleanup.Value()
	if err != nil || result.Progress.View().Stage != domain.Completed || c.Stage != domain.DraftCleanupRequired || d.calls != 1 {
		t.Fatal(result, err)
	}
}

func TestDraftTerminalLateAcquisitionBindsCleanupWithoutChangingOutcome(t *testing.T) {
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
	if p.View().Stage != domain.Unsuccessful || p.View().Unresolved {
		t.Fatal(p.View())
	}
	complete := false
	d.cleanup = func(attempt Placement, _ domain.DraftCleanup) (DraftCleanupInspection, error) {
		evidence, err := d.ObserveDraft(context.Background(), attempt, attempt.Snapshot)
		evidence.Complete = complete
		return DraftCleanupInspection{StartedAt: evidence.StartedAt, ObservedAt: evidence.ObservedAt, Reconcile: &evidence}, err
	}
	if _, err := f.executor.CleanupDraft(context.Background(), f.plan.ID(), f.action.ID()); !errors.Is(err, ErrEvidence) {
		t.Fatal(err)
	}
	complete = true
	r, err := f.executor.CleanupDraft(context.Background(), f.plan.ID(), f.action.ID())
	c, _ := r.Progress.View().DraftCleanup.Value()
	if err != nil || c.Stage != domain.DraftCleanupRequired || r.Progress.View().Stage != domain.Unsuccessful || r.Progress.View().Unresolved || d.releases != 0 {
		t.Fatal(r, err)
	}
	d.cleanup = nil
	r, err = f.executor.CleanupDraft(context.Background(), f.plan.ID(), f.action.ID())
	c, _ = r.Progress.View().DraftCleanup.Value()
	if err != nil || c.Stage != domain.DraftReleased || r.Progress.View().Stage != domain.Unsuccessful || r.Progress.View().Unresolved {
		t.Fatal(r, err)
	}
}

func TestDraftCancelledUnresolvedCompletionPreservesCancellation(t *testing.T) {
	f, _ := newDraftFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	if _, err := f.executor.Cancel(context.Background(), f.plan.ID(), f.action.ID()); err != nil {
		t.Fatal(err)
	}
	r, err := f.run()
	effect, known := r.Progress.View().Effect.Value()
	if err != nil || r.Progress.View().Stage != domain.Cancelled || r.Progress.View().Unresolved || !known || effect != domain.EffectCompleted {
		t.Fatal(r, err)
	}
}
