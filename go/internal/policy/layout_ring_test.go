package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestRingHallwaysNeedNoMainSpine: a plan whose hallways form a ring (no
// main line at Spine[0]) goes through Core, PlanUtilities and PlanPerimeter
// without a panic, and its utility sites stay off the hallway clearance
// (#1947).
func TestRingHallwaysNeedNoMainSpine(t *testing.T) {
	s := zoningSurvey(200, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	storage := PlannedRoom{Role: PlannedStorage, Interior: Rectangle{X: 90, Z: 90, Width: 6, Height: 6}, Door: domain.Cell{X: 82, Z: 93}, DoorRot: domain.West}
	plan := LayoutPlan{
		Zones: Zone(s),
		Spine: []SpineSegment{
			{From: domain.Cell{X: 80, Z: 80}, To: domain.Cell{X: 80, Z: 110}},
			{From: domain.Cell{X: 80, Z: 110}, To: domain.Cell{X: 110, Z: 110}},
			{From: domain.Cell{X: 110, Z: 110}, To: domain.Cell{X: 110, Z: 80}},
			{From: domain.Cell{X: 110, Z: 80}, To: domain.Cell{X: 80, Z: 80}},
		},
		Rooms: []PlannedRoom{storage},
	}
	if c, ok := plan.Core(); !ok || c != (domain.Cell{X: 93, Z: 93}) {
		t.Fatal("core", c, ok)
	}
	if _, ok := (LayoutPlan{Spine: plan.Spine}).Core(); !ok {
		t.Fatal("footprint core missing")
	}
	p := PlanUtilities(plan, UtilityWants{TurbinePairs: 1, Solar: 1})
	u := newUtilityGrid(plan)
	for _, r := range p.Reservations {
		if (r.Kind == ReserveTurbine || r.Kind == ReserveSolar) && !u.free(r.Area, true) {
			t.Fatal("site on the clearance", r)
		}
	}
	PlanPerimeter(p, s)
}
