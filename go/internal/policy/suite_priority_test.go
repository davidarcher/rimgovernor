package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSuitePressureSumsListedThoughts(t *testing.T) {
	got := SuitePressure([]MoodPawn{
		{ID: "a", Thoughts: domain.Known([]MoodThought{{Def: "NeedRoomSize", Offset: -5}, {Def: "Jealous", Offset: -3}, {Def: "SharedBed", Offset: -2}, {Def: "Hungry", Offset: -20}})},
		{ID: "b", Thoughts: domain.Known([]MoodThought{{Def: "Hungry", Offset: -20}})},
		{ID: "c", Thoughts: domain.Unknown[[]MoodThought]()},
	})
	if got["a"] != -10 || got["b"] != 0 || len(got) != 2 {
		t.Fatalf("pressure = %v, want a -10, b 0, c absent", got)
	}
}

func TestSuiteClaimsOrderByPressure(t *testing.T) {
	plan, rooms, sleeping := suiteFixture()
	cramped := domain.Known(RoomQuality{Wealth: 1500, Beauty: 3, Space: 5, Impressiveness: 15})
	sleeping.Rooms = domain.Known([]UpkeepRoom{{ID: "r1", Role: "Bedroom", Quality: cramped}, {ID: "r2", Role: "Bedroom", Quality: cramped}})
	targets := suiteTargetsFor(sleeping, nil)
	pressure := SuitePressure([]MoodPawn{
		{ID: "a", Thoughts: domain.Known([]MoodThought{{Def: "Hungry", Offset: -20}})},
		{ID: "b", Thoughts: domain.Known([]MoodThought{{Def: "SleptInBedroom", Offset: -4}})},
	})
	got := SuiteClaims(plan, rooms, sleeping, targets, nil, pressure, RoomGate{})
	if len(got) != 2 || got[0].Pawn != "b" || got[1].Pawn != "a" {
		t.Fatalf("claims = %+v, want b (bedroom thought) before a", got)
	}
	// Without pressure the queue falls back to pawn id.
	if plain := SuiteClaims(plan, rooms, sleeping, targets, nil, nil, RoomGate{}); plain[0].Pawn != "a" {
		t.Fatalf("no-pressure claims = %+v", plain)
	}
	// The first claim in priority order is walked into the next suite.
	rooms.Rooms = append(rooms.Rooms, Room{ID: "s", Role: domain.Known(RoomRoleBedroom), Enclosed: domain.Known(true), Beds: []string{"sbed"}, Cells: []domain.Cell{{X: 33, Z: 4}}})
	sleeping.Beds = append(sleeping.Beds, SleepingBed{ID: "sbed", Definition: "Bed", Room: domain.Known("s"), AccessibleTo: []PawnID{"a", "b"}})
	if step := nextSuiteStep(plan, rooms, sleeping, got); step.Kind != BedroomMove || step.Pawn != "b" {
		t.Fatalf("step = %+v, want b moved into the suite", step)
	}
}
