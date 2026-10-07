package domain

import "testing"

func TestMaintainedGoalUnknownRenewalAndInvalidation(t *testing.T) {
	_, scope := fixture(t)
	g, e := NewStandard("food", 2, scope, 10)
	if e != nil {
		t.Fatal(e)
	}
	g, e = ReviewStandard(g, scope, 11, FindingMet, false)
	if e != nil || g.Status != StandardSettled {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, 12, FindingUnclear, false)
	if e != nil || g.Status == StandardSettled {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, 13, FindingUnmet, false)
	if e != nil || g.Episode != 1 || g.Status != StandardOpen {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, 15, FindingUnmet, false)
	if e != nil || g.Status != StandardOpen || g.Episode != 1 {
		t.Fatal(g, e)
	}
	// A tick rewind is not the goal's to catch: ReviewRounds voids every goal
	// when the tick falls below the previous round's, and the retirement floor
	// refuses work below a retired plan's tick.
	if quiet, e := ReviewStandard(g, scope, 14, FindingUnmet, false); e != nil || quiet != g {
		t.Fatal("a review with no change must leave the goal untouched", quiet, e)
	}
	moved := scope
	moved.Map++
	g, e = ReviewStandard(g, moved, 16, FindingUnmet, false)
	if e != nil || g.Status != StandardVoided {
		t.Fatal("map change did not invalidate", g, e)
	}
	next, e := ReviewStandard(g, scope, 16, FindingUnmet, false)
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
	g, e = ReviewStandard(g, scope, 11, FindingMet, true)
	if e != nil || g.Status != StandardOpen || g.Finding != FindingMet || g.RecoveryObserved || g.Episode != 0 {
		t.Fatal(g, e)
	}
	// Still open: the episode belongs to the working method.
	g, e = ReviewStandard(g, scope, 12, FindingUnmet, true)
	if e != nil || g.Episode != 0 || g.Status != StandardOpen {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, 13, FindingMet, true)
	if e != nil || g.Episode != 0 || g.Finding != FindingMet {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, 14, FindingUnmet, false)
	if e != nil || g.Episode != 1 || g.Status != StandardOpen || g.RecoveryObserved {
		t.Fatal(g, e)
	}
	// A deficit repeated within the episode does not renew it again.
	g, e = ReviewStandard(g, scope, 15, FindingUnmet, false)
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
	if !StandardWorkOpen([]Progress{progress}) {
		t.Fatal("issued work lost")
	}
	progress, e = progress.Cancel()
	if e != nil || !StandardWorkOpen([]Progress{progress}) {
		t.Fatal(e)
	}
	review, e := ReviewStandard(g, scope, 11, FindingMet, true)
	if e != nil || review.Status == StandardSettled {
		t.Fatal(review, e)
	}
	s := scope
	s.Map++
	if r, e := ReviewStandard(g, s, 11, FindingUnmet, false); e != nil || r.Status != StandardVoided {
		t.Fatal(r, e)
	}
	loaded := scope
	loaded.Load = "other"
	if r, e := ReviewStandard(g, loaded, 11, FindingUnmet, false); e != nil || r.Status == StandardVoided {
		t.Fatal("load change must keep the goal (#1082)", r, e)
	}
}

func TestSituationReducesToFinding(t *testing.T) {
	for s, f := range map[Situation]Finding{SituationActive: FindingUnmet, SituationClear: FindingMet, SituationUnclear: FindingUnclear} {
		if s.Finding() != f || f.Situation() != s {
			t.Fatal(s, f)
		}
	}
}

// A review that changes nothing but the tick returns the goal untouched, so
// the store does not bump its revision or rewrite its governor state each
// round; a real change still advances the tick.
func TestReviewWithoutChangeKeepsTheTickOfTheLastChange(t *testing.T) {
	_, scope := fixture(t)
	g, e := NewStandard("lighting", 3, scope, 10)
	if e != nil {
		t.Fatal(e)
	}
	g, e = ReviewStandard(g, scope, 11, FindingMet, false)
	if e != nil || g.Status != StandardSettled || g.Tick != 11 {
		t.Fatal(g, e)
	}
	for tick := Tick(12); tick < 20; tick++ {
		next, e := ReviewStandard(g, scope, tick, FindingMet, false)
		if e != nil || next != g {
			t.Fatal("repeat review changed the goal", tick, next, e)
		}
	}
	g, e = ReviewStandard(g, scope, 20, FindingUnmet, false)
	if e != nil || g.Tick != 20 || g.Episode != 1 {
		t.Fatal("a real change must advance the tick", g, e)
	}
	moved := scope
	moved.Native++
	next, e := ReviewStandard(g, moved, 21, FindingUnmet, false)
	if e != nil || next.Tick != 21 || next.Snapshot != moved {
		t.Fatal("a new snapshot is a change", next, e)
	}
	p, e := NewProject("project-0011223344556677-cook-0", "cook", 2, scope, 10)
	if e != nil {
		t.Fatal(e)
	}
	p, e = ReviewProject(p, scope, 11, FindingUnmet, true)
	if e != nil || p.Tick != 11 {
		t.Fatal(p, e)
	}
	if again, e := ReviewProject(p, scope, 12, FindingUnmet, true); e != nil || again != p {
		t.Fatal("repeat project review changed the project", again, e)
	}
}
