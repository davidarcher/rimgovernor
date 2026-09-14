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

type caravanDepartureEnvironment struct {
	*environment
	inspected, dispatched, observed int
	uncertain, ineligible, foreign  bool
	effect                          domain.Effect
}

func (n *caravanDepartureEnvironment) caravanDepartureFacts(target Target) policy.CaravanDepartureFacts {
	departure, _ := target.Action.CaravanDeparture()
	crew := make([]policy.CaravanCrewFacts, 0, len(departure.Crew()))
	for _, pawn := range departure.Crew() {
		crew = append(crew, policy.CaravanCrewFacts{Pawn: pawn, SnapshotToken: "crew-token-" + string(pawn), Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0))})
	}
	return policy.CaravanDepartureFacts{Snapshot: target.Snapshot, PawnTick: n.tick, PreviewTick: n.tick, Crew: crew, CatalogToken: "catalog-token", RemainingHomeColonists: domain.Known(uint32(5)), HomeDoctorAvailable: domain.Known(true), HomeFoodRunwayDays: domain.Known(30.0), RouteReachable: domain.Known(true), RouteTemperatureC: domain.Known(15.0), RouteHostile: domain.Known(false), RouteFactionID: "faction-1", RouteGoodwill: domain.Known(int32(0)), RouteFoodRotDays: domain.Known(9.0), NativeCanTry: domain.Known(!n.ineligible)}
}
func (n *caravanDepartureEnvironment) InspectCaravanDeparture(_ context.Context, target Target) (CaravanDepartureInspection, error) {
	n.inspected++
	now := n.clock.Now()
	return CaravanDepartureInspection{StartedAt: now, ObservedAt: now, Facts: n.caravanDepartureFacts(target), Policy: policy.CaravanDeparturePolicy{MinimumHomeColonists: 1, MinimumHomeFoodDays: 1, KeepHomeDoctor: false, MinimumDestinationTemperatureC: -10, MaximumDestinationTemperatureC: 40, MinimumGoodwill: -50}}, nil
}
func (n *caravanDepartureEnvironment) DepartCaravan(_ context.Context, dispatch CaravanDepartureDispatch) (Receipt, error) {
	n.dispatched++
	if n.uncertain {
		return Receipt{}, errors.New("reply lost")
	}
	p := dispatch.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *caravanDepartureEnvironment) ObserveCaravanDeparture(_ context.Context, dispatch CaravanDepartureDispatch, current domain.GenerationSnapshot) (CaravanDepartureEvidence, error) {
	n.observed++
	p := dispatch.Attempt
	departure, _ := p.Action.CaravanDeparture()
	crew := departure.Crew()
	if n.foreign {
		crew = []domain.PawnID{"foreign-pawn"}
	}
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return CaravanDepartureEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Crew: crew, CaravanID: "caravan-1"}, nil
}

func caravanDepartureFixture(t *testing.T) (*fixture, *caravanDepartureEnvironment) {
	t.Helper()
	f := newFixture(t)
	departure, err := domain.NewCaravanDeparture([]domain.PawnID{"alpha", "beta"}, []domain.CargoItem{{Definition: "MealSimple", Count: 10}}, 42)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewCaravanDepartureAction("caravan-departure-1", departure)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("caravan-departures", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &caravanDepartureEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableCaravanDeparture(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

func TestCaravanDepartureAdmitsAndDispatches(t *testing.T) {
	f, n := caravanDepartureFixture(t)
	result, err := f.run()
	if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	state, err := f.store.LoadPlan(context.Background(), f.plan.ID())
	if err != nil || len(state.CaravanDepartureAdmissions) != 1 || state.Spec.Actions()[0] != f.action {
		t.Fatal(state, err)
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err, n.dispatched)
	}
	// A completed FormCaravan must begin caravan-journey tracking in the same
	// commit as the completion observation; see store.ObserveCaravanDeparture.
	active, err := f.store.ListActiveCaravanTracking(context.Background())
	if err != nil || len(active) != 1 || active[0].CaravanID != "caravan-1" || len(active[0].Crew) != 2 {
		t.Fatal(active, err)
	}
}

func TestCaravanDepartureUnknownReplyReopensAndObservesAfterManual(t *testing.T) {
	f, n := caravanDepartureFixture(t)
	n.uncertain = true
	result, err := f.run()
	if err == nil || !result.Progress.View().Unresolved || n.dispatched != 1 || n.inspected != 2 {
		t.Fatal(result, err, n)
	}
	if err = f.store.Close(); err != nil {
		t.Fatal(err)
	}
	f.store, err = store.Open(context.Background(), f.path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.store.Close()
	f.executor.journal, f.executor.caravanDepartureJournal = f.store, f.store
	f.authority.Enabled = false
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	if _, err = f.run(); err != nil {
		t.Fatal(err)
	}
	if n.dispatched != 1 || n.observed != 1 {
		t.Fatal("uncertainty retried")
	}
	n.effect = domain.EffectCompleted
	result, err = f.run()
	if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 {
		t.Fatal(result, err)
	}
}

func TestCaravanDepartureNativeIneligibleBlocksDispatch(t *testing.T) {
	f, n := caravanDepartureFixture(t)
	n.ineligible = true
	result, err := f.run()
	if err != ErrHeld || result.Progress.View().Stage != domain.Pending || n.dispatched != 0 {
		t.Fatal(result, err)
	}
	held, ok := result.Progress.View().FreshHeldReason()
	if !ok || len(held) != 1 || held[0] != domain.HeldNativeIneligible {
		t.Fatal("ordinary refusal was not persisted as a held reason", held)
	}
}

func TestCaravanDepartureReconcileRejectsForeignCrew(t *testing.T) {
	f, n := caravanDepartureFixture(t)
	n.uncertain = true
	if _, err := f.run(); err == nil {
		t.Fatal("expected uncertain dispatch")
	}
	n.foreign = true
	if _, err := f.run(); err != ErrEvidence {
		t.Fatal(err)
	}
}
