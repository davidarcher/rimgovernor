package snapshot

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Recorded from acceptance run clean/filthy at e24c531b (the last review of
// the run, tick 158107): blood filth in a kitchen with no cleaner.
const cleanFilthy = "testdata/clean-filthy-kitchen.json.gz"

func TestReplayReproducesTheRecordedReview(t *testing.T) {
	r, err := Load(cleanFilthy)
	if err != nil {
		t.Fatal(err)
	}
	needs, err := r.Detect()
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(needs.Latches, r.Review.Latches) {
		t.Fatalf("latches %+v, recorded %+v", needs.Latches, r.Review.Latches)
	}
	bound := map[domain.GoalID]bool{}
	for _, g := range r.Review.Goals {
		bound[g.Need] = true
	}
	for _, a := range needs.Assessments {
		if !bound[a.ID] {
			t.Error("replay assessed unbound", a.ID)
		}
		delete(bound, a.ID)
	}
	if len(bound) > 0 {
		t.Error("recorded goals replay did not assess", bound)
	}
}

func TestReplayFilthyKitchenOpensCleaning(t *testing.T) {
	r, err := Load(cleanFilthy)
	if err != nil {
		t.Fatal(err)
	}
	a, err := r.Assessment(policy.MaintainCleanFacilities)
	if err != nil || a.Need != domain.NeedDeficit || a.MethodUnavailable {
		t.Fatal(a, err)
	}
}

func TestRecordRoundTrips(t *testing.T) {
	r, err := Load(cleanFilthy)
	if err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	if err = Record(dir, r); err != nil {
		t.Fatal(err)
	}
	again, err := Load(dir + "/routine-158107-1.json")
	if err != nil || !reflect.DeepEqual(r, again) {
		t.Fatal("recorded snapshot does not round-trip", err)
	}
	if err = Record(dir, r); err != nil {
		t.Fatal(err)
	}
	if _, err = Load(dir + "/routine-158107-2.json"); err != nil {
		t.Fatal("a second review at the same tick overwrote the first", err)
	}
}
