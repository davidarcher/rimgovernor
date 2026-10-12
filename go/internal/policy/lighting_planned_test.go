package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSelectLightingBuildsThePlannedLampsOfTheBenchRoom(t *testing.T) {
	p := DefaultLightingPolicy()
	census := lightingCensus()
	census.Lamps = []Lamp{{ID: "built", Definition: "StandingLamp", Cell: domain.Cell{X: 8, Z: 8}, Radius: 1, Lit: true}}
	known := domain.Known(census)
	review, _ := ReviewLighting(known, nil, p, domain.Known(false))
	site := lightingSite()
	site.PoweredSource = domain.Known(true)
	wall := PlannedLamp{Def: "WallLamp", Cell: domain.Cell{X: 7, Z: 10}, Facing: domain.West}
	standing := PlannedLamp{Def: "StandingLamp", Cell: domain.Cell{X: 8, Z: 8}}
	site.Plan = LayoutPlan{
		Rooms: []PlannedRoom{{ID: 3, Role: PlannedKitchen, Region: RectRegion(Rectangle{X: 7, Z: 7, Width: 9, Height: 7})}, {ID: 4, Role: PlannedStorage, Region: RectRegion(Rectangle{X: 40, Z: 7, Width: 3, Height: 3})}},
		Lamps: map[int32][]PlannedLamp{3: {standing, wall}, 4: {{Def: "StandingLamp", Cell: domain.Cell{X: 41, Z: 8}}}},
	}
	proposal, err := SelectLightingMethod(review, known, site, p)
	if err != nil || proposal.Method != LightingBuild || !slices.Equal(proposal.Planned, []PlannedLamp{wall}) || proposal.Definition != "WallLamp" {
		t.Fatal(proposal, err)
	}
	// The improvised cells stay as the fallback behind the planned lamp.
	if len(proposal.Cells) == 0 {
		t.Fatal("no fallback cells")
	}
	// Without power the plan's electric lamps wait; the torch is improvised.
	site.PoweredSource = domain.Known(false)
	proposal, err = SelectLightingMethod(review, known, site, p)
	if err != nil || len(proposal.Planned) != 0 || proposal.Definition != "TorchLamp" {
		t.Fatal(proposal, err)
	}
	// A room the plan lit no lamp in gets the improvised lamp.
	site.PoweredSource = domain.Known(true)
	site.Plan.Lamps = nil
	proposal, err = SelectLightingMethod(review, known, site, p)
	if err != nil || len(proposal.Planned) != 0 || proposal.Definition != "StandingLamp" {
		t.Fatal(proposal, err)
	}
}

func TestPlanLitRoomsBuildTheBenchlessRoomsPlannedLamp(t *testing.T) {
	p := DefaultLightingPolicy()
	region := RectRegion(Rectangle{X: 7, Z: 7, Width: 4, Height: 4})
	lamp := PlannedLamp{Def: "StandingLamp", Cell: domain.Cell{X: 8, Z: 8}}
	plan := LayoutPlan{
		Rooms: []PlannedRoom{{ID: 3, Role: PlannedStorage, Region: region}, {ID: 4, Role: PlannedStorage, Region: RectRegion(Rectangle{X: 40, Z: 7, Width: 4, Height: 4})}},
		Lamps: map[int32][]PlannedLamp{3: {lamp}},
	}
	bedroom := Room{ID: "bed", Enclosed: domain.Known(true), Cells: region.Cells()}
	rooms := domain.Known(RoomObservation{Rooms: []Room{bedroom}})
	census := domain.Known(LightingObservation{}) // no bench, nothing dark
	powered := domain.Known(true)

	got, _ := PlanLitRooms(domain.Known(plan), rooms, census, powered).Value()
	if !slices.Equal(got, []int32{3}) {
		t.Fatalf("built powered room with a planned lamp: %v", got)
	}
	review, err := ReviewLighting(census, nil, p, domain.Known(false))
	if err != nil || review.Active {
		t.Fatal(review, err)
	}
	review = review.WithPlanned(domain.Known(got))
	site := lightingSite()
	site.PoweredSource = powered
	site.Plan = plan
	proposal, err := SelectLightingMethod(review, census, site, p)
	if err != nil || !review.Active || proposal.Method != LightingBuild || !slices.Equal(proposal.Planned, []PlannedLamp{lamp}) || proposal.Definition != "StandingLamp" {
		t.Fatal(review, proposal, err)
	}

	// The lamp standing releases the deficit.
	standing := domain.Known(LightingObservation{Lamps: []Lamp{{ID: "l", Definition: "StandingLamp", Cell: lamp.Cell, Lit: true}}})
	if got, _ := PlanLitRooms(domain.Known(plan), rooms, standing, powered).Value(); len(got) != 0 {
		t.Fatal("standing lamp still a deficit", got)
	}
	// A room the plan found darkness cheaper for (4, no lamps) yields none, and
	// so does a planned room that is not built yet.
	if got, _ := PlanLitRooms(domain.Known(plan), domain.Known(RoomObservation{}), census, powered).Value(); len(got) != 0 {
		t.Fatal("unbuilt room is a deficit", got)
	}
	// No power, no deficit; unknown power is unknown.
	if got, known := PlanLitRooms(domain.Known(plan), rooms, census, domain.Known(false)).Value(); !known || len(got) != 0 {
		t.Fatal("unpowered room is a deficit", got, known)
	}
	if _, known := PlanLitRooms(domain.Known(plan), rooms, census, domain.Unknown[bool]()).Value(); known {
		t.Fatal("unknown power read as known")
	}
	if r := (LightingReview{}).WithPlanned(domain.Unknown[[]int32]()); r.Active {
		t.Fatal("unknown plan deficit opened the review")
	}
}
