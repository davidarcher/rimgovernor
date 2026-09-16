package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestSleepingUseSurvivesManualRestartAndResetsWithWorld(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "sleep.db")
	s := open(t, path)
	r := routineRequest()
	v := policy.SleepingObservation{Colonists: 1, People: []policy.SleepingPerson{{ID: "pawn", OwnedBed: domain.Known("bed"), ComfortableMin: domain.Known(10.0), ComfortableMax: domain.Known(30.0)}}, Beds: []policy.SleepingBed{{ID: "bed", Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Roofed: domain.Known(true), RestEffectiveness: domain.Known(1.0), Temperature: domain.Known(20.0), Owners: []policy.PawnID{"pawn"}, Users: []policy.PawnID{"pawn"}, AccessibleTo: []policy.PawnID{"pawn"}}}}
	r.Facts.Sleeping = domain.Known(v)
	out := reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainSleeping).Goal.Need != domain.NeedRecovered || len(out.Review.Sleeping.Uses) != 1 {
		t.Fatal(out)
	}
	r.Enabled = false
	reviewRoutine(t, s, &r)
	s.Close()
	s = open(t, path)
	defer s.Close()
	saved, err := s.LoadRoutineReview(context.Background())
	if err != nil || saved.Enabled || len(saved.Sleeping.Uses) != 1 {
		t.Fatal(saved, err)
	}
	r.Enabled = true
	r.Current.Native++
	v.Beds[0].Users = nil
	r.Facts.Sleeping = domain.Known(v)
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainSleeping).Goal.Need != domain.NeedRecovered {
		t.Fatal(out)
	}
	r.Facts.Sleeping = domain.Unknown[policy.SleepingObservation]()
	r.Facts.SleepingRecovered = domain.Known(true)
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainSleeping).Goal.Need != domain.NeedUnknown || len(out.Review.Sleeping.Uses) != 1 {
		t.Fatal(out)
	}
	r.Facts.Sleeping = domain.Known(v)
	r.Current.Load = "replacement"
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainSleeping).Goal.Need != domain.NeedDeficit || len(out.Review.Sleeping.Uses) != 0 {
		t.Fatal(out)
	}
}
