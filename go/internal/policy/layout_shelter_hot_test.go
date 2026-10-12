package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestHotMapCurve(t *testing.T) {
	t.Parallel()
	mild := []float64{4, 6, 9, 13, 17, 21, 24, 23, 19, 14, 9, 5}
	scorching := []float64{20, 22, 26, 30, 34, 38, 41, 39, 33, 27, 22, 20}
	edge := append([]float64(nil), mild...)
	edge[6] = DefaultRoundsPolicy().HotEnter
	if HotMapCurve(nil) || HotMapCurve(mild) || HotMapCurve(edge) {
		t.Fatal("a curve that stays at or below HotEnter is hot")
	}
	if !HotMapCurve(scorching) {
		t.Fatal("a curve that peaks above HotEnter is not hot")
	}
	if ShelterCoolers(true) != 1 || ShelterCoolers(false) != 0 {
		t.Fatalf("coolers hot=%d mild=%d, want 1 and 0", ShelterCoolers(true), ShelterCoolers(false))
	}
}

// A hot map's survey latches Hot on the plan through the real derive path, the
// shelter is sized for the passive cooler slot, and the
// template plans the slot on the floor only when hot.
func TestHotMapShelterHoldsAPassiveCoolerSlot(t *testing.T) {
	slowtest.Skip(t, "two full layout derives on a 120-cell map; runs under cmd/test -full and nightly")
	t.Parallel()
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	s := zoningSurvey(120, open)
	for _, hot := range []bool{false, true} {
		s.Hot = hot
		plan, ok := derivePlan(s, 3, nil)
		if !ok || plan.Hot != hot {
			t.Fatalf("survey hot=%v: plan ok=%v hot=%v", hot, ok, plan.Hot)
		}
		shelter := shelterOf(t, plan)
		if got := shelter.Region.Bounds(); !shelterHolds(shelter, 3, 0, ShelterCoolers(hot)) {
			t.Errorf("survey hot=%v: shelter %dx%d holds less than the sizing", hot, got.Width, got.Height)
		}
		room := InteriorRoom{Role: RoomRoleShelter, Shapes: testShapes, Interior: shelter.Region.Bounds(), Doors: []domain.Cell{shelter.Door}, Occupants: 3, Coolers: ShelterCoolers(plan.Hot), Capabilities: CapabilitiesOf(PlannedShelter)}
		planned, ok := PlanRoomInterior(room, InteriorPieceDef{})
		if !ok {
			t.Fatalf("hot=%v: no interior plan", hot)
		}
		if n := len(slotsOf(planned, ShelterCoolerSlotPrefix)); n != ShelterCoolers(hot) {
			t.Errorf("hot=%v: %d cooler slots, want %d", hot, n, ShelterCoolers(hot))
		}
	}
	if ShelterInteriorArea(3, 0, 1) <= ShelterInteriorArea(3, 0, 0) {
		t.Fatal("a hot shelter is no bigger than a mild one")
	}
}
