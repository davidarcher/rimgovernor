package snapshot

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Recordings of the retired takeover/* acceptance cases (#748), taken at
// 04b0a98c: each is the first review Auto ran after the case staged a
// player's Manual edit. A Manual edit is an ordinary deficit, never a hold.

func load(t *testing.T, path string) Routine {
	t.Helper()
	r, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	return r
}

func deficit(t *testing.T, r Routine, id policy.GoalID) {
	t.Helper()
	a, err := r.Assessment(id)
	if err != nil || a.Need != domain.NeedDeficit || a.MethodUnavailable {
		t.Fatal(id, a, err)
	}
}

// takeover/schedule, tick 15: a hand-edited timetable (and a NightOwl on the
// native default) makes the work review a deficit the same review corrects.
func TestTakeoverScheduleEditOpensWorkAssignments(t *testing.T) {
	r := load(t, "testdata/takeover-schedule-edited-timetable.json")
	deficit(t, r, policy.EnsureWorkAssignments)
	profiles, _ := r.Facts.WorkProfiles.Value()
	owl := false
	for _, p := range profiles {
		owl = owl || p.Effects.NightShift
	}
	if !owl {
		t.Fatal("recording lost the NightOwl profile the timetable is planned from")
	}
}

// takeover/allowed-areas, tick 15: the player restricted the husky to an area
// without food; with no roof hazard Auto clears it (empty Area).
func TestTakeoverAllowedAreaRestrictionIsCleared(t *testing.T) {
	r := load(t, "testdata/takeover-allowed-areas-restricted-pet.json")
	changes := policy.PlanAllowedAreas(r.Facts)
	if len(changes) != 1 || !changes[0].Animal || changes[0].Area != "" || changes[0].Pawn == "" {
		t.Fatal(changes)
	}
	// History-free: the same census replans the same correction.
	if again := policy.PlanAllowedAreas(r.Facts); len(again) != 1 || again[0] != changes[0] {
		t.Fatal(again)
	}
}

// takeover/suspended-bill, tick 15: the player's suspended kibble bill leaves
// the pet unfed; Auto opens MaintainAnimalFeed with a method available.
func TestTakeoverSuspendedBillOpensAnimalFeed(t *testing.T) {
	deficit(t, load(t, "testdata/takeover-suspended-kibble-bill.json"), policy.MaintainAnimalFeed)
}

// takeover/herd-removal, ticks 32 and 6693: standing Manual release and
// slaughter flags on animals the breeding pair keeps are cancelled, release first.
func TestTakeoverHerdRemovalFlagsAreCancelled(t *testing.T) {
	for path, want := range map[string]domain.HusbandryMethod{
		"testdata/takeover-herd-release-flag.json":   domain.HusbandryCancelRelease,
		"testdata/takeover-herd-slaughter-flag.json": domain.HusbandryCancelSlaughter,
	} {
		r := load(t, path)
		got := policy.ReconcileHerdRemoval(r.Facts.AnimalUpkeep.Animals, policy.HerdFor(r.Facts.AnimalUpkeep.Animals, r.Facts.Wealth), r.Facts.FoodPlan)
		if got.Method != want || got.Animal == "" {
			t.Fatal(path, got)
		}
		if got.Method == domain.HusbandryTame {
			t.Fatal("replacement taming")
		}
	}
}

// takeover/home-removal and takeover/built-facility, tick 15: player-built
// beds with no autonomous build history. The recorded census is covered; a
// player-removed Home cell under a bed opens MaintainHomeCoverage from the
// building census alone.
func TestTakeoverRemovedHomeOpensHomeCoverage(t *testing.T) {
	r := load(t, "testdata/takeover-home-player-beds.json")
	if a, err := r.Assessment(policy.MaintainHomeCoverage); err != nil || a.Need == domain.NeedDeficit {
		t.Fatal("covered colony", a, err)
	}
	home, _ := r.Facts.HomeCoverage.Value()
	home.Targets = append([]policy.HomeCoverageTarget(nil), home.Targets...)
	found := false
	for i, target := range home.Targets {
		if len(target.ID) > 9 && target.ID[:9] == "Thing_Bed" {
			home.Targets[i].Missing = domain.Known(int64(1))
			found = true
			break
		}
	}
	if !found {
		t.Fatal("no player bed in the Home census")
	}
	r.Facts.HomeCoverage = domain.Known(home)
	deficit(t, r, policy.MaintainHomeCoverage)
}
