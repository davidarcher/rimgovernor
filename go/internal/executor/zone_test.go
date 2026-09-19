package executor

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type zoneEnvironment struct {
	*environment
	inspected, created, observed int
	unsafe                       bool
	effect                       domain.Effect
	matches                      bool
}

func (n *zoneEnvironment) InspectZone(_ context.Context, target Target) (ZoneInspection, error) {
	n.inspected++
	zone, _ := target.Action.ZoneCreate()
	tick := domain.Tick(100 + int64(n.inspected))
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(!n.unsafe), ThreatsComplete: domain.Known(true)})
	return ZoneInspection{Current: target.Snapshot, Tick: tick, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Zone: zone, SnapshotToken: "zone-token", Accepted: true, Emergency: emergency}, nil
}
func (n *zoneEnvironment) CreateZone(_ context.Context, request ZoneDispatch) (Receipt, error) {
	n.created++
	if request.SnapshotToken != "zone-token" {
		return Receipt{}, ErrEvidence
	}
	p := request.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *zoneEnvironment) ObserveZone(_ context.Context, p Placement, current domain.GenerationSnapshot) (ZoneEvidence, error) {
	n.observed++
	zone, _ := p.Action.ZoneCreate()
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	e := ZoneEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect}, StartedAt: n.clock.Now(), ObservedAt: n.clock.Now(), Complete: effect != domain.EffectUnknown, Zone: zone}
	if effect == domain.EffectCompleted || effect == domain.EffectUnsuccessful {
		e.Matches = domain.Known(n.matches)
	}
	if effect == domain.EffectCompleted {
		e.Observation.Zone = "Zone_7"
	}
	if effect == domain.EffectUnsuccessful {
		e.Observation.UnsuccessfulReason = domain.OutcomeNotAchieved
	}
	return e, nil
}
func zoneFixture(t *testing.T) (*fixture, *zoneEnvironment) {
	t.Helper()
	f := newFixture(t)
	zone, err := domain.NewZoneCreate(domain.GrowingZone, "Corn", []domain.Cell{{X: 3, Z: 4}})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewZoneCreateAction("zone-1", zone)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("zones", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if err = f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &zoneEnvironment{environment: f.env}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: 5 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableZone(n); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}
func TestZoneEmergencyBlocksDispatch(t *testing.T) {
	f, n := zoneFixture(t)
	n.unsafe = true
	if _, err := f.run(); err == nil || n.created != 0 {
		t.Fatal("unsafe write", err)
	}
	held, ok := f.progress(t).FreshHeldReason()
	if !ok || len(held) != 1 || held[0] != domain.HeldUnknownFacts {
		t.Fatal("emergency hold was not persisted as a held reason", held)
	}
}
