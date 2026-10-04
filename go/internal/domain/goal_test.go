package domain

import "testing"

func TestProjectFinishedIsTerminalUntilRegressed(t *testing.T) {
	_, scope := fixture(t)
	g, _ := NewGoal("cook", AutopilotGoal, 2, scope, 10)
	g, e := ReviewProjectGoal(g, scope, 11, NeedRecovered, false)
	if e != nil || !ProjectGoalFinished(g) {
		t.Fatal(g, e)
	}
	g, e = ReviewProjectGoal(g, scope, 12, NeedUnknown, false)
	if e != nil || !ProjectGoalFinished(g) || g.Tick != 12 {
		t.Fatal(g, e)
	}
	if g2, e := ReviewProjectGoal(g, scope, 13, NeedDeficit, true); e != nil || !ProjectGoalFinished(g2) {
		t.Fatal("open work regressed a finished project", g2, e)
	}
	if !ProjectGoalRegressed(g, NeedDeficit, false) {
		t.Fatal("deficit did not regress")
	}
	if _, e = ReviewProjectGoal(g, scope, 13, NeedDeficit, false); e == nil {
		t.Fatal("regressed project reviewed in place")
	}
}

func TestMaintainedGoalUnknownRenewalAndCancellation(t *testing.T) {
	_, scope := fixture(t)
	g, e := NewGoal("food", AutopilotGoal, 2, scope, 10)
	if e != nil {
		t.Fatal(e)
	}
	g, e = ReviewGoal(g, scope, 11, NeedRecovered, false, true)
	if e != nil || g.Status != GoalSatisfied {
		t.Fatal(g, e)
	}
	g, e = ReviewGoal(g, scope, 12, NeedUnknown, false, true)
	if e != nil || g.Status == GoalSatisfied {
		t.Fatal(g, e)
	}
	g, e = ReviewGoal(g, scope, 13, NeedDeficit, false, true)
	if e != nil || g.Epoch != 1 || g.Status != GoalActive {
		t.Fatal(g, e)
	}
	g, e = ReviewGoal(g, scope, 15, NeedDeficit, false, true)
	if e != nil || g.Status != GoalActive || g.Epoch != 1 {
		t.Fatal(g, e)
	}
	g, e = CancelGoal(g)
	if e != nil {
		t.Fatal(e)
	}
	next, e := ReviewGoal(g, scope, 16, NeedDeficit, false, true)
	if e != nil || next != g {
		t.Fatal(next, e)
	}
}

// A recovery measured while the method's effects were still open (the
// lamp stands, the plan not yet observed) never reaches satisfaction; the
// next deficit after that work settles still opens a new epoch so the
// same method can repair the regression (#161).
func TestMaintainedGoalRecoveredWithOpenWorkThenDeficitRenewsEpoch(t *testing.T) {
	_, scope := fixture(t)
	g, e := NewGoal("lighting", AutopilotGoal, 3, scope, 10)
	if e != nil {
		t.Fatal(e)
	}
	g, e = ReviewGoal(g, scope, 11, NeedRecovered, true, true)
	if e != nil || g.Status != GoalActive || g.Need != NeedRecovered || g.RecoveryObserved || g.Epoch != 0 {
		t.Fatal(g, e)
	}
	// Still open: the epoch belongs to the working method.
	g, e = ReviewGoal(g, scope, 12, NeedDeficit, true, true)
	if e != nil || g.Epoch != 0 || g.Status != GoalActive {
		t.Fatal(g, e)
	}
	g, e = ReviewGoal(g, scope, 13, NeedRecovered, true, true)
	if e != nil || g.Epoch != 0 || g.Need != NeedRecovered {
		t.Fatal(g, e)
	}
	g, e = ReviewGoal(g, scope, 14, NeedDeficit, false, true)
	if e != nil || g.Epoch != 1 || g.Status != GoalActive || g.RecoveryObserved {
		t.Fatal(g, e)
	}
	// A deficit repeated within the epoch does not renew it again.
	g, e = ReviewGoal(g, scope, 15, NeedDeficit, false, true)
	if e != nil || g.Epoch != 1 {
		t.Fatal(g, e)
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
	review, e := ReviewGoal(g, scope, 11, NeedRecovered, true, true)
	if e != nil || review.Status == GoalSatisfied {
		t.Fatal(review, e)
	}
	for _, change := range []string{"map", "rewind"} {
		t.Run(change, func(t *testing.T) {
			s := scope
			tick := Tick(11)
			switch change {
			case "map":
				s.Map++
			case "rewind":
				tick = 9
			}
			r, e := ReviewGoal(g, s, tick, NeedDeficit, false, true)
			if e != nil || r.Status != GoalInvalidated {
				t.Fatal(r, e)
			}
		})
	}
	loaded := scope
	loaded.Load = "other"
	if r, e := ReviewGoal(g, loaded, 11, NeedDeficit, false, true); e != nil || r.Status == GoalInvalidated {
		t.Fatal("load change must keep the goal (#1082)", r, e)
	}
}

// Only a Standard re-arms its epoch on regress (#1024): any other goal
// reactivates on the same epoch.
func TestNonStandardGoalNeverBumpsEpoch(t *testing.T) {
	_, scope := fixture(t)
	g, e := NewGoal("project", AutopilotGoal, 2, scope, 10)
	if e != nil {
		t.Fatal(e)
	}
	tick := Tick(10)
	for _, need := range []NeedState{NeedRecovered, NeedDeficit, NeedRecovered, NeedDeficit} {
		for _, open := range []bool{true, false} {
			tick++
			if g, e = ReviewGoal(g, scope, tick, need, open, false); e != nil || g.Epoch != 0 {
				t.Fatal(g, e)
			}
		}
	}
	if g.Status != GoalActive {
		t.Fatal(g)
	}
}
