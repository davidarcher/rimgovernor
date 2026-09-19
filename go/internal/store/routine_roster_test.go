package store

import (
	"context"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A review that planned work records the roster report, a review without a
// known census (or a disabled one) keeps the last report, and the record
// survives a reload byte for byte (#448).
func TestRoutineRosterRecordedAndKept(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	none := reviewRoutine(t, s, &r)
	if none.Review.Roster != nil {
		t.Fatal("roster recorded without a census")
	}
	profile := policy.BuildProfile(policy.WorkPawn{ID: "p1", Traits: domain.Known([]policy.PawnTrait{{Name: "Brawler"}}), Skills: domain.Known([]policy.WorkSkill{{Name: "Melee", Level: 14, Stored: 14, Passion: "Minor"}}), Incapable: domain.Known([]policy.WorkType{policy.WorkResearch}), Age: domain.Known(41.25)})
	r.Facts.WorkRoster = domain.Known([]policy.WorkCoverage{{Work: policy.WorkHunting, Demand: 1, Owners: 0, Capable: 0}})
	r.Facts.WorkDecaying = domain.Known([]policy.DecayingSkill{{Pawn: "p1", Skill: "Melee", Level: 14}})
	r.Facts.WorkProfiles = domain.Known([]policy.PawnProfile{profile})
	r.Tick += 10
	planned := reviewRoutine(t, s, &r)
	got := planned.Review.Roster
	if got == nil || got.Tick != r.Tick-1 || len(got.Coverage) != 1 || len(got.Decaying) != 1 || len(got.Profiles) != 1 || !reflect.DeepEqual(got.Profiles[0], profile) {
		t.Fatalf("roster not recorded: %+v", got)
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded.Roster, got) {
		t.Fatal(loaded.Roster, err)
	}
	r.Facts.WorkRoster = domain.Unknown[[]policy.WorkCoverage]()
	r.Tick += 10
	unknown := reviewRoutine(t, s, &r)
	if !reflect.DeepEqual(unknown.Review.Roster, got) {
		t.Fatalf("unknown census dropped the report: %+v", unknown.Review.Roster)
	}
	r.Enabled = false
	r.Tick += 10
	stopped := reviewRoutine(t, s, &r)
	if !reflect.DeepEqual(stopped.Review.Roster, got) {
		t.Fatalf("disabled review dropped the report: %+v", stopped.Review.Roster)
	}
}
