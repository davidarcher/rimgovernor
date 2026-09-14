package executor

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type rangedFake struct {
	f                                 *fixture
	claim                             domain.DraftClaim
	inspections, writes, observations int
	rangedEquipped                    bool
	inspect                           func(*RangedInspection)
	attack                            func(context.Context, RangedDispatch) (Receipt, error)
	observe                           func(*RangedEvidence)
	last                              RangedDispatch
}

func (m *rangedFake) InspectRanged(_ context.Context, target Target, claim domain.DraftClaim) (RangedInspection, error) {
	m.inspections++
	if claim != m.claim {
		return RangedInspection{}, ErrEvidence
	}
	now := m.f.clock.Now()
	tick := domain.Tick(101 + m.inspections)
	emergency, err := policy.NewEmergencySnapshot(target.Snapshot, tick+1, policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}, Threats: []policy.EmergencyThreat{{ID: "hostile", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}})
	if err != nil {
		return RangedInspection{}, err
	}
	v := RangedInspection{StartedAt: now, ObservedAt: now, Facts: policy.RangedDefenseFacts{Snapshot: target.Snapshot, PawnTick: tick, PreviewTick: tick, Emergency: emergency, NativeCanTry: domain.Known(true), Pawn: policy.RangedPawnFacts{Pawn: "pawn", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false), HealthFraction: domain.Known(1.0), FreeColonist: domain.Known(true), Drafted: domain.Known(true), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ViolenceCapable: domain.Known(true), RangedWeaponEquipped: domain.Known(m.rangedEquipped), Owner: domain.Known(policy.MeleeDraftOwner{Claim: claim.Claim, Session: claim.Session, Direction: claim.Origin.Direction})}, Target: policy.RangedTargetFacts{Pawn: "hostile", SnapshotToken: "target-cas", Dead: domain.Known(false), Downed: domain.Known(false), Hostile: domain.Known(true)}}}
	if m.inspect != nil {
		m.inspect(&v)
	}
	return v, nil
}
func (m *rangedFake) AttackRanged(ctx context.Context, v RangedDispatch) (Receipt, error) {
	m.writes++
	m.last = v
	if m.attack != nil {
		return m.attack(ctx, v)
	}
	return Receipt{Action: v.Attempt.Action.ID(), Attempt: v.Attempt.Attempt, Snapshot: v.Attempt.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (m *rangedFake) ObserveRanged(_ context.Context, v RangedDispatch, current domain.GenerationSnapshot) (RangedEvidence, error) {
	m.observations++
	m.last = v
	now := m.f.clock.Now()
	e := RangedEvidence{Observation: domain.Observation{Action: v.Attempt.Action.ID(), Attempt: v.Attempt.Attempt, Snapshot: current, Tick: v.Attempt.Tick + 1, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, StartedAt: now, ObservedAt: now, Complete: true, Pawn: "pawn", Target: "hostile"}
	if m.observe != nil {
		m.observe(&e)
	}
	return e, nil
}
func newRangedFixture(t *testing.T) (*fixture, *draftFake, *rangedFake) {
	t.Helper()
	f := newFixture(t)
	draft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("draft-action", draft)
	attack, _ := domain.NewRangedAttack("pawn", "hostile", d.ID())
	a, _ := domain.NewRangedAttackAction("attack-action", attack)
	plan, err := domain.NewPlan("ranged-plan", 1, []domain.Action{d, a})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	f.plan, f.action = plan, a
	f.authority.Snapshot.Plan = plan.ID()
	df := &draftFake{f: f}
	mf := &rangedFake{f: f, rangedEquipped: true}
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
	f.executor, err = NewWithRanged(f.store, f.env, df, mf, f.clock, Limits{time.Second, 2 * time.Second, time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, df, mf
}

func TestRangedAttackDispatchThenCausalCompletion(t *testing.T) {
	f, _, m := newRangedFixture(t)
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

func TestRangedAttackRequiresRangedWeapon(t *testing.T) {
	f, _, m := newRangedFixture(t)
	m.rangedEquipped = false
	r, err := f.run()
	if err != ErrHeld || r.Progress.View().Stage != domain.Pending || m.writes != 0 {
		t.Fatal(r, err)
	}
	if len(r.Refused) != 1 || r.Refused[0].Reason != policy.UnsuitableEquipment {
		t.Fatal(r.Refused)
	}
	held, ok := r.Progress.View().FreshHeldReason()
	if !ok || len(held) != 1 || held[0] != domain.HeldUnsuitableEquipment {
		t.Fatal("ordinary refusal was not persisted as a held reason", held)
	}
}
