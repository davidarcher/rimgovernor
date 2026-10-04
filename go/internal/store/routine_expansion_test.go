package store

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestExpansionDurableRenewalAndUnknown(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	r.Policy.Stage.Floor = policy.StageDevelopment
	r.Facts.Colonists = domain.Known(int64(3))
	r.Facts.BedCapacity = domain.Known(int64(3))
	r.Facts.IndoorCapacity = domain.Known(int64(3))
	r.Facts.Wood = domain.Known(int64(500))
	r.Facts.Sleeping = housedSleeping(3)
	out := reviewRoutine(t, s, &r)
	first := routineGoal(t, out, policy.MaintainHousing)
	if first.Standard.Need != domain.NeedDeficit || !developmentRow(t, out.Review, policy.MaintainHousing).Selected {
		t.Fatal(out)
	}
	r.Facts.IndoorCapacity = domain.Unknown[int64]()
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainHousing); g.Standard.Need != domain.NeedUnknown || g.Standard.Episode != first.Standard.Episode {
		t.Fatal(g)
	}
	r.Enabled = false
	out = reviewRoutine(t, s, &r)
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRounds(ctx)
	if err != nil || loaded.Enabled || loaded.Revision != out.Review.Revision {
		t.Fatal(loaded, err)
	}
	r.Enabled = true
	r.Facts.IndoorCapacity = domain.Known(int64(4))
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainHousing); g.Standard.Need != domain.NeedRecovered {
		t.Fatal(g)
	}
	recovered := routineGoal(t, out, policy.MaintainHousing)
	r.Facts.Colonists = domain.Known(int64(4))
	r.Facts.BedCapacity = domain.Known(int64(4))
	r.Facts.Sleeping = housedSleeping(4)
	out = reviewRoutine(t, s, &r)
	if g := routineGoal(t, out, policy.MaintainHousing); g.Standard.Need != domain.NeedDeficit || g.Standard.Episode <= recovered.Standard.Episode {
		t.Fatal(g, recovered)
	}
}

// housedSleeping is n colonists each sleeping in their own roofed bed:
// MaintainHousing's bedroom phase recovered, so expansion decides it.
func housedSleeping(n int) domain.Fact[policy.SleepingObservation] {
	v := policy.SleepingObservation{Colonists: n}
	for i := range n {
		pawn, bed := policy.PawnID(fmt.Sprint("pawn", i)), fmt.Sprint("bed", i)
		v.People = append(v.People, policy.SleepingPerson{ID: pawn, OwnedBed: domain.Known(bed), ComfortableMin: domain.Known(10.0), ComfortableMax: domain.Known(30.0)})
		v.Beds = append(v.Beds, policy.SleepingBed{ID: bed, Definition: "Bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Roofed: domain.Known(true), RestEffectiveness: domain.Known(1.0), Temperature: domain.Known(20.0), Owners: []policy.PawnID{pawn}, Users: []policy.PawnID{pawn}, AccessibleTo: []policy.PawnID{pawn}})
	}
	return domain.Known(v)
}

func TestRoutineCapabilitiesPreserveCommittedExpansion(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	r.Policy.Stage.Floor = policy.StageDevelopment
	r.Facts.Colonists = domain.Known(int64(3))
	r.Facts.BedCapacity = domain.Known(int64(3))
	r.Facts.IndoorCapacity = domain.Known(int64(3))
	r.Facts.AvailableMethods = domain.Known([]policy.ConcernID{policy.MaintainHousing})
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.MaintainHousing)
	if !developmentRow(t, out.Review, policy.MaintainHousing).Selected {
		t.Fatal(out)
	}
	if _, err := s.CommitGoalMethod(ctx, g.Standard.ID, g.Revision, "expansion", plan(t, "expansion", "additional-place")); err != nil {
		t.Fatal(err)
	}
	r.Facts.AvailableMethods = domain.Known([]policy.ConcernID{})
	out = reviewRoutine(t, s, &r)
	row := developmentRow(t, out.Review, policy.MaintainHousing)
	if row.Selected || !row.Committed || row.Reason != policy.DevelopmentCommitted {
		t.Fatal(row)
	}
	if got := routineGoal(t, out, policy.MaintainHousing); got.Standard.Need != domain.NeedDeficit || len(got.Methods) != 1 {
		t.Fatal(got)
	}
}
