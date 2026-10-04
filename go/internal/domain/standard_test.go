package domain

import "testing"

func TestMaintainedGoalUnknownRenewalAndInvalidation(t *testing.T) {
	_, scope := fixture(t)
	g, e := NewStandard("food", 2, scope, 10)
	if e != nil {
		t.Fatal(e)
	}
	g, e = ReviewStandard(g, scope, 11, NeedRecovered, false)
	if e != nil || g.Status != StandardSettled {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, 12, NeedUnknown, false)
	if e != nil || g.Status == StandardSettled {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, 13, NeedDeficit, false)
	if e != nil || g.Episode != 1 || g.Status != StandardOpen {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, 15, NeedDeficit, false)
	if e != nil || g.Status != StandardOpen || g.Episode != 1 {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, 14, NeedDeficit, false)
	if e != nil || g.Status != StandardVoided {
		t.Fatal("tick rewind did not invalidate", g, e)
	}
	next, e := ReviewStandard(g, scope, 16, NeedDeficit, false)
	if e != nil || next != g {
		t.Fatal(next, e)
	}
}

// A recovery measured while the method's effects were still open (the
// lamp stands, the plan not yet observed) never reaches satisfaction; the
// next deficit after that work settles still opens a new episode so the
// same method can repair the regression (#161).
func TestMaintainedGoalRecoveredWithOpenWorkThenDeficitRenewsEpoch(t *testing.T) {
	_, scope := fixture(t)
	g, e := NewStandard("lighting", 3, scope, 10)
	if e != nil {
		t.Fatal(e)
	}
	g, e = ReviewStandard(g, scope, 11, NeedRecovered, true)
	if e != nil || g.Status != StandardOpen || g.Need != NeedRecovered || g.RecoveryObserved || g.Episode != 0 {
		t.Fatal(g, e)
	}
	// Still open: the episode belongs to the working method.
	g, e = ReviewStandard(g, scope, 12, NeedDeficit, true)
	if e != nil || g.Episode != 0 || g.Status != StandardOpen {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, 13, NeedRecovered, true)
	if e != nil || g.Episode != 0 || g.Need != NeedRecovered {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, 14, NeedDeficit, false)
	if e != nil || g.Episode != 1 || g.Status != StandardOpen || g.RecoveryObserved {
		t.Fatal(g, e)
	}
	// A deficit repeated within the episode does not renew it again.
	g, e = ReviewStandard(g, scope, 15, NeedDeficit, false)
	if e != nil || g.Episode != 1 {
		t.Fatal(g, e)
	}
}
func TestMaintainedGoalInvalidatesScopeAndWaitsForEffects(t *testing.T) {
	progress, scope := dispatched(t)
	g, e := NewStandard("shelter", 2, scope, 10)
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
	review, e := ReviewStandard(g, scope, 11, NeedRecovered, true)
	if e != nil || review.Status == StandardSettled {
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
			r, e := ReviewStandard(g, s, tick, NeedDeficit, false)
			if e != nil || r.Status != StandardVoided {
				t.Fatal(r, e)
			}
		})
	}
	loaded := scope
	loaded.Load = "other"
	if r, e := ReviewStandard(g, loaded, 11, NeedDeficit, false); e != nil || r.Status == StandardVoided {
		t.Fatal("load change must keep the goal (#1082)", r, e)
	}
}
