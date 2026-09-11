package domain

import (
	"reflect"
	"strings"
	"testing"
)

func fixture(t *testing.T) (Progress, GenerationSnapshot) {
	t.Helper()
	b, err := NewBuilding("Modded_Wall", Cell{3, 5}, North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewBuildingAction("a1", b)
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
	return p, GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "p1", Revision: 1}
}
func dispatched(t *testing.T) (Progress, GenerationSnapshot) {
	t.Helper()
	p, s := fixture(t)
	p, err := p.Prepare(s, 10)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.MarkDispatched(s, 10)
	if err != nil {
		t.Fatal(err)
	}
	return p, s
}
func TestFactsPreserveUnknown(t *testing.T) {
	if _, ok := Unknown[bool]().Value(); ok {
		t.Fatal("unknown became false fact")
	}
	if v, ok := Known(false).Value(); !ok || v {
		t.Fatal("known false lost")
	}
	if v, ok := Known(Tick(0)).Value(); !ok || v != 0 {
		t.Fatal("known tick zero lost")
	}
}

func TestSharedIdentifierBounds(t *testing.T) {
	for _, text := range []string{strings.Repeat("a", 256), strings.Repeat("\U0001F600", 64)} {
		building, err := NewBuilding(text, Cell{}, North, text)
		if err != nil {
			t.Fatal(err)
		}
		action, err := NewBuildingAction(ActionID(text), building)
		if err != nil {
			t.Fatal(err)
		}
		if _, err = NewPlan(PlanID(text), 0, []Action{action}); err != nil {
			t.Fatal(err)
		}
	}
	building, err := NewBuilding("Wall", Cell{}, North, "")
	if err != nil {
		t.Fatal(err)
	}
	for _, text := range []string{"a\x00b", strings.Repeat("a", 257), strings.Repeat("\U0001F600", 65), "\xff"} {
		if _, err := NewBuildingAction(ActionID(text), building); err == nil {
			t.Fatalf("accepted invalid action ID %q", text)
		}
		if _, err := NewPlan(PlanID(text), 0, nil); err == nil {
			t.Fatalf("accepted invalid plan ID %q", text)
		}
		for _, snapshot := range []GenerationSnapshot{
			{Colony: ColonyID(text), Load: "load", Plan: "plan"},
			{Colony: "colony", Load: LoadID(text), Plan: "plan"},
		} {
			if err := snapshot.Validate(); err == nil {
				t.Fatalf("accepted invalid world identity %q", text)
			}
		}
		if _, err := NewBuilding(text, Cell{}, North, ""); err == nil {
			t.Fatalf("accepted invalid definition %q", text)
		}
		if _, err := NewBuilding("Wall", Cell{}, North, text); err == nil {
			t.Fatalf("accepted invalid material %q", text)
		}
	}
}

