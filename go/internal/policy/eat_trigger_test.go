package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func eatColony(days float64, colonists int64) EatTriggerColony {
	return EatTriggerColony{FoodDays: domain.Known(days), Colonists: domain.Known(colonists), EmergencyDays: 3, MinDays: 3}
}

func TestWantEatTrigger(t *testing.T) {
	for _, tc := range []struct {
		name   string
		colony EatTriggerColony
		want   float64
		ok     bool
	}{
		{"solo with runway", eatColony(20, 1), EatTriggerLowered, true},
		{"group with runway", eatColony(20, 4), EatTriggerVanilla, true},
		{"solo below emergency", eatColony(2, 1), EatTriggerVanilla, true},
		{"group below emergency", eatColony(2, 4), EatTriggerVanilla, true},
		{"group below seasonal minimum", EatTriggerColony{FoodDays: domain.Known(4.0), Colonists: domain.Known(int64(4)), EmergencyDays: 3, MinDays: 6}, EatTriggerLowered, true},
		{"unknown colonists with runway", EatTriggerColony{FoodDays: domain.Known(20.0), EmergencyDays: 3, MinDays: 3}, EatTriggerVanilla, true},
		{"unknown runway", EatTriggerColony{Colonists: domain.Known(int64(1)), EmergencyDays: 3, MinDays: 3}, 0, false},
		{"nan runway", eatColony(math.NaN(), 1), 0, false},
	} {
		got, ok := WantEatTrigger(tc.colony)
		if got != tc.want || ok != tc.ok {
			t.Errorf("%s: got %v %v, want %v %v", tc.name, got, ok, tc.want, tc.ok)
		}
	}
}

func TestEatTriggerChangesSkipMatchingUnknownAndGuests(t *testing.T) {
	pawns := []EatTriggerPawn{
		{ID: "vanilla", Current: domain.Known(0.30), Read: true},
		{ID: "lowered", Current: domain.Known(float64(float32(EatTriggerLowered))), Read: true},
		{ID: "unknown", Read: true},
		{ID: "guest", Current: domain.Known(0.30), Read: true, Guest: true},
		{ID: "unread", Current: domain.Known(0.30)},
	}
	changes := EatTriggerChanges(pawns, eatColony(20, 1))
	if len(changes) != 1 || changes[0].Pawn() != "vanilla" {
		t.Fatal(changes)
	}
	if level, ok := changes[0].EatTrigger(); !ok || level != EatTriggerLowered {
		t.Fatal(level, ok)
	}
	// Dropping under the emergency line returns the lowered pawn to vanilla.
	back := EatTriggerChanges(pawns, eatColony(2, 1))
	if len(back) != 1 || back[0].Pawn() != "lowered" {
		t.Fatal(back)
	}
	if level, _ := back[0].EatTrigger(); level != EatTriggerVanilla {
		t.Fatal(level)
	}
	if len(EatTriggerChanges(pawns, EatTriggerColony{})) != 0 {
		t.Fatal("unknown runway wrote")
	}
}

func TestEatTriggerOwed(t *testing.T) {
	census := []WorkPawn{{ID: "solo", EatTrigger: domain.Known(0.30), PolicyInputs: domain.Known(PawnPolicyInputs{})}}
	if owed, ok := EatTriggerOwed(domain.Known(census), eatColony(20, 1)).Value(); !ok || !owed {
		t.Fatal(owed, ok)
	}
	if owed, ok := EatTriggerOwed(domain.Known(census), eatColony(20, 3)).Value(); !ok || owed {
		t.Fatal(owed, ok)
	}
	if _, ok := EatTriggerOwed(domain.Unknown[[]WorkPawn](), eatColony(20, 1)).Value(); ok {
		t.Fatal("unknown census is known")
	}
}

// Eating at 12.5% instead of 30% stretches 57 survival meals by about 6-7
// days for one colonist (spike #2768: 24.9 days become about 31).
func TestEatTriggerDaysSavedFor57Meals(t *testing.T) {
	runway := MealRunwayDays(57)
	if math.Abs(runway-24.9) > 0.1 {
		t.Fatal(runway)
	}
	saved := EatTriggerDaysSaved(runway, EatTriggerLowered)
	if saved < 6 || saved > 7 {
		t.Fatal(saved)
	}
	if EatTriggerDaysSaved(runway, EatTriggerVanilla) != 0 || EatTriggerDaysSaved(0, EatTriggerLowered) != 0 {
		t.Fatal("vanilla or empty runway saved days")
	}
}
