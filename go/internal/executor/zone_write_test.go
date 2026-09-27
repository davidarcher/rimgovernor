package executor

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type zoneWriteEnvironment struct {
	*environment
	inspected, dispatched, observed int
	token                           string
	effect                          domain.Effect
}

func (n *zoneWriteEnvironment) InspectZoneWrite(_ context.Context, target Target) (ZoneWriteInspection, error) {
	n.inspected++
	now := n.clock.Now()
	emergency, _ := policy.NewEmergencySnapshot(target.Snapshot, n.tick, policy.EmergencyFacts{ColonistsComplete: domain.Known(true)})
	return ZoneWriteInspection{Current: target.Snapshot, Tick: n.tick, StartedAt: now, ObservedAt: now, Action: target.Action, SnapshotToken: n.token, Accepted: true, Emergency: emergency}, nil
}
func (n *zoneWriteEnvironment) ApplyZoneWrite(_ context.Context, d ZoneWriteDispatch) (Receipt, error) {
	n.dispatched++
	p := d.Attempt
	return Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}, nil
}
func (n *zoneWriteEnvironment) ObserveZoneWrite(_ context.Context, p Placement, current domain.GenerationSnapshot) (ZoneWriteEvidence, error) {
	n.observed++
	effect := n.effect
	if effect == "" {
		effect = domain.EffectUnknown
	}
	now := n.clock.Now()
	return ZoneWriteEvidence{Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: p.Tick + 1, Effect: effect, Causality: causalityFor(effect)}, StartedAt: now, ObservedAt: now, Complete: effect != domain.EffectUnknown, Action: p.Action, Matches: domain.Known(effect == domain.EffectCompleted)}, nil
}

func zoneWriteActions(t *testing.T) map[domain.ActionKind]domain.Action {
	t.Helper()
	del, err := domain.NewZoneDelete("Zone_1", "tok")
	if err != nil {
		t.Fatal(err)
	}
	edit, err := domain.NewZoneCellEdit("Zone_1", "tok", domain.AddZoneCells, []domain.Cell{{X: 1, Z: 1}})
	if err != nil {
		t.Fatal(err)
	}
	patch, err := domain.NewStockpilePatch(domain.StorageBuildingTarget, "Shelf_1", "tok", domain.GeneralFilter(), domain.CriticalPriority, "")
	if err != nil {
		t.Fatal(err)
	}
	out := map[domain.ActionKind]domain.Action{}
	for kind, build := range map[domain.ActionKind]func() (domain.Action, error){
		domain.ZoneDeleteAction:     func() (domain.Action, error) { return domain.NewZoneDeleteAction("zone-write-1", del) },
		domain.ZoneCellEditAction:   func() (domain.Action, error) { return domain.NewZoneCellEditAction("zone-write-1", edit) },
		domain.StockpilePatchAction: func() (domain.Action, error) { return domain.NewStockpilePatchAction("zone-write-1", patch) },
	} {
		a, err := build()
		if err != nil {
			t.Fatal(kind, err)
		}
		out[kind] = a
	}
	return out
}

func zoneWriteFixture(t *testing.T, action domain.Action) (*fixture, *zoneWriteEnvironment) {
	t.Helper()
	f := newFixture(t)
	plan, err := domain.NewPlan("zone-writes", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if err := f.store.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	n := &zoneWriteEnvironment{environment: f.env, token: "tok"}
	e, err := New(f.store, n, f.clock, Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	if err := e.EnableZoneWrite(n, action.Kind()); err != nil {
		t.Fatal(err)
	}
	f.executor, f.plan, f.action = e, plan, action
	f.authority.Snapshot.Plan = plan.ID()
	if err = e.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	return f, n
}

// TestZoneWriteKinds runs every zone write kind through the one path: a
// matching token admits and dispatches once, a completed observation
// finishes it, and a stale token holds before dispatch.
func TestZoneWriteKinds(t *testing.T) {
	for kind, action := range zoneWriteActions(t) {
		t.Run(string(kind), func(t *testing.T) {
			f, n := zoneWriteFixture(t, action)
			result, err := f.run()
			if err != nil || !result.Progress.View().Unresolved || n.dispatched != 1 || n.inspected != 2 {
				t.Fatal(result, err, n.dispatched, n.inspected)
			}
			n.effect = domain.EffectCompleted
			result, err = f.run()
			if err != nil || result.Progress.View().Stage != domain.Completed || n.dispatched != 1 || n.observed != 1 {
				t.Fatal(result, err, n.dispatched, n.observed)
			}

			f, n = zoneWriteFixture(t, action)
			n.token = "stale"
			if result, err = f.run(); err != ErrHeld || n.dispatched != 0 {
				t.Fatal(result, err, n.dispatched)
			}
		})
	}
}

func TestEnableZoneWriteRefusesOtherKinds(t *testing.T) {
	f := newFixture(t)
	if err := f.executor.EnableZoneWrite(&zoneWriteEnvironment{environment: f.env}, domain.QuestAcceptAction); err == nil {
		t.Fatal("zone write enabled for a non-zone kind")
	}
}