func TestBuildingAndPlanValidation(t *testing.T) {
	for _, tc := range []struct {
		name, def string
		cell      Cell
		rotation  Rotation
		stuff     string
	}{
		{"blank", " \t", Cell{}, North, ""},
		{"negative", "Wall", Cell{-1, 0}, North, ""},
		{"rotation", "Wall", Cell{}, "diagonal", ""},
		{"utf8 bound", strings.Repeat("\U0001F600", 65), Cell{}, North, ""},
		{"encoding", "\xff", Cell{}, North, ""},
		{"nul", "Wall\x00", Cell{}, North, ""},
		{"stuff", "Wall", Cell{}, North, " "},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if _, err := NewBuilding(tc.def, tc.cell, tc.rotation, tc.stuff); err == nil {
				t.Fatal("accepted malformed building")
			}
		})
	}
	b, err := NewBuilding(strings.Repeat("\U0001F600", 64), Cell{}, West, "Modded_Stuff")
	if err != nil {
		t.Fatal(err)
	}
	a, err := NewBuildingAction("stable", b)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = NewPlan("p", 0, []Action{a, a}); err == nil {
		t.Fatal("duplicate IDs accepted")
	}
	if _, err = NewBuildingAction("", b); err == nil {
		t.Fatal("empty ID accepted")
	}
	if _, err = NewBuildingAction("a", Building{}); err == nil {
		t.Fatal("zero building accepted")
	}
	if _, err = NewPlan("p", 0, []Action{{}}); err == nil {
		t.Fatal("zero action accepted")
	}
	input := []Action{a}
	plan, err := NewPlan("p", 0, input)
	if err != nil {
		t.Fatal(err)
	}
	input[0] = Action{}
	output := plan.Actions()
	output[0] = Action{}
	if plan.Actions()[0].ID() != "stable" {
		t.Fatal("plan mutable through slice")
	}
	if _, err = NewProgress(plan, "missing"); err == nil {
		t.Fatal("unbound progress")
	}
	if err = ValidateHandlerCoverage(SupportedActionKinds()); err != nil {
		t.Fatal(err)
	}
	for _, kinds := range [][]ActionKind{nil, {BuildingAction, BuildingAction}, {"unsupported"}} {
		if ValidateHandlerCoverage(kinds) == nil {
			t.Fatal("invalid handlers accepted")
		}
	}
}
func TestReceiptNeverCompletesOrUnlocksRetry(t *testing.T) {
	for _, receipt := range []Receipt{ReceiptAccepted, ReceiptRefused, ReceiptUnknown} {
		t.Run(string(receipt), func(t *testing.T) {
			p, s := dispatched(t)
			p, err := p.RecordReceipt(p.View().Attempt, receipt)
			if err != nil {
				t.Fatal(err)
			}
			if p.View().Stage != AwaitingObservation || !p.View().Unresolved {
				t.Fatal(p.View())
			}
			if _, err = p.Prepare(s, 11); err == nil {
				t.Fatal("receipt unlocked retry")
			}
			p, err = p.Observe(Observation{Action: "a1", Attempt: 1, Snapshot: s, Tick: 11, Effect: EffectUnknown}, s)
			if err != nil {
				t.Fatal(err)
			}
			if !p.View().Unresolved {
				t.Fatal("unknown cleared dispatch")
			}
			p, err = p.Observe(Observation{Action: "a1", Attempt: 1, Snapshot: s, Tick: 12, Effect: EffectPending}, s)
			if err != nil {
				t.Fatal(err)
			}
			if !p.View().Unresolved {
				t.Fatal("pending cleared dispatch")
			}
			p, err = p.Observe(Observation{Action: "a1", Attempt: 1, Snapshot: s, Tick: 13, Effect: EffectCompleted}, s)
			if err != nil {
				t.Fatal(err)
			}
			if p.View().Stage != Completed || p.View().Unresolved {
				t.Fatal(p.View())
			}
			if _, err = p.Prepare(s, 14); err == nil {
				t.Fatal("completed action retried")
			}
		})
	}
}
func TestCompleteAbsenceAllowsSameActionRetry(t *testing.T) {
	p, s := dispatched(t)
	p, err := p.Observe(Observation{Action: "a1", Attempt: 1, Snapshot: s, Tick: 11, Effect: EffectAbsent}, s)
	if err != nil {
		t.Fatal(err)
	}
	if p.View().Stage != Pending || p.View().Unresolved || p.View().Action != "a1" {
		t.Fatal(p.View())
	}
	p, err = p.Prepare(s, 11)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.MarkDispatched(s, 11)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := p.View().Effect.Value(); known {
		t.Fatal("old observation reused")
	}
}
func TestCancellationRetainsUncertaintyAndNeverReactivates(t *testing.T) {
	for _, effect := range []Effect{EffectUnknown, EffectPending, EffectAbsent, EffectCompleted} {
		t.Run(string(effect), func(t *testing.T) {
			p, s := dispatched(t)
			p, err := p.Cancel()
			if err != nil {
				t.Fatal(err)
			}
			if !p.View().Unresolved {
				t.Fatal("cancel lost dispatch")
			}
			p, err = p.RecordReceipt(p.View().Attempt, ReceiptUnknown)
			if err != nil {
				t.Fatal(err)
			}
			s.Direction++
			p, err = p.Observe(Observation{Action: "a1", Attempt: 1, Snapshot: s, Tick: 11, Effect: effect}, s)
			if err != nil {
				t.Fatal(err)
			}
			if p.View().Stage != Cancelled {
				t.Fatal("cancelled intent reactivated")
			}
			if p.View().Unresolved != (effect == EffectUnknown || effect == EffectPending) {
				t.Fatal("incorrect uncertainty")
			}
			if _, err = p.Prepare(s, 12); err == nil {
				t.Fatal("cancelled action retried")
			}
		})
	}
}
func TestStaleEvidenceAndAuthorityPreserveProgress(t *testing.T) {
	p, s := fixture(t)
	p, err := p.Prepare(s, 10)
	if err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*GenerationSnapshot){func(s *GenerationSnapshot) { s.Colony = "other" }, func(s *GenerationSnapshot) { s.Map++ }, func(s *GenerationSnapshot) { s.Load = "other" }, func(s *GenerationSnapshot) { s.Direction++ }, func(s *GenerationSnapshot) { s.Revision++ }, func(s *GenerationSnapshot) { s.Native++ }} {
		stale := s
		change(&stale)
		got, err := p.MarkDispatched(stale, 10)
		if err == nil || !reflect.DeepEqual(got, p) {
			t.Fatal("stale dispatch changed progress")
		}
	}
	p, err = p.MarkDispatched(s, 10)
	if err != nil {
		t.Fatal(err)
	}
	for _, o := range []Observation{{Action: "a1", Attempt: 1, Snapshot: s, Tick: 10, Effect: EffectAbsent}, {Action: "other", Attempt: 1, Snapshot: s, Tick: 11, Effect: EffectAbsent}, {Action: "a1", Attempt: 1, Snapshot: s, Tick: 11, Effect: "invalid"}} {
		got, err := p.Observe(o, s)
		if err == nil || !reflect.DeepEqual(got, p) {
			t.Fatal("bad observation changed progress")
		}
	}
	for _, change := range []func(*GenerationSnapshot){func(s *GenerationSnapshot) { s.Colony = "other" }, func(s *GenerationSnapshot) { s.Map++ }, func(s *GenerationSnapshot) { s.Load = "other" }} {
		other := s
		change(&other)
		got, err := p.Observe(Observation{Action: "a1", Attempt: 1, Snapshot: other, Tick: 11, Effect: EffectCompleted}, other)
		if err == nil || !reflect.DeepEqual(got, p) {
			t.Fatal("cross-world attribution accepted")
		}
	}
	changed := s
	changed.Direction++
	p, err = p.Observe(Observation{Action: "a1", Attempt: 1, Snapshot: changed, Tick: 11, Effect: EffectAbsent}, changed)
	if err != nil {
		t.Fatal(err)
	}
	if p.View().Stage != Cancelled {
		t.Fatal("new direction unlocked old intent")
	}
}
func TestIllegalTransitions(t *testing.T) {
	p, s := fixture(t)
	if _, err := p.MarkDispatched(s, 0); err == nil {
		t.Fatal("unprepared dispatch")
	}
	if _, err := p.RecordReceipt(p.View().Attempt, ReceiptAccepted); err == nil {
		t.Fatal("unissued receipt")
	}
	if _, err := p.Observe(Observation{Action: "a1", Attempt: 1, Snapshot: s, Tick: 1, Effect: EffectCompleted}, s); err == nil {
		t.Fatal("unissued observation")
	}
	if _, err := p.Prepare(s, -1); err == nil {
		t.Fatal("negative tick")
	}
	s.Plan = "other"
	if _, err := p.Prepare(s, 0); err == nil {
		t.Fatal("wrong plan")
	}
	p, s = dispatched(t)
	if _, err := p.RecordReceipt(p.View().Attempt, "invalid"); err == nil {
		t.Fatal("invalid receipt")
	}
	p, err := p.RecordReceipt(p.View().Attempt, ReceiptAccepted)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = p.RecordReceipt(p.View().Attempt, ReceiptRefused); err == nil {
		t.Fatal("overwritten receipt")
	}
	if _, err = (Progress{}).Cancel(); err == nil {
		t.Fatal("zero progress cancelled")
	}
}

