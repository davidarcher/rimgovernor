package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestLayoutKillboxAnchorsTheCorridor(t *testing.T) {
	p := perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	bounds := Bounds{Width: 200, Height: 200}
	k, region, home, ok := LayoutKillbox(p, bounds)
	if !ok {
		t.Fatal("no killbox anchor")
	}
	kb := reserved(p, ReserveKillbox)[0]
	d := directionOf(k.Toward)
	if !contains(kb, addCell(k.Entry, scale(d, perimeterThick))) || contains(kb, addCell(k.Entry, scale(d, perimeterThick-1))) {
		t.Fatal("entry is not the opening's outer face", k.Entry, kb)
	}
	walled := map[domain.Cell]bool{}
	for _, c := range k.Walled {
		walled[c] = true
	}
	for t2 := int32(0); t2 < perimeterThick; t2++ {
		if walled[addCell(k.Entry, scale(d, t2))] {
			t.Fatal("opening is walled")
		}
	}
	// An open census over the region: the corridor, kill zone and turret
	// slots stand in the opening's killbox, Home behind its back wall.
	r := defenseFixture()
	r.Region, r.Bounds, r.Home, r.Entrances, r.Killbox = region, bounds, home, nil, k
	r.Cells = nil
	for _, c := range rectCells(region) {
		r.Cells = append(r.Cells, DefenseCell{Cell: c, Walkable: domain.Known(true), Passable: domain.Known(true), BlocksSight: domain.Known(false),
			PlayerOwned: domain.Known(false), NaturalRock: domain.Known(false), EdgeReachable: domain.Known(true), HomeArea: domain.Known(false), Door: domain.Known(false), CoverFill: domain.Known(0.0)})
	}
	r.Turret = DefenseTurretRequest{Definition: "Turret_MiniTurret", Conduit: "HiddenConduit"}
	layout, err := DefenseLayouts(r)
	if err != nil {
		t.Fatal(err)
	}
	if layout.Entry != k.Entry || !contains(kb, layout.KillZone()) || contains(kb, home) {
		t.Fatalf("%+v", layout)
	}
	funnel, _ := layout.Tier(TierFunnel)
	for _, b := range funnel.Buildings {
		if walled[b.Cell()] {
			t.Fatal("funnel on the perimeter wall", b.Cell())
		}
	}
	for _, f := range layout.Firing {
		if !contains(kb, f.Cell) {
			t.Fatal("firing cell outside the killbox", f)
		}
	}
	_, candidates, err := DefenseTurrets(r, layout.Geometry())
	if err != nil || len(candidates) == 0 {
		t.Fatal(candidates, err)
	}
	for i, c := range candidates {
		if !contains(kb, c.Cell) {
			t.Fatal("turret slot outside the killbox", c)
		}
		for _, o := range candidates[i+1:] {
			if chebyshev(c.Cell, o.Cell) < turretSpacing {
				t.Fatal("turret slots too close", c, o)
			}
		}
	}
}

func TestPerimeterSectionsCoverTheWallKillboxFirst(t *testing.T) {
	p := perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	p.Reservations = append(p.Reservations, LayoutReservation{Kind: ReserveGeothermal, Area: Rectangle{X: 90, Z: 90, Width: 10, Height: 10}})
	sections, err := PerimeterSections(p, "Wall", "Door", PerimeterBridge)
	if err != nil {
		t.Fatal(err)
	}
	wall, gate := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, r := range reserved(p, ReservePerimeter) {
		for _, c := range rectCells(r) {
			wall[c] = true
		}
	}
	for _, r := range reserved(p, ReserveGate) {
		cs := rectCells(r)
		gate[cs[0]], gate[cs[len(cs)-1]] = true, true
		for _, c := range cs[1 : len(cs)-1] {
			delete(wall, c) // the airlock cell stays open
		}
	}
	kb := reserved(p, ReserveKillbox)[0]
	centre := domain.Cell{X: kb.X + kb.Width/2, Z: kb.Z + kb.Height/2}
	seen := map[domain.Cell]bool{}
	doors := 0
	for _, s := range sections {
		if s.Name == TierGeothermal {
			if len(s.Buildings) != 100-36 {
				t.Fatal("geothermal shell", len(s.Buildings))
			}
			continue
		}
		if !IsPerimeterTier(s.Name) || len(s.Buildings) == 0 || len(s.Buildings) > 60 {
			t.Fatal(s.Name, len(s.Buildings))
		}
		for _, b := range s.Buildings {
			c := b.Cell()
			if seen[c] || !wall[c] {
				t.Fatal("cell", c)
			}
			seen[c] = true
			if (b.Definition() == "Door") != gate[c] {
				t.Fatal("gate door", c, b.Definition())
			}
			if gate[c] {
				doors++
			}
		}
	}
	first := squaredDistance(sections[0].Buildings[0].Cell(), centre)
	for _, s := range sections[1:] {
		if s.Name != TierGeothermal && squaredDistance(s.Buildings[0].Cell(), centre)+900 < first {
			t.Fatal("a section nearer the killbox than the first", s.Name)
		}
	}
	if len(seen) != len(wall) || doors == 0 || doors%2 != 0 {
		t.Fatal(len(seen), len(wall), doors)
	}
}

// Every perimeter gate is an airlock (#1060): a door on each face of the
// wall, the cell between them unbuilt and walled in on both flanks.
func TestPerimeterAirlock(t *testing.T) {
	p := perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	sections, err := PerimeterSections(p, "Wall", "Door", PerimeterBridge)
	if err != nil {
		t.Fatal(err)
	}
	built := map[domain.Cell]string{}
	for _, s := range sections {
		for _, b := range s.Buildings {
			built[b.Cell()] = b.Definition()
		}
	}
	gates := reserved(p, ReserveGate)
	if len(gates) == 0 {
		t.Fatal("no gates")
	}
	for _, g := range gates {
		cs := rectCells(g)
		if len(cs) != int(perimeterThick) {
			t.Fatal("gate", g)
		}
		mid := cs[1]
		if built[cs[0]] != "Door" || built[cs[2]] != "Door" || built[mid] != "" {
			t.Fatalf("gate %+v: %q %q %q, want Door, open, Door", g, built[cs[0]], built[mid], built[cs[2]])
		}
		// The flanks run along the wall, across the gate's axis.
		flank := domain.Cell{X: 1}
		if g.Width > 1 {
			flank = domain.Cell{Z: 1}
		}
		for _, c := range []domain.Cell{addCell(mid, flank), addCell(mid, scale(flank, -1))} {
			if built[c] != "Wall" {
				t.Fatalf("gate %+v: airlock flank %v is %q, want Wall", g, c, built[c])
			}
		}
	}
}
