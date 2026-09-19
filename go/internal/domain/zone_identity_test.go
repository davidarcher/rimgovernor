package domain

import "testing"

func dispatchedZone(t *testing.T) (Progress, GenerationSnapshot) {
	t.Helper()
	zone, err := NewStockpileZone(FoodPreset, ImportantPriority, []Cell{{X: 3, Z: 5}})
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewZoneCreateAction("z1", zone)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := NewPlan("p1", 1, []Action{a})
	if err != nil {
		t.Fatal(err)
	}
	p, err := NewProgress(plan, a.ID())
	if err != nil {
		t.Fatal(err)
	}
	s := GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "p1", Revision: 1}
	if p, err = p.Prepare(s, 10); err != nil {
		t.Fatal(err)
	}
	if p, err = p.MarkDispatched(s, 10); err != nil {
		t.Fatal(err)
	}
	return p, s
}

// The zone identity a completed creation receipt names is the only ownership
// evidence a stockpile claim has (#315), so it is kept exactly like a
// construction identity: on correlated completed zone evidence only.
func TestCompletedZoneIdentityRequiresCausalCompletedZoneEvidence(t *testing.T) {
	p, scope := dispatchedZone(t)
	for _, effect := range []Effect{EffectUnknown, EffectPending, EffectCompleted, EffectAbsent} {
		for _, causality := range []ObservationCausality{"", AfterDispatch} {
			o := Observation{Action: p.View().Action, Attempt: p.View().Attempt, Snapshot: scope, Tick: 11, Effect: effect, Causality: causality, Zone: "Zone_7"}
			got, err := p.Observe(o, scope)
			if effect == EffectCompleted && causality == AfterDispatch {
				id, known := got.View().Zone.Value()
				if err != nil || !known || id != "Zone_7" {
					t.Fatal(got, err)
				}
			} else if err == nil || got != p {
				t.Fatal("invalid zone identity changed progress", got, err)
			}
		}
	}
	if _, err := p.Observe(Observation{Action: p.View().Action, Attempt: p.View().Attempt, Snapshot: scope, Tick: 11, Effect: EffectCompleted, Causality: AfterDispatch, Zone: " "}, scope); err == nil {
		t.Fatal("blank zone identity accepted")
	}
	building, scope := dispatched(t)
	if _, err := building.Observe(Observation{Action: building.View().Action, Attempt: building.View().Attempt, Snapshot: scope, Tick: 11, Effect: EffectCompleted, Causality: AfterDispatch, Zone: "Zone_7"}, scope); err == nil {
		t.Fatal("building completion accepted a zone identity")
	}
	if got, err := p.Observe(Observation{Action: p.View().Action, Attempt: p.View().Attempt, Snapshot: scope, Tick: 11, Effect: EffectCompleted, Causality: AfterDispatch}, scope); err != nil {
		t.Fatal(err)
	} else if _, known := got.View().Zone.Value(); known {
		t.Fatal("completion without a receipt identity invented one")
	}
}
