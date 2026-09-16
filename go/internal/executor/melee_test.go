package executor

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type meleeFake struct {
	f                                 *fixture
	claim                             domain.DraftClaim
	inspections, writes, observations int
	inspect                           func(*MeleeInspection)
	attack                            func(context.Context, MeleeDispatch) (Receipt, error)
	observe                           func(*MeleeEvidence)
	last                              MeleeDispatch
}

func (m *meleeFake) InspectMelee(_ context.Context, target Target, claim domain.DraftClaim) (MeleeInspection, error) {
	m.inspections++
	if claim != m.claim {
		return MeleeInspection{}, ErrEvidence
	}
	now := m.f.clock.Now()
	tick := domain.Tick(101 + m.inspections)
	emergency, err := policy.NewEmergencySnapshot(target.Snapshot, tick+1, policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}, Threats: []policy.EmergencyThreat{{ID: "hostile", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}})
	if err != nil {
		return MeleeInspection{}, err
	}
	v := MeleeInspection{StartedAt: now, ObservedAt: now, Facts: policy.MeleeDefenseFacts{Snapshot: target.Snapshot, PawnTick: tick, PreviewTick: tick, Emergency: emergency, NativeCanTry: domain.Known(true), Pawn: policy.MeleePawnFacts{Pawn: "pawn", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false), HealthFraction: domain.Known(1.0), FreeColonist: domain.Known(true), Drafted: domain.Known(true), ViolenceCapable: domain.Known(true), EquipmentKnown: domain.Known(true), Owner: domain.Known(policy.MeleeDraftOwner{Claim: claim.Claim, Session: claim.Session, Direction: claim.Origin.Direction})}, Target: policy.MeleeTargetFacts{Pawn: "hostile", SnapshotToken: "target-cas", Dead: domain.Known(false), Downed: domain.Known(false), Hostile: domain.Known(true)}}}
	if m.inspect != nil {
		m.inspect(&v)
	}
	return v, nil
}
func (m *meleeFake) AttackMelee(ctx context.Context, v MeleeDispatch) (Receipt, error) {
	m.writes++
	m.last = v
	if m.attack != nil {
		return m.attack(ctx, v)
	}
	return Receipt{Action: v.Attempt.Action.ID(), Attempt: v.Attempt.Attempt, Snapshot: v.Attempt.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (m *meleeFake) ObserveMelee(_ context.Context, v MeleeDispatch, current domain.GenerationSnapshot) (MeleeEvidence, error) {
	m.observations++
	m.last = v
	now := m.f.clock.Now()
	e := MeleeEvidence{Observation: domain.Observation{Action: v.Attempt.Action.ID(), Attempt: v.Attempt.Attempt, Snapshot: current, Tick: v.Attempt.Tick + 1, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, StartedAt: now, ObservedAt: now, Complete: true, Pawn: "pawn", Target: "hostile"}
	if m.observe != nil {
		m.observe(&e)
	}
	return e, nil
}
func newMeleeFixture(t *testing.T) (*fixture, *draftFake, *meleeFake) {
	t.Helper()
	f := newFixture(t)
	draft, _ := domain.NewOwnedDraft("pawn")
	d, _ := domain.NewOwnedDraftAction("draft-action", draft)
	attack, _ := domain.NewMeleeAttack("pawn", "hostile", d.ID())
	a, _ := domain.NewMeleeAttackAction("attack-action", attack)
	plan, err := domain.NewPlan("melee-plan", 1, []domain.Action{d, a})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	f.plan, f.action = plan, a
	f.authority.Snapshot.Plan = plan.ID()
	df := &draftFake{f: f}
	mf := &meleeFake{f: f}
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
	f.executor, err = NewWithMelee(f.store, f.env, df, mf, f.clock, Limits{time.Second, 2 * time.Second, time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, df, mf
}

func TestMeleeDispatchThenDisabledCausalCompletion(t *testing.T) {
	f, _, m := newMeleeFixture(t)
	r, err := f.run()
	if err != nil || m.inspections != 2 || m.writes != 1 || !r.Progress.View().Unresolved {
		t.Fatal(r, err, m)
	}
	if m.last.Admission.DraftClaim != m.claim || m.last.Admission.Tick != 103 {
		t.Fatal(m.last)
	}
	// Restart loses all executor memory, then Manual has advanced native authority.
	f.executor, err = NewWithMelee(f.store, f.env, &draftFake{f: f}, m, f.clock, Limits{time.Second, 2 * time.Second, time.Second})
	if err != nil {
		t.Fatal(err)
	}
	current := f.authority.Snapshot
	current.Native++
	if err = f.executor.UpdateAuthority(Authority{Snapshot: current}); err != nil {
		t.Fatal(err)
	}
	request := domain.DraftReleaseRequest{Claim: m.claim, PawnSnapshotToken: "release", Observed: current, Tick: 104}
	p, err := f.store.BeginDraftCleanup(context.Background(), f.plan.ID(), "draft-action", request)
	if err != nil {
		t.Fatal(err)
	}
	cleanup, _ := p.View().DraftCleanup.Value()
	release, _ := cleanup.Release.Value()
	if _, err = f.store.RecordDraftCleanup(context.Background(), f.plan.ID(), "draft-action", release, domain.DraftReleaseConfirmed); err != nil {
		t.Fatal(err)
	}
	r, err = f.run()
	if err != nil || r.Progress.View().Stage != domain.Completed || m.writes != 1 || m.inspections != 2 || m.observations != 1 {
		t.Fatal(r, err)
	}
	if m.last.Admission.DraftClaim != m.claim || m.last.Attempt.Snapshot == current {
		t.Fatal("original attribution changed", m.last)
	}
}

func TestMeleeSecondInspectionAndCleanupRaceHold(t *testing.T) {
	for _, mode := range []string{"stale-time", "cleanup"} {
		t.Run(mode, func(t *testing.T) {
			f, _, m := newMeleeFixture(t)
			m.inspect = func(v *MeleeInspection) {
				if m.inspections != 2 {
					return
				}
				switch mode {
				case "stale-time":
					v.StartedAt = v.StartedAt.Add(-2 * time.Second)
				case "cleanup":
					_, err := f.store.BeginDraftCleanup(context.Background(), f.plan.ID(), "draft-action", domain.DraftReleaseRequest{Claim: m.claim, PawnSnapshotToken: "release", Observed: f.authority.Snapshot, Tick: 102})
					if err != nil {
						t.Fatal(err)
					}
				}
			}
			r, err := f.run()
			if err == nil || m.writes != 0 || r.Progress.View().Attempt != 0 {
				t.Fatal(r, err)
			}
		})
	}
}

func TestMeleeUnknownNeverRedispatchesAndRejectsMalformedCompletion(t *testing.T) {
	f, _, m := newMeleeFixture(t)
	m.attack = func(context.Context, MeleeDispatch) (Receipt, error) { return Receipt{}, errors.New("lost reply") }
	r, err := f.run()
	if err == nil || !r.Progress.View().Unresolved {
		t.Fatal(r, err)
	}
	for _, mode := range []string{"unknown", "incomplete", "wrong-target", "wrong-attempt", "no-causality"} {
		m.observe = func(e *MeleeEvidence) {
			switch mode {
			case "unknown":
				e.Observation.Effect = domain.EffectUnknown
				e.Complete = false
			case "incomplete":
				e.Complete = false
			case "wrong-target":
				e.Target = "unrelated"
			case "wrong-attempt":
				e.Observation.Attempt++
			case "no-causality":
				e.Observation.Causality = ""
			}
		}
		r, err = f.run()
		if mode == "unknown" && err != nil {
			t.Fatal(err)
		}
		if mode != "unknown" && err == nil {
			t.Fatal("invalid effect accepted", mode)
		}
		if !r.Progress.View().Unresolved || m.writes != 1 {
			t.Fatal(r, err)
		}
	}
}

func TestMeleeStopJoinsWriteJournalThenDraftCleanup(t *testing.T) {
	f, d, m := newMeleeFixture(t)
	entered := make(chan struct{})
	release := make(chan struct{})
	finished := make(chan error, 1)
	m.attack = func(ctx context.Context, _ MeleeDispatch) (Receipt, error) {
		close(entered)
		<-ctx.Done()
		<-release
		return Receipt{}, ctx.Err()
	}
	go func() { _, err := f.run(); finished <- err }()
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("no attack")
	}
	stopped := make(chan error, 1)
	go func() { stopped <- f.executor.Stop(context.Background()) }()
	select {
	case err := <-stopped:
		t.Fatal("stop failed to join write", err)
	case <-time.After(20 * time.Millisecond):
	}
	close(release)
	if err := <-finished; err == nil {
		t.Fatal("cancelled attack succeeded")
	}
	if err := <-stopped; err != nil {
		t.Fatal(err)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	kind, known := state.Progress[1].View().Receipt.Value()
	if !known || kind != domain.ReceiptUnknown || !state.Progress[1].View().Unresolved {
		t.Fatal(state.Progress[1])
	}
	if _, err = f.executor.CleanupDraft(context.Background(), f.plan.ID(), "draft-action"); err != nil || d.releases != 1 {
		t.Fatal(err)
	}
	if _, err = f.run(); !errors.Is(err, ErrStopped) {
		t.Fatal(err)
	}
}

func TestMeleeCancelledIntentSurvivesLaterCompletion(t *testing.T) {
	f, _, m := newMeleeFixture(t)
	if _, err := f.run(); err != nil {
		t.Fatal(err)
	}
	if _, err := f.executor.Cancel(context.Background(), f.plan.ID(), f.action.ID()); err != nil {
		t.Fatal(err)
	}
	r, err := f.run()
	effect, known := r.Progress.View().Effect.Value()
	if err != nil || r.Progress.View().Stage != domain.Cancelled || r.Progress.View().Unresolved || !known || effect != domain.EffectCompleted || m.writes != 1 {
		t.Fatal(r, err)
	}
}

func TestMeleeRequiresExplicitCapability(t *testing.T) {
	f, d, m := newMeleeFixture(t)
	if _, err := NewWithMelee(f.store, f.env, d, nil, f.clock, Limits{time.Second, time.Second, time.Second}); err == nil {
		t.Fatal("missing capability accepted")
	}
	var err error
	f.executor, err = NewWithDraft(f.store, f.env, d, f.clock, Limits{time.Second, time.Second, time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	if _, err = f.run(); err == nil || m.writes != 0 {
		t.Fatal("unconfigured melee handler ran", err)
	}
}
