package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestQuestSpareColonists(t *testing.T) {
	workers := domain.Known([]WorkPawn{{ID: "doctor", Available: domain.Known(true)}, {ID: "spare", Available: domain.Known(true)}})
	states := []MoodPawn{}
	for _, id := range []PawnID{"doctor", "spare"} {
		states = append(states, MoodPawn{ID: id, Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), Mental: domain.Known(false)})
	}
	roster := WorkDecision{Capacity: domain.Known(true), Coverage: []WorkCoverage{{Work: WorkDoctor, Owners: 1}, {Work: WorkCooking, Owners: 0}, {Work: WorkConstruction, Owners: 0}}, Assignments: []PawnWorkAssignment{{Pawn: "doctor", Priorities: []WorkPriority{{Work: WorkDoctor, Priority: 1}}}, {Pawn: "spare", Priorities: []WorkPriority{{Work: WorkDoctor, Priority: 2}}}}}
	check := func(want []PawnID) {
		t.Helper()
		got, known := QuestSpareColonists(workers, domain.Known(states), roster).Value()
		if !known || !slices.Equal(got, want) {
			t.Fatalf("spare = %v, known %v, want %v", got, known, want)
		}
	}
	check([]PawnID{"spare"})
	for _, role := range []WorkType{WorkCooking, WorkConstruction} {
		roster.Coverage = []WorkCoverage{{Work: WorkDoctor}, {Work: WorkCooking}, {Work: WorkConstruction}}
		for i := range roster.Coverage {
			if roster.Coverage[i].Work == role {
				roster.Coverage[i].Owners = 1
			}
		}
		roster.Assignments[0].Priorities[0].Work = role
		roster.Assignments[1].Priorities[0].Work = role
		check([]PawnID{"spare"})
	}
	roster.Coverage = []WorkCoverage{{Work: WorkDoctor, Owners: 1}, {Work: WorkCooking}, {Work: WorkConstruction}}
	roster.Assignments[0].Priorities[0].Work = WorkDoctor
	roster.Assignments[1].Priorities[0].Work = WorkDoctor
	roster.Coverage[0].Owners = 2
	roster.Assignments[1].Priorities[0].Priority = 1
	check([]PawnID{"doctor", "spare"})
	states[1].Drafted = domain.Known(true)
	check([]PawnID{"doctor"})
	states[1].Drafted = domain.Known(false)
	states[1].Downed = domain.Known(true)
	check([]PawnID{"doctor"})
	states[1].Downed = domain.Known(false)
	states[1].Mental = domain.Known(true)
	check([]PawnID{"doctor"})
	states[1].Mental = domain.Unknown[bool]()
	if _, known := QuestSpareColonists(workers, domain.Known(states), roster).Value(); known {
		t.Fatal("unknown pawn status gave known capacity")
	}
	if _, known := QuestSpareColonists(domain.Unknown[[]WorkPawn](), domain.Known(states), roster).Value(); known {
		t.Fatal("unknown workers gave known capacity")
	}
	roster.Capacity = domain.Unknown[bool]()
	if _, known := QuestSpareColonists(workers, domain.Known(states), roster).Value(); known {
		t.Fatal("unknown roster gave known capacity")
	}
}

func TestQuestHomeFloorFollowsCoverage(t *testing.T) {
	ids := []PawnID{"a", "b", "c", "d"}
	workers, states := []WorkPawn{}, []MoodPawn{}
	for _, id := range ids {
		workers = append(workers, WorkPawn{ID: id, Available: domain.Known(true)})
		states = append(states, MoodPawn{ID: id, Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), Mental: domain.Known(false)})
	}
	assign := func(pawn PawnID, work WorkType) PawnWorkAssignment {
		return PawnWorkAssignment{Pawn: pawn, Priorities: []WorkPriority{{Work: work, Priority: 1}}}
	}
	floor := func(owners [3]int, rows ...PawnWorkAssignment) int {
		t.Helper()
		roster := WorkDecision{Capacity: domain.Known(true), Assignments: rows, Coverage: []WorkCoverage{{Work: WorkDoctor, Owners: owners[0]}, {Work: WorkCooking, Owners: owners[1]}, {Work: WorkConstruction, Owners: owners[2]}}}
		got, known := QuestHomeFloor(domain.Known(workers), domain.Known(states), roster).Value()
		if !known {
			t.Fatal("floor unknown")
		}
		return got
	}
	if got := floor([3]int{1, 1, 1}, assign("a", WorkDoctor), assign("b", WorkCooking), assign("c", WorkConstruction), assign("d", "Research")); got != 3 {
		t.Fatalf("sole owners floor = %d, want 3", got)
	}
	if got := floor([3]int{2, 2, 2}, assign("a", WorkDoctor), assign("b", WorkCooking), assign("c", WorkConstruction), assign("d", WorkDoctor)); got != 0 {
		t.Fatalf("redundant owners floor = %d, want 0", got)
	}
	if got := floor([3]int{1, 0, 0}, assign("a", WorkDoctor), assign("b", "Research"), assign("c", "Research"), assign("d", "Research")); got != 1 {
		t.Fatalf("single owner floor = %d, want 1", got)
	}
	if _, known := QuestHomeFloor(domain.Unknown[[]WorkPawn](), domain.Known(states), WorkDecision{}).Value(); known {
		t.Fatal("unknown workers gave a known floor")
	}
}

func TestQuestCalmColony(t *testing.T) {
	facts := RoundsFacts{Hostiles: domain.Known(int64(0))}
	for _, tc := range []struct {
		name         string
		hostiles     domain.Fact[int64]
		decision     EmergencyDecision
		pods         bool
		value, known bool
	}{
		{"calm", domain.Known(int64(0)), EmergencyDecision{Clear: true}, false, true, true},
		{"raid", domain.Known(int64(2)), EmergencyDecision{Clear: true}, false, false, true},
		{"pods", domain.Known(int64(0)), EmergencyDecision{Clear: true}, true, false, true},
		{"medical", domain.Known(int64(0)), EmergencyDecision{Holds: []EmergencyHold{{Reason: EmergencyCriticalMedical}}}, false, false, true},
		{"unknown emergency", domain.Known(int64(0)), EmergencyDecision{Holds: []EmergencyHold{{Reason: EmergencyUnknownFacts}}}, false, false, false},
		{"unknown hostiles", domain.Unknown[int64](), EmergencyDecision{Clear: true}, false, false, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			facts.Hostiles = tc.hostiles
			value, known := QuestCalmColony(facts, tc.decision, tc.pods).Value()
			if value != tc.value || known != tc.known {
				t.Fatalf("calm=%v,%v", value, known)
			}
		})
	}
}
