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
