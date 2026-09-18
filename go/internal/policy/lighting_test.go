package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func lightingCensus() LightingObservation {
	return LightingObservation{WorkCells: []WorkLightCell{
		{Bench: "stove", Definition: "FueledStove", Cell: domain.Cell{X: 10, Z: 10}, Glow: 0.1, Roofed: true, Room: domain.Known("7")},
		{Bench: "bench", Definition: "TableStonecutter", Cell: domain.Cell{X: 30, Z: 30}, Glow: 0.0, Roofed: false},
		{Bench: "research", Definition: "SimpleResearchBench", Cell: domain.Cell{X: 12, Z: 10}, Glow: 0.9, Roofed: true, Room: domain.Known("7")},
	}}
}

func lightingSite() LightingFacts {
	room := Room{ID: "7"}
	var cells []SiteCell
	for z := int32(7); z <= 13; z++ {
		for x := int32(7); x <= 15; x++ {
			c := domain.Cell{X: x, Z: z}
			room.Cells = append(room.Cells, c)
			cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false)})
		}
	}
	return LightingFacts{Rooms: []Room{room}, Cells: cells, Available: map[string]domain.Fact[bool]{"StandingLamp": domain.Known(true), "TorchLamp": domain.Known(true)}, PoweredSource: domain.Known(false)}
}

func TestReviewLightingMeasuresRoofedWorkCells(t *testing.T) {
	p := DefaultLightingPolicy()
	r, err := ReviewLighting(domain.Known(lightingCensus()), nil, p)
	if err != nil || !r.Known || !r.Active || len(r.Dark) != 1 || r.Dark[0] != "stove" {
		t.Fatal(r, err)
	}
	// An unknown census keeps the previous latch instead of asserting light.
	r, err = ReviewLighting(domain.Unknown[LightingObservation](), r.Dark, p)
	if err != nil || r.Known || !r.Active || len(r.Dark) != 1 {
		t.Fatal(r, err)
	}
	lit := lightingCensus()
	lit.WorkCells[0].Glow = 0.6
	r, err = ReviewLighting(domain.Known(lit), r.Dark, p)
	if err != nil || !r.Known || r.Active || len(r.Dark) != 0 {
		t.Fatal(r, err)
	}
	// A protected fungus room is never dark: lighting it kills the crop.
	fungus := lightingCensus()
	fungus.WorkCells[0].LightSensitive = true
	r, err = ReviewLighting(domain.Known(fungus), []string{"stove"}, p)
	if err != nil || !r.Known || r.Active || len(r.Dark) != 0 {
		t.Fatal(r, err)
	}
}

func TestSelectLightingBuildsAffordableLampBesideDarkCell(t *testing.T) {
	p := DefaultLightingPolicy()
	census := domain.Known(lightingCensus())
	review, _ := ReviewLighting(census, nil, p)
	site := lightingSite()
	proposal, err := SelectLightingMethod(review, census, site, p)
	if err != nil || proposal.Method != LightingBuild || proposal.Definition != "TorchLamp" || proposal.Bench != "stove" || proposal.Key == "" {
		t.Fatal(proposal, err)
	}
	if len(proposal.Cells) == 0 || proposal.Cells[0] != (domain.Cell{X: 9, Z: 9}) || chebyshev(proposal.Cells[len(proposal.Cells)-1], proposal.Target) > 2 {
		t.Fatal(proposal.Cells)
	}
	for _, c := range proposal.Cells {
		if c == proposal.Target || c == (domain.Cell{X: 12, Z: 10}) {
			t.Fatal("lamp offered on a work cell", c)
		}
	}
	// A powered source promotes the standing lamp; unknown source defers it
	// to the torch rather than guessing.
	site.PoweredSource = domain.Known(true)
	proposal, err = SelectLightingMethod(review, census, site, p)
	if err != nil || proposal.Method != LightingBuild || proposal.Definition != "StandingLamp" {
		t.Fatal(proposal, err)
	}
	site.PoweredSource = domain.Unknown[bool]()
	proposal, err = SelectLightingMethod(review, census, site, p)
	if err != nil || proposal.Method != LightingBuild || proposal.Definition != "TorchLamp" {
		t.Fatal(proposal, err)
	}
	// Nothing available: research needed. Unknown availability: unknown.
	site.Available = map[string]domain.Fact[bool]{"StandingLamp": domain.Known(false), "TorchLamp": domain.Known(false)}
	proposal, err = SelectLightingMethod(review, census, site, p)
	if err != nil || proposal.Method != LightingResearchNeeded {
		t.Fatal(proposal, err)
	}
	site.Available = map[string]domain.Fact[bool]{}
	proposal, err = SelectLightingMethod(review, census, site, p)
	if err != nil || proposal.Method != LightingUnknown {
		t.Fatal(proposal, err)
	}
	// No free cell in the room within radius: no space.
	site = lightingSite()
	for i := range site.Cells {
		site.Cells[i].Occupied = domain.Known(true)
	}
	proposal, err = SelectLightingMethod(review, census, site, p)
	if err != nil || proposal.Method != LightingNoSpace {
		t.Fatal(proposal, err)
	}
}