func TestRetryRejectsLateEvidenceFromPreviousAttempt(t *testing.T) {
	p, s := dispatched(t)
	first := p.View().Attempt
	if first != 1 {
		t.Fatal("first dispatch identity", first)
	}
	p, err := p.RecordReceipt(first, ReceiptUnknown)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.Observe(Observation{Action: "a1", Attempt: first, Snapshot: s, Tick: 11, Effect: EffectUnknown}, s)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = p.Prepare(s, 12); err == nil {
		t.Fatal("partial observation unlocked retry")
	}
	p, err = p.Observe(Observation{Action: "a1", Attempt: first, Snapshot: s, Tick: 12, Effect: EffectAbsent}, s)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.Prepare(s, 12)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.MarkDispatched(s, 12)
	if err != nil {
		t.Fatal(err)
	}
	second := p.View().Attempt
	if second != first+1 {
		t.Fatal("retry reused attempt identity")
	}
	for _, attempt := range []AttemptID{0, first, second + 1} {
		got, err := p.RecordReceipt(attempt, ReceiptAccepted)
		if err == nil || !reflect.DeepEqual(got, p) {
			t.Fatal("late receipt changed current dispatch")
		}
		for _, effect := range []Effect{EffectAbsent, EffectCompleted, EffectUnknown} {
			got, err = p.Observe(Observation{Action: "a1", Attempt: attempt, Snapshot: s, Tick: 13, Effect: effect}, s)
			if err == nil || !reflect.DeepEqual(got, p) {
				t.Fatal("late observation changed current dispatch")
			}
		}
	}
	if !p.View().Unresolved {
		t.Fatal("retry uncertainty lost")
	}
	if _, known := p.View().Receipt.Value(); known {
		t.Fatal("late receipt retained")
	}
	p, err = p.RecordReceipt(second, ReceiptAccepted)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.Observe(Observation{Action: "a1", Attempt: second, Snapshot: s, Tick: 13, Effect: EffectCompleted}, s)
	if err != nil || p.View().Stage != Completed {
		t.Fatal("current attempt cannot complete", err)
	}
}

func TestAttemptIdentityOverflowPreservesPreparedProgress(t *testing.T) {
	p, s := fixture(t)
	p, err := p.Prepare(s, 10)
	if err != nil {
		t.Fatal(err)
	}
	p.view.Attempt = ^AttemptID(0)
	got, err := p.MarkDispatched(s, 10)
	if err == nil || !reflect.DeepEqual(got, p) {
		t.Fatal("attempt counter wrapped or mutated progress")
	}
}
