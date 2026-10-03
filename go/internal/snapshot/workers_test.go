package snapshot

import (
	"fmt"
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The workers/* recordings are the three seeded debug-start colonists as
// the routine census's pawn read lifted them (observation.WorkPawnRow),
// captured from the native acceptance cases workers/passion, traits,
// coverage, nightowl and helpers at 04b0a98c (#748) before those cases
// were retired. "before" is WorkersFixture's seeded sheet; "after" is the
// readback once the planned matrix (and, for nightowl, the timetables)
// went through native work settings writes. Role order (A, B, C) is the fixture's.
// Every run seeded the same three debug-start pawns.
const roleA, roleB, roleC = "Thing_Human79", "Thing_Human82", "Thing_Human85"

func loadPawns(t *testing.T, name string) []policy.WorkPawn {
	t.Helper()
	data, err := os.ReadFile("testdata/" + name + ".json")
	if err != nil {
		t.Fatal(err)
	}
	var pawns []policy.WorkPawn
	if err = Decode(data, &pawns); err != nil {
		t.Fatal(err)
	}
	if len(pawns) != 3 {
		t.Fatalf("%s: %d pawns, want 3", name, len(pawns))
	}
	resolveWorkRows(t, pawns)
	return pawns
}

func workPriority(d policy.WorkDecision, pawn policy.PawnID, work policy.WorkType) int {
	for _, a := range d.Assignments {
		if a.Pawn != pawn {
			continue
		}
		for _, p := range a.Priorities {
			if p.Work == work {
				return p.Priority
			}
		}
	}
	return -1
}

func workCoverage(d policy.WorkDecision, work policy.WorkType) (policy.WorkCoverage, bool) {
	for _, c := range d.Coverage {
		if c.Work == work {
			return c, true
		}
	}
	return policy.WorkCoverage{}, false
}

func workDisabled(pawns []policy.WorkPawn, id policy.PawnID, work policy.WorkType) bool {
	for _, p := range pawns {
		if p.ID != id {
			continue
		}
		rows, _ := p.Work.Value()
		for _, r := range rows {
			if r.Work == work {
				return r.Disabled
			}
		}
	}
	return false
}

// snapshotDemand plans under a haul backlog: the captured readbacks were
// written when Hauling and Cleaning always sat at 3 (before #1278).
var snapshotDemand = policy.WorkDemand{HaulBacklog: true}

// rosterCheck plans the seeded sheet and the written readback, checks the
// scenario's rows on both, and requires the readback to match the plan
// unchanged (what runRoster asserted natively).
func rosterCheck(t *testing.T, scenario string, verify func([]policy.WorkPawn, policy.WorkDecision) error) {
	before := loadPawns(t, "workers-"+scenario+"-before")
	after := loadPawns(t, "workers-"+scenario+"-after")
	decision, err := policy.PlanWork(before, nil, snapshotDemand)
	if err != nil {
		t.Fatal(err)
	}
	if capacity, ok := decision.Capacity.Value(); !ok || !capacity {
		t.Fatalf("before: no capacity: %+v", decision.Coverage)
	}
	if matches, _ := decision.Matches.Value(); matches {
		t.Fatalf("before: the seeded sheet already matched the plan")
	}
	if err := verify(before, decision); err != nil {
		t.Fatalf("before: %v", err)
	}
	replan, err := policy.PlanWork(after, nil, snapshotDemand)
	if err != nil {
		t.Fatal(err)
	}
	if matches, _ := replan.Matches.Value(); !matches {
		t.Fatalf("after: the written sheet does not match the plan: %+v", replan.Assignments)
	}
	if err := verify(after, replan); err != nil {
		t.Fatalf("after: %v", err)
	}
	for _, a := range decision.Assignments {
		for _, p := range a.Priorities {
			if got := workPriority(replan, a.Pawn, p.Work); got != p.Priority {
				t.Fatalf("%s %s planned %d before the write and %d after it", a.Pawn, p.Work, p.Priority, got)
			}
		}
	}
}

// workers/passion: two colonists tied at Cooking 12; the major passion (A)
// owns the kitchen at 1, the other (B) backs it at 2, C stays under the
// food-poisoning floor.
func TestWorkersPassionOwnsTiedKitchen(t *testing.T) {
	const a, b, c = roleA, roleB, roleC
	rosterCheck(t, "passion", func(pawns []policy.WorkPawn, d policy.WorkDecision) error {
		if workDisabled(pawns, a, policy.WorkCooking) || workDisabled(pawns, b, policy.WorkCooking) {
			return fmt.Errorf("recorded cooks cannot cook")
		}
		if got := workPriority(d, a, policy.WorkCooking); got != 1 {
			return fmt.Errorf("major passion cook planned Cooking %d, want 1", got)
		}
		if got := workPriority(d, b, policy.WorkCooking); got != 2 {
			return fmt.Errorf("tied passionless cook planned Cooking %d, want 2", got)
		}
		if got := workPriority(d, c, policy.WorkCooking); got != 0 && got != 4 {
			return fmt.Errorf("cook at 3 planned Cooking %d, want under the floor", got)
		}
		if row, ok := workCoverage(d, policy.WorkCooking); !ok || row.Demand != 1 || row.Owners != 1 {
			return fmt.Errorf("cooking coverage %+v, want one owner", row)
		}
		return nil
	})
}

// workers/traits: A (Pyromaniac, Brawler, Abrasive) never fights fires,
// hunts or wardens; Industrious B wins a Construction sheet tied with C.
func TestWorkersTraitsForbidAndLift(t *testing.T) {
	const a, b, c = roleA, roleB, roleC
	rosterCheck(t, "traits", func(pawns []policy.WorkPawn, d policy.WorkDecision) error {
		for _, work := range []policy.WorkType{policy.WorkFirefighter, policy.WorkHunting, policy.WorkWarden} {
			if got := workPriority(d, a, work); got > 0 {
				return fmt.Errorf("pyromaniac brawler abrasive planned %s %d, want 0", work, got)
			}
		}
		if got := workPriority(d, a, policy.WorkHunting); got != 0 {
			return fmt.Errorf("brawler planned Hunting %d, want the row at 0", got)
		}
		if workDisabled(pawns, b, policy.WorkConstruction) || workDisabled(pawns, c, policy.WorkConstruction) {
			return fmt.Errorf("recorded builders cannot build")
		}
		if got := workPriority(d, b, policy.WorkConstruction); got != 1 {
			return fmt.Errorf("industrious builder planned Construction %d, want 1", got)
		}
		if got := workPriority(d, c, policy.WorkConstruction); got == 1 {
			return fmt.Errorf("tied builder without the trait also owns Construction")
		}
		return nil
	})
}

// workers/coverage: a flat sheet still covers every core role with one
// owner, Firefighter pinned at 1, Hauling 3 under the backlog (4 for the
// research owner).
func TestWorkersCoverageOwnsEveryCoreRole(t *testing.T) {
	ids := []policy.PawnID{roleA, roleB, roleC}
	rosterCheck(t, "coverage", func(pawns []policy.WorkPawn, d policy.WorkDecision) error {
		for _, work := range []policy.WorkType{policy.WorkDoctor, policy.WorkCooking, policy.WorkConstruction, policy.WorkGrowing} {
			row, ok := workCoverage(d, work)
			if !ok || row.Owners != 1 || row.Demand != 1 {
				return fmt.Errorf("core role %s coverage %+v, want one owner", work, row)
			}
			owners := 0
			for _, id := range ids {
				if workPriority(d, id, work) == 1 {
					owners++
				}
			}
			if owners != 1 {
				return fmt.Errorf("%d pawns own %s at 1, want one", owners, work)
			}
		}
		for _, id := range ids {
			if got := workPriority(d, id, policy.WorkFirefighter); got != 1 && !workDisabled(pawns, id, policy.WorkFirefighter) {
				return fmt.Errorf("%s planned Firefighter %d, want 1", id, got)
			}
			want := 3
			if workPriority(d, id, policy.WorkResearch) == 1 {
				want = 4
			}
			if got := workPriority(d, id, policy.WorkHauling); got != want {
				return fmt.Errorf("%s planned Hauling %d, want %d", id, got, want)
			}
		}
		return nil
	})
}

func pawnSchedule(pawns []policy.WorkPawn, id policy.PawnID) []string {
	for _, p := range pawns {
		if p.ID == id {
			slots, _ := p.Schedule.Value()
			return slots
		}
	}
	return nil
}

func countSlots(slots []string, def string) int {
	n := 0
	for _, s := range slots {
		if s == def {
			n++
		}
	}
	return n
}

// workers/nightowl: a NightOwl sleeps by day, a QuickSleeper gets a
// six-hour sleep, and a hand-edited timetable (Joy at hour 12) is
// replanned (#461); the written work rows match.
func TestWorkersNightOwlSchedules(t *testing.T) {
	const owl, quick, edited = roleA, roleB, roleC
	before := loadPawns(t, "workers-nightowl-before")
	if s := pawnSchedule(before, edited); len(s) != 24 || s[12] != policy.ScheduleJoy {
		t.Fatalf("the player-edited timetable was not recorded: %v", s)
	}
	wanted := map[policy.PawnID][]string{}
	for _, row := range policy.PlanSchedules(before, domain.Unknown[policy.ComfortObservation](), false).Schedules {
		if row.Matches {
			t.Fatalf("%s already matches its template", row.Pawn)
		}
		wanted[row.Pawn] = row.Slots
	}
	if len(wanted) != 3 || wanted[owl] == nil || wanted[quick] == nil || wanted[edited] == nil {
		t.Fatalf("expected all three planned, got %v", wanted)
	}
	if wanted[edited][12] != policy.ScheduleAnything {
		t.Fatalf("the edited template keeps the Joy hour: %v", wanted[edited])
	}
	if countSlots(wanted[owl], policy.ScheduleSleep) != 8 || wanted[owl][0] != policy.ScheduleAnything || wanted[owl][9] != policy.ScheduleJoy || wanted[owl][12] != policy.ScheduleSleep {
		t.Fatalf("night owl template is not a night shift: %v", wanted[owl])
	}
	if countSlots(wanted[quick], policy.ScheduleSleep) != 6 {
		t.Fatalf("quick sleeper template is not a six-hour sleep: %v", wanted[quick])
	}
	// The after recording wears the pre-#1314 templates (a Work night
	// shift), so only its work rows are still pinned here.
	after := loadPawns(t, "workers-nightowl-after")
	work, err := policy.PlanWork(after, nil, snapshotDemand)
	if err != nil {
		t.Fatal(err)
	}
	if matches, _ := work.Matches.Value(); !matches {
		t.Fatalf("after: the work rows written beside the timetables do not match: %+v", work.Assignments)
	}
}

// workers/helpers (#653): beside six ready wood walls, two reviews of
// planning (spare capacity, then assignment) enable both sub-floor pawns
// at Construction 4 beside the skilled builder's 1. The native half (a
// helper finishing a wall with the builder drafted) is vanilla job
// behaviour and is not replayed.
func TestWorkersHelpersEnabledUnderTheFloor(t *testing.T) {
	const builder, a, b = roleA, roleB, roleC
	pawns := loadPawns(t, "workers-helpers-before")
	for _, id := range []policy.PawnID{builder, a, b} {
		if workDisabled(pawns, id, policy.WorkConstruction) {
			t.Fatalf("%s cannot build", id)
		}
	}
	ready := &policy.ReadyWorkReport{}
	for i := 0; i < 6; i++ {
		ready.Candidates = append(ready.Candidates, policy.ReadyWork{Stage: "building:Wall", Work: policy.LaborProfile{policy.WorkConstruction}, State: policy.ReadyRunnable, Parallelism: 1, Adapter: policy.ReadyMigrated, Claims: []policy.ReadyClaim{{Kind: "cell", Key: fmt.Sprint(i)}}})
	}
	plan := func(tick domain.Tick, previous *policy.ConstructionHelpRecord) policy.WorkDecision {
		help := policy.ConstructionHelpDemand(ready, domain.GenerationSnapshot{}, tick, []string{"Wall"}, previous)
		d, err := policy.PlanWork(pawns, nil, policy.WorkDemand{Construction: true, Help: &help})
		if err != nil {
			t.Fatal(err)
		}
		if d.Help == nil {
			t.Fatalf("tick %d: no helper record", tick)
		}
		return d
	}
	first := plan(1, nil)
	d := plan(2, first.Help)
	if workPriority(d, builder, policy.WorkConstruction) != 1 || workPriority(d, a, policy.WorkConstruction) != 4 || workPriority(d, b, policy.WorkConstruction) != 4 {
		t.Fatalf("want builder 1 and helpers 4, got %+v (help %+v, first %+v)", d.Assignments, d.Help, first.Help)
	}
}
