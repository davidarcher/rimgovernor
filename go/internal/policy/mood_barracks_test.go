package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSleptInBarracksRaisesTheHousingDeficit(t *testing.T) {
	p := moodPawn()
	p.Food = domain.Known(.8)
	p.Thoughts = domain.Known([]MoodThought{{"SleptInBarracks", -4}})
	h := moodReview(t, p, MoodHistory{})
	if got := MoodProvisionDeficits(h)[MaintainHousing]; got != 1 {
		t.Fatalf("barracks thought housing deficit = %v, want 1", got)
	}
	if len(h.States[0].Unowned) != 0 {
		t.Fatalf("owned thought recorded as unowned: %+v", h.States[0].Unowned)
	}
	// Absent thought, or a positive stage (an immune pawn has no row): no deficit.
	p.Thoughts = domain.Known([]MoodThought{})
	if d := MoodProvisionDeficits(moodReview(t, p, MoodHistory{})); d != nil {
		t.Fatalf("deficit without the thought: %v", d)
	}
	p.Thoughts = domain.Known([]MoodThought{{"SleptInBarracks", 2}})
	if d := MoodProvisionDeficits(moodReview(t, p, MoodHistory{})); d != nil {
		t.Fatalf("positive barracks stage raised a deficit: %v", d)
	}
}

// Barracks sleepers are unhoused, so the bedroom builder owes a step while
// the plan has slots, and owes none once everyone sleeps in a bedroom.
func TestBedroomsOwedWhileSleepingInABarracks(t *testing.T) {
	plan, rooms, sleeping := bedroomFixture()
	owed := func() bool {
		v, known := BedroomsOwed(domain.Known(plan), domain.Known(rooms), domain.Known(sleeping), nil, nil, nil, RoomGate{}).Value()
		return known && v
	}
	if !owed() {
		t.Fatal("barracks sleepers owe no bedroom")
	}
	// Everyone sleeps in standing bedrooms and the plan has no slot left.
	plan.Rooms = plan.Rooms[:1]
	rooms.Rooms[0].Role = domain.Known(RoomRoleBedroom)
	rooms.Rooms[0].Cells = []domain.Cell{{X: 30, Z: 30}}
	if owed() {
		t.Fatal("bedroom sleepers still owe a bedroom")
	}
}
