package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCommonRoomTargetsUseTheColonyBaseline(t *testing.T) {
	q := domain.Known(RoomQuality{Impressiveness: 10})
	obs := SleepingObservation{Rooms: domain.Known([]UpkeepRoom{
		{ID: "dining", Role: "DiningRoom", Quality: q},
		{ID: "rec", Role: "RecRoom", Quality: q},
		{ID: "bedroom", Role: "Bedroom", Quality: q, Beds: []string{"b1"}},
		{ID: "dining-with-bed", Role: "DiningRoom", Quality: q, Beds: []string{"b2"}},
		{ID: "workshop", Role: "Workshop", Quality: q},
	})}
	got := CommonRoomTargets(obs, TechTierIndustrial, testImpressiveness, 0)
	if len(got) != 2 {
		t.Fatalf("targets = %+v", got)
	}
	for _, id := range []string{"dining", "rec"} {
		tg := got[id]
		if tg.Room != id || tg.Min != ImpressivenessDecent || tg.Max != 0 || tg.NeverUpgrade || !slices.Equal(tg.Reasons, []string{"common"}) {
			t.Errorf("%s = %+v", id, tg)
		}
	}
	if got := CommonRoomTargets(obs, TechTierCamp, testImpressiveness, 0); got["dining"].Min != 0 || got["dining"].Reasons != nil {
		t.Errorf("camp = %+v", got["dining"])
	}
	if got := CommonRoomTargets(SleepingObservation{}, TechTierIndustrial, testImpressiveness, 0); got != nil {
		t.Errorf("unknown census = %+v", got)
	}
}

func TestCommonRoomBelowTargetGetsTheTemplateLamp(t *testing.T) {
	room := InteriorRoom{Role: RoomRoleDiningRoom, Interior: Rectangle{0, 0, 7, 11}, Doors: []domain.Cell{{X: 3, Z: -1}}, Dining: testDining, Shapes: testShapes}
	plan, ok := PlanInterior(room, InteriorPieceDef{})
	if !ok {
		t.Fatal("no dining plan")
	}
	tidy := FurnitureRoom{ID: "Room_2", Room: room}
	for _, p := range plan.Pieces {
		if p.Slot != "lamp" {
			tidy.Pieces = append(tidy.Pieces, FurniturePiece{Def: p.Def, Size: p.Size, Rot: p.Rot, Rect: p.Rect})
		}
	}
	low := domain.Known(RoomQuality{Wealth: 300, Beauty: 1, Space: 60, Impressiveness: 25})
	obs := SleepingObservation{Rooms: domain.Known([]UpkeepRoom{{ID: "Room_2", Role: "DiningRoom", Quality: low}})}
	targets := CommonRoomTargets(obs, TechTierIndustrial, testImpressiveness, 0)
	all := func(string) bool { return true }
	u, ok := NextRoomUpgrade(obs, targets, []FurnitureRoom{tidy}, all, RoomGate{})
	if !ok || u.Room != "Room_2" || u.Slot != "lamp" || u.Def != "StandingLamp" {
		t.Fatalf("upgrade = %+v %v", u, ok)
	}
	// At the colony target: nothing to do.
	obs.Rooms = domain.Known([]UpkeepRoom{{ID: "Room_2", Role: "DiningRoom", Quality: domain.Known(RoomQuality{Wealth: 300, Beauty: 1, Space: 60, Impressiveness: 41})}})
	if u, ok := NextRoomUpgrade(obs, targets, []FurnitureRoom{tidy}, all, RoomGate{}); ok {
		t.Fatalf("met target upgrade = %+v", u)
	}
}
