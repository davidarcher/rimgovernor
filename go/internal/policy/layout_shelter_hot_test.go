package policy

import (
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
// shelter is sized for the passive cooler slot, a replan keeps both, and the
// template plans the slot on the floor only when hot (#2044).
func TestHotMapShelterHoldsAPassiveCoolerSlot(t *testing.T) {
	t.Parallel()
	open := func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }
	s := zoningSurvey(120, open)
	plans := map[bool]LayoutPlan{}
	for _, hot := range []bool{false, true} {
		s.Hot = hot
		plan, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 0, 0).Value()
		if !ok || plan.Hot != hot {
			t.Fatalf("survey hot=%v: plan ok=%v hot=%v", hot, ok, plan.Hot)
		}
		shelter := shelterOf(t, plan)
		got, want := shelter.Interior, ShelterSizes(3, 0, ShelterCoolers(hot))[0]
		if got.Width != want[0] || got.Height != want[1] {
			t.Errorf("survey hot=%v: shelter %dx%d, want %v", hot, got.Width, got.Height, want)
		}
		plans[hot] = plan
		room := InteriorRoom{Role: RoomRoleShelter, Shapes: testShapes, Interior: shelter.Interior, Doors: []domain.Cell{shelter.Door}, Occupants: 3, Coolers: ShelterCoolers(plan.Hot)}
		planned, ok := PlanInterior(room, InteriorPieceDef{})
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
	hot := plans[true]
	before := shelterOf(t, hot)
	if grown := growPlan(hot, 12, 1, BuildTierCamp); !grown.Hot || !before.Same(shelterOf(t, grown)) {
		t.Fatal("a replan resized or re-sited the hot shelter, or lost the latch")
	}
}
