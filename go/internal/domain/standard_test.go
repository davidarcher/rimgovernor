package domain

import "testing"

func TestMaintainedGoalUnknownRenewalAndInvalidation(t *testing.T) {
	_, scope := fixture(t)
	g, e := NewStandard("food", "EnsureFoodSupply", 2, scope)
	if e != nil {
		t.Fatal(e)
	}
	g, e = ReviewStandard(g, scope, FindingMet, false)
	if e != nil || g.Status != StandardSettled {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, FindingUnclear, false)
	if e != nil || g.Status == StandardSettled {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, FindingUnmet, false)
	if e != nil || g.Episode != 1 || g.Status != StandardOpen {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, FindingUnmet, false)
	if e != nil || g.Status != StandardOpen || g.Episode != 1 {
		t.Fatal(g, e)
	}
	// A tick rewind is not the goal's to catch: ReviewRounds voids every goal
	// when the tick falls below the previous round's, and the retirement floor
	// refuses work below a retired plan's tick.
	if quiet, e := ReviewStandard(g, scope, FindingUnmet, false); e != nil || quiet != g {
		t.Fatal("a review with no change must leave the goal untouched", quiet, e)
	}
	moved := scope
	moved.Map++
	g, e = ReviewStandard(g, moved, FindingUnmet, false)
	if e != nil || g.Status != StandardVoided {
		t.Fatal("map change did not invalidate", g, e)
	}
	next, e := ReviewStandard(g, scope, FindingUnmet, false)
	if e != nil || next != g {
		t.Fatal(next, e)
	}
}

// A recovery measured while the method's effects were still open (the
// lamp stands, the plan not yet observed) never reaches satisfaction; the
// next deficit after that work settles still opens a new episode so the
// same method can repair the regression.
func TestMaintainedGoalRecoveredWithOpenWorkThenDeficitRenewsEpoch(t *testing.T) {
	_, scope := fixture(t)
	g, e := NewStandard("lighting", "MaintainLighting", 3, scope)
	if e != nil {
		t.Fatal(e)
	}
	g, e = ReviewStandard(g, scope, FindingMet, true)
	if e != nil || g.Status != StandardOpen || g.Finding != FindingMet || g.RecoveryObserved || g.Episode != 0 {
		t.Fatal(g, e)
	}
	// Still open: the episode belongs to the working method.
	g, e = ReviewStandard(g, scope, FindingUnmet, true)
	if e != nil || g.Episode != 0 || g.Status != StandardOpen {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, FindingMet, true)
	if e != nil || g.Episode != 0 || g.Finding != FindingMet {
		t.Fatal(g, e)
	}
	g, e = ReviewStandard(g, scope, FindingUnmet, false)
	if e != nil || g.Episode != 1 || g.Status != StandardOpen || g.RecoveryObserved {
		t.Fatal(g, e)
	}
	// A deficit repeated within the episode does not renew it again.
	g, e = ReviewStandard(g, scope, FindingUnmet, false)
	if e != nil || g.Episode != 1 {
		t.Fatal(g, e)
	}
}
func TestMaintainedGoalInvalidatesScopeAndWaitsForEffects(t *testing.T) {
	progress, scope := dispatched(t)
	g, e := NewStandard("shelter", "MaintainShelter", 2, scope)
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
	review, e := ReviewStandard(g, scope, FindingMet, true)
	if e != nil || review.Status == StandardSettled {
		t.Fatal(review, e)
	}
	s := scope
	s.Map++
	if r, e := ReviewStandard(g, s, FindingUnmet, false); e != nil || r.Status != StandardVoided {
		t.Fatal(r, e)
	}
	loaded := scope
	loaded.Load = "other"
	if r, e := ReviewStandard(g, loaded, FindingUnmet, false); e != nil || r.Status == StandardVoided {
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

// A review that changes nothing returns the goal equal to its input, so the
// store does not bump its revision or rewrite its governor state each round;
// a real change still differs.
func TestReviewWithoutChangeReturnsTheGoalEqual(t *testing.T) {
	_, scope := fixture(t)
	g, e := NewStandard("lighting", "MaintainLighting", 3, scope)
	if e != nil {
		t.Fatal(e)
	}
	g, e = ReviewStandard(g, scope, FindingMet, false)
	if e != nil || g.Status != StandardSettled {
		t.Fatal(g, e)
	}
	for i := 0; i < 8; i++ {
		next, e := ReviewStandard(g, scope, FindingMet, false)
		if e != nil || next != g {
			t.Fatal("repeat review changed the goal", i, next, e)
		}
	}
	changed, e := ReviewStandard(g, scope, FindingUnmet, false)
	if e != nil || changed == g || changed.Episode != 1 {
		t.Fatal("a real change must differ", changed, e)
	}
	moved := scope
	moved.Native++
	next, e := ReviewStandard(changed, moved, FindingUnmet, false)
	if e != nil || next == changed || next.Snapshot != moved {
		t.Fatal("a new snapshot is a change", next, e)
	}
}

func TestOnceStandardSettlesOnceAndRegressionStartsNewEpisode(t *testing.T) {
	_, scope := fixture(t)
	p, e := NewStandard("routine-0011223344556677-cook-0", "cook", 2, scope)
	if e != nil {
		t.Fatal(e)
	}
	p.Once = true
	if p, e = ReviewStandard(p, scope, FindingUnmet, false); e != nil || p.Status != StandardOpen || p.Finding != FindingUnmet {
		t.Fatal(p, e)
	}
	if p, e = ReviewStandard(p, scope, FindingMet, true); e != nil || p.Status != StandardOpen {
		t.Fatal("recovery with open work settled the standard", p, e)
	}
	// A deficit measured after unresolved effects is not a regression.
	if q, e := ReviewStandard(p, scope, FindingUnmet, false); e != nil || q.Episode != 0 || q.Status != StandardOpen {
		t.Fatal("unsettled once standard regressed", q, e)
	}
	if p, e = ReviewStandard(p, scope, FindingMet, false); e != nil || p.Status != StandardSettled {
		t.Fatal(p, e)
	}
	// An unknown measurement or open work does not reopen it, and a review
	// that changes nothing returns it equal to its input.
	for _, open := range []bool{false, true} {
		if again, e := ReviewStandard(p, scope, FindingUnclear, open); e != nil || again != p {
			t.Fatal("unclear reopened a settled once standard", again, e)
		}
	}
	if again, e := ReviewStandard(p, scope, FindingUnmet, true); e != nil || again != p {
		t.Fatal("open work regressed a settled once standard", again, e)
	}
	next, e := ReviewStandard(p, scope, FindingUnmet, false)
	if e != nil || next.Status != StandardOpen || next.Episode != 1 || next.RecoveryObserved {
		t.Fatal("deficit with no work open must start a new episode", next, e)
	}
}