func TestSelectLightingDefersToUnservicedLampInRange(t *testing.T) {
	p := DefaultLightingPolicy()
	site := lightingSite()
	for _, tc := range []struct {
		lamp Lamp
		want LightingMethod
	}{
		{Lamp{ID: "lamp", Definition: "StandingLamp", Cell: domain.Cell{X: 11, Z: 11}, Radius: 12, Lit: false, Connected: domain.Known(false), Powered: domain.Known(false), SwitchedOn: domain.Known(true)}, LightingPowerNeeded},
		{Lamp{ID: "lamp", Definition: "TorchLamp", Cell: domain.Cell{X: 11, Z: 11}, Radius: 10, Lit: false, OutOfFuel: domain.Known(true)}, LightingFuelNeeded},
		{Lamp{ID: "lamp", Definition: "StandingLamp", Cell: domain.Cell{X: 11, Z: 11}, Radius: 12, Lit: false, BrokenDown: domain.Known(true)}, LightingRepairNeeded},
		{Lamp{ID: "lamp", Definition: "StandingLamp", Cell: domain.Cell{X: 11, Z: 11}, Radius: 12, Lit: false, SwitchedOn: domain.Known(false), Powered: domain.Known(true)}, LightingSwitchedOff},
		{Lamp{ID: "lamp", Definition: "StandingLamp", Cell: domain.Cell{X: 11, Z: 11}, Radius: 12, Lit: true}, LightingBlocked},
		// Out of range: not serving, so a new lamp is placed.
		{Lamp{ID: "lamp", Definition: "TorchLamp", Cell: domain.Cell{X: 40, Z: 40}, Radius: 10, Lit: true}, LightingBuild},
		// Partially lit: a lit lamp beyond the placement radius whose
		// radius only grazes the cell leaves it dark and gets no deference.
		{Lamp{ID: "lamp", Definition: "TorchLamp", Cell: domain.Cell{X: 18, Z: 10}, Radius: 10, Lit: true}, LightingBuild},
	} {
		v := lightingCensus()
		v.Lamps = []Lamp{tc.lamp}
		census := domain.Known(v)
		review, _ := ReviewLighting(census, nil, p)
		proposal, err := SelectLightingMethod(review, census, site, p)
		if err != nil || proposal.Method != tc.want {
			t.Fatal(tc.lamp, proposal, err)
		}
		if tc.want == LightingBuild {
			for _, c := range proposal.Cells {
				if c == tc.lamp.Cell {
					t.Fatal("lamp cell offered")
				}
			}
		}
	}
	// Inactive review and unknown census short-circuit.
	proposal, err := SelectLightingMethod(LightingReview{}, domain.Known(lightingCensus()), site, p)
	if err != nil || proposal.Method != LightingNoMethod {
		t.Fatal(proposal, err)
	}
	proposal, err = SelectLightingMethod(LightingReview{Active: true, Dark: []string{"stove"}}, domain.Unknown[LightingObservation](), site, p)
	if err != nil || proposal.Method != LightingUnknown {
		t.Fatal(proposal, err)
	}
}

func TestDetectRoutineRanksLightingFromMeasuredCensus(t *testing.T) {
	f := stableRoutine()
	f.Upkeep.Lighting = domain.Known(lightingCensus())
	r := needs(t, f, RoutineLatches{})
	if !hasNeed(r, MaintainLighting) || len(r.Latches.Lighting) != 1 || r.Latches.Lighting[0] != "stove" || assessment(t, r, MaintainLighting) != domain.NeedDeficit {
		t.Fatal(r.Latches, r.Assessments)
	}
	for _, g := range r.Goals {
		if g.ID == MaintainLighting {
			if g.Priority != lightingPriority || g.MethodUnavailable || len(g.Labor) != 1 || g.Labor[0] != WorkConstruction {
				t.Fatal(g)
			}
			if d, known := g.Deficit.Value(); !known || d != 1 {
				t.Fatal(g)
			}
		}
	}
	f.Upkeep.Lighting = domain.Unknown[LightingObservation]()
	r = needs(t, f, r.Latches)
	if !hasNeed(r, MaintainLighting) || len(r.Latches.Lighting) != 1 || assessment(t, r, MaintainLighting) != domain.NeedUnknown {
		t.Fatal("unknown census dropped the latch", r.Latches, r.Assessments)
	}
	f.Upkeep.Lighting = domain.Known(LightingObservation{})
	r = needs(t, f, r.Latches)
	if hasNeed(r, MaintainLighting) || len(r.Latches.Lighting) != 0 || assessment(t, r, MaintainLighting) != domain.NeedRecovered {
		t.Fatal(r.Latches, r.Assessments)
	}
}
