package snapshot

import (
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Recordings of the retired takeover/* acceptance cases, taken at
// 04b0a98c: each is the first review Auto ran after the case staged a
// player's Manual edit. A Manual edit is an ordinary deficit, never a hold.

func load(t *testing.T, path string) Rounds {
	t.Helper()
	r, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	return r
}

func deficit(t *testing.T, r Rounds, id policy.ConcernID) {
	t.Helper()
	a, err := r.Assessment(id)
	if err != nil || a.Finding != domain.FindingUnmet || a.MethodUnavailable {
		t.Fatal(id, a, err)
	}
}

// takeover/schedule, tick 15, recorded at 476208aa7: the player gave
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
	for _, row := range policy.PlanSchedules(pawns, r.Projection.Facts.Comfort, false).Schedules {
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
	for _, row := range policy.PlanSchedules(pawns, r.Projection.Facts.Comfort, false).Schedules {
		if !row.Matches {
			t.Fatalf("%s still replanned after wearing its template", row.Pawn)
		}
	}
}

// takeover/herd-removal, ticks 32 and 6693: standing Manual release and
// slaughter flags on animals the breeding pair keeps are cancelled, release first.
func TestTakeoverHerdRemovalFlagsAreCancelled(t *testing.T) {
	for path, want := range map[string]domain.HusbandryMethod{
		"testdata/takeover-herd-release-flag.json":   domain.HusbandryCancelRelease,
		"testdata/takeover-herd-slaughter-flag.json": domain.HusbandryCancelSlaughter,
	} {
		r := load(t, path)
		got := policy.ReconcileHerdRemoval(r.Facts.AnimalUpkeep.Animals, r.Facts.HerdPolicy(), r.Facts.FoodPlan)
		if got.Method != want || got.Animal == "" {
			t.Fatal(path, got)
		}
		if got.Method == domain.HusbandryTame {
			t.Fatal("replacement taming")
		}
	}
}

// takeover/home-removal and takeover/built-facility, tick 15: the first
// review after the player's Manual edit, recorded at 476208aa7.
// home-removal removed one Home cell under a player-built bed (every
// target over that room reads one cell missing); built-facility built the
// bed room with no Home at all (the room's 49 cells missing). The recordings
// predate the home-cell read: with no Home at all, MaintainHomeCoverage
// opens from the building census alone, and once Home holds the planned
// footprint it does not.
func TestTakeoverRemovedHomeOpensHomeCoverage(t *testing.T) {
	for path, missing := range map[string]int64{
		"testdata/takeover-home-removed-cell.json.gz":   1,
		"testdata/takeover-home-built-facility.json.gz": 49,
	} {
		r := load(t, path)
		home, _ := r.Facts.HomeCoverage.Value()
		beds := 0
		for _, target := range home.Targets {
			n, _ := target.Missing.Value()
			if strings.HasPrefix(target.ID, "Thing_Bed") {
				beds++
				if n != missing {
					t.Fatalf("%s: %s misses %d Home cells, want the edit's %d", path, target.ID, n, missing)
				}
			}
		}
		if beds == 0 {
			t.Fatal(path, "no player bed in the Home census")
		}
		home.Home, home.AutoHome = domain.Known([]domain.Cell{}), domain.Known(false)
		r.Facts.HomeCoverage = domain.Known(home)
		deficit(t, r, policy.MaintainHomeCoverage)
		planned, err := policy.PlanHomeArea(r.Facts.MapBounds, r.Facts.CurrentConstruction, r.Facts.ConstructionClaims, r.Facts.HomeCoverage, r.Facts.RangeHold)
		plan, known := planned.Value()
		if err != nil || !known || len(plan.Set) == 0 {
			t.Fatal(path, plan, known, err)
		}
		home.Home = domain.Known(plan.Set)
		r.Facts.HomeCoverage = domain.Known(home)
		if a, err := r.Assessment(policy.MaintainHomeCoverage); err != nil || a.Finding == domain.FindingUnmet {
			t.Fatal(path, "covered colony still a deficit", a, err)
		}
	}
}
