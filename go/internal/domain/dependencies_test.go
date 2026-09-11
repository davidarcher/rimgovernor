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
		{{"walls", "missing"}}, {{"missing", "walls"}}, {{"walls", "walls"}},
		{{"walls", "foundation"}, {"walls", "foundation"}},
		{{"walls", "foundation"}, {"foundation", "furniture"}, {"furniture", "walls"}},
	} {
		if _, e := NewPlan(p.ID(), p.Revision(), p.Actions(), deps...); e == nil {
			t.Fatal("accepted", deps)
		}
	}
}
func TestDependenciesRequireObservedOutcomeAndPreserveIntent(t *testing.T) {
	deps := []ActionDependency{{"furniture", "walls"}, {"walls", "foundation"}}
	p := dependencyPlan(t, deps...)
	deps[0].Requires = "foundation"
	if !reflect.DeepEqual(p.Dependencies(), []ActionDependency{{"furniture", "walls"}, {"walls", "foundation"}}) {
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
