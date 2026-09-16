package domain

import "testing"

func TestMaintainedGoalUnknownRenewalAndCancellation(t *testing.T) {
	_, scope := fixture(t)
	g, e := NewGoal("food", AutopilotGoal, 2, scope, 10)
	if e != nil {
		t.Fatal(e)
	}
	g, e = ReviewGoal(g, scope, 11, NeedRecovered, false, false)
	if e != nil || g.Status != GoalSatisfied {
		t.Fatal(g, e)
	}
	g, e = ReviewGoal(g, scope, 12, NeedUnknown, false, false)
	if e != nil || g.Status == GoalSatisfied {
		t.Fatal(g, e)
	}
	g, e = ReviewGoal(g, scope, 13, NeedDeficit, false, false)
	if e != nil || g.Epoch != 1 || g.Status != GoalActive {
		t.Fatal(g, e)
	}
	g, e = ReviewGoal(g, scope, 14, NeedDeficit, true, false)
	if e != nil || g.Status != GoalSuspended {
		t.Fatal(g, e)
	}
	g, e = ReviewGoal(g, scope, 15, NeedDeficit, false, false)
	if e != nil || g.Status != GoalActive || g.Epoch != 1 {
		t.Fatal(g, e)
	}
	g, e = CancelGoal(g)
	if e != nil {
		t.Fatal(e)
	}
	next, e := ReviewGoal(g, scope, 16, NeedDeficit, false, false)
	if e != nil || next != g {
		t.Fatal(next, e)
	}
}
func TestMaintainedGoalInvalidatesScopeAndWaitsForEffects(t *testing.T) {
	progress, scope := dispatched(t)
	g, e := NewGoal("shelter", AutopilotGoal, 2, scope, 10)
	if e != nil {
		t.Fatal(e)
	}
	if !GoalWorkOpen([]Progress{progress}) {
		t.Fatal("issued work lost")
	}
	progress, e = progress.Cancel()
	if e != nil || !GoalWorkOpen([]Progress{progress}) {
		t.Fatal(e)
	}
	review, e := ReviewGoal(g, scope, 11, NeedRecovered, false, true)
	if e != nil || review.Status == GoalSatisfied {
		t.Fatal(review, e)
	}
	for _, change := range []string{"load", "map", "rewind"} {
		t.Run(change, func(t *testing.T) {
			s := scope
			tick := Tick(11)
			switch change {
			case "load":
				s.Load = "other"
			case "map":
				s.Map++
			case "rewind":
				tick = 9
			}
			r, e := ReviewGoal(g, s, tick, NeedDeficit, false, false)
			if e != nil || r.Status != GoalInvalidated {
				t.Fatal(r, e)
			}
		})
	}
}
