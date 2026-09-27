package snapshot

import (
	"slices"
	"strings"
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

// takeover/schedule, tick 15, recorded at 476208aa7 (#769): the player gave
// one colonist (Thing_Human728) a Joy hour at noon under Manual. The
// review's pawn read carries every timetable; the schedule planner replans
// that colonist's noon to Anything, so the work review is a deficit, and a
// colony wearing the planned timetables has nothing left to replan.
func TestTakeoverScheduleEditOpensWorkAssignments(t *testing.T) {
	r := load(t, "testdata/takeover-schedule-edited.json.gz")
	deficit(t, r, policy.EnsureWorkAssignments)
	pawns, known := r.Projection.WorkPawns.Value()
	if !known {
		t.Fatal("recording carries no pawn read")
	}
	const edited = policy.PawnID("Thing_Human728")
	planned := map[policy.PawnID][]string{}
	for _, row := range policy.PlanSchedules(pawns).Schedules {
		planned[row.Pawn] = row.Slots
		if row.Pawn == edited && (row.Matches || row.Slots[12] != policy.ScheduleAnything) {
			t.Fatalf("edited timetable not replanned: %+v", row)
		}
	}
	pawns = slices.Clone(pawns)
	for i := range pawns {
		slots, _ := pawns[i].Schedule.Value()
		if pawns[i].ID == edited && (len(slots) != 24 || slots[12] != policy.ScheduleJoy) {
			t.Fatalf("recording lost the player's Joy hour: %v", slots)
		}
		if want, ok := planned[pawns[i].ID]; ok {
			pawns[i].Schedule = domain.Known(want)
		}
	}
	for _, row := range policy.PlanSchedules(pawns).Schedules {
		if !row.Matches {
			t.Fatalf("%s still replanned after wearing its template", row.Pawn)
		}
	}
}

// takeover/allowed-areas, tick 15, recorded at 476208aa7 (#769): the player
// restricted a colonist (Thing_Human724) and the husky to an area without
// food. With no roof hazard Auto clears both (empty Area), and once the
// census reads them unrestricted nothing is replanned.
func TestTakeoverAllowedAreaRestrictionIsCleared(t *testing.T) {
	r := load(t, "testdata/takeover-allowed-areas-restricted.json.gz")
	changes := policy.PlanAllowedAreas(r.Facts)
	colonist, animal := false, false
	for _, c := range changes {
		if c.Area != "" {
			t.Fatal("restricted instead of cleared", c)
		}
		colonist = colonist || !c.Animal && c.Pawn == "Thing_Human724"
		animal = animal || c.Animal && c.Pawn == "Thing_Husky44693"
	}
	if len(changes) != 2 || !colonist || !animal {
		t.Fatal(changes)
	}
	safety, _ := r.Facts.RecoverySafety.Value()
	safety.Restrictions = slices.Clone(safety.Restrictions)
	for i := range safety.Restrictions {
		safety.Restrictions[i].Area = domain.Known("")
	}
	r.Facts.RecoverySafety = domain.Known(safety)
	animals, _ := r.Facts.AnimalUpkeep.Animals.Value()
	animals = slices.Clone(animals)
	for i := range animals {
		animals[i].AllowedArea = domain.Known("")
	}
	r.Facts.AnimalUpkeep.Animals = domain.Known(animals)
	if after := policy.PlanAllowedAreas(r.Facts); len(after) != 0 {
		t.Fatal("replanned after the restrictions cleared", after)
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
		got := policy.ReconcileHerdRemoval(r.Facts.AnimalUpkeep.Animals, policy.HerdFor(r.Facts.AnimalUpkeep.Animals, r.Facts.Wealth, r.Facts.PenGrazing), r.Facts.FoodPlan)
		if got.Method != want || got.Animal == "" {
			t.Fatal(path, got)
		}
		if got.Method == domain.HusbandryTame {
			t.Fatal("replacement taming")
		}
	}
}

// takeover/home-removal and takeover/built-facility, tick 15: the first
// review after the player's Manual edit, recorded at 476208aa7 (#769).
// home-removal removed one Home cell under a player-built bed (every
// target over that room reads one cell missing); built-facility built the
// bed room with no Home at all (the room's 49 cells missing). Neither has
// autonomous build history: MaintainHomeCoverage opens from the building
// census alone, and once the recorded gap reads covered it does not.
func TestTakeoverRemovedHomeOpensHomeCoverage(t *testing.T) {
	for path, missing := range map[string]int64{
		"testdata/takeover-home-removed-cell.json.gz":   1,
		"testdata/takeover-home-built-facility.json.gz": 49,
	} {
		r := load(t, path)
		deficit(t, r, policy.MaintainHomeCoverage)
		home, _ := r.Facts.HomeCoverage.Value()
		home.Targets = slices.Clone(home.Targets)
		beds := 0
		for i, target := range home.Targets {
			n, _ := target.Missing.Value()
			if strings.HasPrefix(target.ID, "Thing_Bed") {
				beds++
				if n != missing {
					t.Fatalf("%s: %s misses %d Home cells, want the edit's %d", path, target.ID, n, missing)
				}
			}
			home.Targets[i].Missing = domain.Known(int64(0))
		}
		if beds == 0 {
			t.Fatal(path, "no player bed in the Home census")
		}
		r.Facts.HomeCoverage = domain.Known(home)
		if a, err := r.Assessment(policy.MaintainHomeCoverage); err != nil || a.Need == domain.NeedDeficit {
			t.Fatal(path, "covered colony still a deficit", a, err)
		}
	}
}
