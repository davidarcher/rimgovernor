package domain

import (
	"errors"
	"reflect"
	"testing"
)

func dependencyPlan(t *testing.T, deps ...ActionDependency) PlanSpec {
	t.Helper()
	var actions []Action
	for i, id := range []ActionID{"foundation", "walls", "furniture"} {
		b, e := NewBuilding("Wall", Cell{int32(i), 1}, North, "")
		if e != nil {
			t.Fatal(e)
		}
		a, e := NewBuildingAction(id, b)
		if e != nil {
			t.Fatal(e)
		}
		actions = append(actions, a)
	}
	p, e := NewPlan("plan", 1, actions, deps...)
	if e != nil {
		t.Fatal(e)
	}
	return p
}
func TestDependenciesRejectUnknownSelfDuplicateAndCycles(t *testing.T) {
	p := dependencyPlan(t)
	for _, deps := range [][]ActionDependency{
		{{Action: "walls", Requires: "missing"}}, {{Action: "missing", Requires: "walls"}}, {{Action: "walls", Requires: "walls"}},
		{{Action: "walls", Requires: "foundation"}, {Action: "walls", Requires: "foundation"}},
		{{Action: "walls", Requires: "foundation"}, {Action: "foundation", Requires: "furniture"}, {Action: "furniture", Requires: "walls"}},
	} {
		if _, e := NewPlan(p.ID(), p.Revision(), p.Actions(), deps...); e == nil {
			t.Fatal("accepted", deps)
		}
	}
}
func TestDependenciesRequireObservedOutcomeAndPreserveIntent(t *testing.T) {
	deps := []ActionDependency{{Action: "furniture", Requires: "walls"}, {Action: "walls", Requires: "foundation"}}
	p := dependencyPlan(t, deps...)
	deps[0].Requires = "foundation"
	if !reflect.DeepEqual(p.Dependencies(), []ActionDependency{{Action: "furniture", Requires: "walls"}, {Action: "walls", Requires: "foundation"}}) {
		t.Fatal("aliased input")
	}
	got := p.Dependencies()
	got[0].Requires = "foundation"
	if p.Dependencies()[0].Requires != "walls" {
		t.Fatal("aliased output")
	}
	s := GenerationSnapshot{Colony: "colony", Load: "load", Map: 1, Plan: p.ID(), Revision: p.Revision()}
	progress, e := NewProgress(p, "foundation")
	if e != nil {
		t.Fatal(e)
	}
	if e = p.CheckDependencies("walls", []Progress{progress}, s, 10); !errors.Is(e, ErrDependency) {
		t.Fatal(e)
	}
	progress, e = progress.Prepare(s, 10)
	if e != nil {
		t.Fatal(e)
	}
	progress, e = progress.MarkDispatched(s, 10)
	if e != nil {
		t.Fatal(e)
	}
	progress, e = progress.RecordReceipt(1, ReceiptAccepted)
	if e != nil {
		t.Fatal(e)
	}
	if e = p.CheckDependencies("walls", []Progress{progress}, s, 11); !errors.Is(e, ErrDependency) {
		t.Fatal("receipt satisfied dependency", e)
	}
	progress, e = progress.Observe(Observation{Action: "foundation", Attempt: 1, Snapshot: s, Tick: 11, Effect: EffectCompleted}, s)
	if e != nil {
		t.Fatal(e)
	}
	if e = p.CheckDependencies("walls", []Progress{progress}, s, 11); e != nil {
		t.Fatal(e)
	}
	if e = p.CheckDependencies("furniture", []Progress{progress}, s, 11); !errors.Is(e, ErrDependency) {
		t.Fatal("skipped walls", e)
	}
	s.Load = "replacement"
	if e = p.CheckDependencies("walls", []Progress{progress}, s, 11); !errors.Is(e, ErrDependency) {
		t.Fatal("old load satisfied dependency", e)
	}
}

// A coupled dependency names its action as ready exactly once every
// prerequisite has completed in the current world and the action itself is
// still undispatched (#244); an ordering-only dependency never does.
func TestCoupledPendingNamesReadyCoupledOrders(t *testing.T) {
	p := dependencyPlan(t, ActionDependency{Action: "walls", Requires: "foundation", Coupled: true}, ActionDependency{Action: "furniture", Requires: "walls"})
	s := GenerationSnapshot{Colony: "colony", Load: "load", Map: 1, Plan: p.ID(), Revision: p.Revision()}
	var progress []Progress
	for _, id := range []ActionID{"foundation", "walls", "furniture"} {
		v, e := NewProgress(p, id)
		if e != nil {
			t.Fatal(e)
		}
		progress = append(progress, v)
	}
	if got := p.CoupledPending(progress, s, 10); got != nil {
		t.Fatal("prerequisite pending", got)
	}
	foundation, e := progress[0].Prepare(s, 10)
	if e != nil {
		t.Fatal(e)
	}
	if foundation, e = foundation.MarkDispatched(s, 10); e != nil {
		t.Fatal(e)
	}
	if foundation, e = foundation.RecordReceipt(1, ReceiptAccepted); e != nil {
		t.Fatal(e)
	}
	if foundation, e = foundation.Observe(Observation{Action: "foundation", Attempt: 1, Snapshot: s, Tick: 11, Effect: EffectCompleted}, s); e != nil {
		t.Fatal(e)
	}
	progress[0] = foundation
	if got := p.CoupledPending(progress, s, 11); !reflect.DeepEqual(got, []ActionID{"walls"}) {
		t.Fatal(got)
	}
	walls, e := progress[1].Prepare(s, 11)
	if e != nil {
		t.Fatal(e)
	}
	progress[1] = walls
	if got := p.CoupledPending(progress, s, 11); !reflect.DeepEqual(got, []ActionID{"walls"}) {
		t.Fatal("prepared is still undispatched", got)
	}
	if walls, e = walls.MarkDispatched(s, 11); e != nil {
		t.Fatal(e)
	}
	progress[1] = walls
	if got := p.CoupledPending(progress, s, 11); got != nil {
		t.Fatal("dispatched", got)
	}
	if _, e = NewPlan(p.ID(), p.Revision(), p.Actions(), ActionDependency{Action: "walls", Requires: "foundation"}, ActionDependency{Action: "walls", Requires: "foundation", Coupled: true}); e == nil {
		t.Fatal("coupling does not distinguish a duplicate edge")
	}
}
