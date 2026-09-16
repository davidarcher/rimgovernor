package executor

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type movementFake struct {
	f                                 *fixture
	claim                             domain.DraftClaim
	inspections, writes, observations int
	freeColonist                      bool
	inspect                           func(*MovementInspection)
	move                              func(context.Context, MovementDispatch) (Receipt, error)
	observe                           func(*MovementEvidence)
	last                              MovementDispatch
}

func (m *movementFake) InspectMovement(_ context.Context, target Target, claim domain.DraftClaim) (MovementInspection, error) {
	m.inspections++
	if claim != m.claim {
		return MovementInspection{}, ErrEvidence
	}
	now := m.f.clock.Now()
	tick := domain.Tick(101 + m.inspections)
	emergency, err := policy.NewEmergencySnapshot(target.Snapshot, tick+1, policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}})
	if err != nil {
		return MovementInspection{}, err
	}
	v := MovementInspection{StartedAt: now, ObservedAt: now, Facts: policy.MovementFacts{Snapshot: target.Snapshot, PawnTick: tick, PreviewTick: tick, Emergency: emergency, NativeCanTry: domain.Known(true), Pawn: policy.MovementPawnFacts{Pawn: "pawn", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false), FreeColonist: domain.Known(m.freeColonist), Drafted: domain.Known(true), Owner: domain.Known(policy.MovementDraftOwner{Claim: claim.Claim, Session: claim.Session, Direction: claim.Origin.Direction})}}}
	if m.inspect != nil {
		m.inspect(&v)
	}
	return v, nil
}
func (m *movementFake) MoveTo(ctx context.Context, v MovementDispatch) (Receipt, error) {
	m.writes++
	m.last = v
	if m.move != nil {
		return m.move(ctx, v)
	}
	return Receipt{Action: v.Attempt.Action.ID(), Attempt: v.Attempt.Attempt, Snapshot: v.Attempt.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (m *movementFake) ObserveMovement(_ context.Context, v MovementDispatch, current domain.GenerationSnapshot) (MovementEvidence, error) {
	m.observations++
	m.last = v
	now := m.f.clock.Now()
	e := MovementEvidence{Observation: domain.Observation{Action: v.Attempt.Action.ID(), Attempt: v.Attempt.Attempt, Snapshot: current, Tick: v.Attempt.Tick + 1, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, StartedAt: now, ObservedAt: now, Complete: true, Pawn: "pawn"}
	if m.observe != nil {
		m.observe(&e)
	}
	return e, nil
}
func newMovementFixture(t *testing.T) (*fixture, *draftFake, *movementFake) {
	t.Helper()
	f := newFixture(t)
	draft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("draft-action", draft)
	move, _ := domain.NewMovement("pawn", domain.Cell{X: 3, Z: 4}, d.ID())
	a, _ := domain.NewMovementAction("move-action", move)
	plan, err := domain.NewPlan("movement-plan", 1, []domain.Action{d, a})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	f.plan, f.action = plan, a
	f.authority.Snapshot.Plan = plan.ID()
	df := &draftFake{f: f}
	mf := &movementFake{f: f, freeColonist: true}
	s := f.authority.Snapshot
	ctx := context.Background()
	if _, err = f.store.PrepareDraft(ctx, plan.ID(), d.ID(), store.DraftAdmission{Snapshot: s, Tick: 100, Pawn: "pawn", PawnSnapshotToken: "draft-cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = f.store.Dispatch(ctx, plan.ID(), d.ID(), s, 100); err != nil {
		t.Fatal(err)
	}
	mf.claim = df.claim(Placement{d, 1, s, 100})
	if _, err = f.store.ObserveDraft(ctx, plan.ID(), domain.Observation{Action: d.ID(), Attempt: 1, Snapshot: s, Tick: 101, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, s, domain.Known(mf.claim)); err != nil {
		t.Fatal(err)
	}
	f.executor, err = NewWithMovement(f.store, f.env, df, mf, f.clock, Limits{time.Second, 2 * time.Second, time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, df, mf
}

func TestMovementDispatchThenCausalCompletion(t *testing.T) {
	f, _, m := newMovementFixture(t)
	r, err := f.run()
	if err != nil || m.inspections != 2 || m.writes != 1 || !r.Progress.View().Unresolved {
		t.Fatal(r, err, m)
	}
	if m.last.Admission.DraftClaim != m.claim {
		t.Fatal(m.last)
	}
	r, err = f.run()
	if err != nil || r.Progress.View().Stage != domain.Completed || m.observations != 1 {
		t.Fatal(r, err)
	}
}

func TestMovementRequiresFreeColonist(t *testing.T) {
	f, _, m := newMovementFixture(t)
	m.freeColonist = false
	r, err := f.run()
	if err != ErrHeld || r.Progress.View().Stage != domain.Pending || m.writes != 0 {
		t.Fatal(r, err)
	}
	if len(r.Refused) != 1 || r.Refused[0].Reason != policy.NativeIneligible {
		t.Fatal(r.Refused)
	}
}
