package policy

import (
	"bytes"
	"encoding/json"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func extentFixture(t *testing.T, points ...domain.Cell) ColonyExtentRequest {
	t.Helper()
	census := CurrentConstruction{Colony: true}
	home := HomeCoverageObservation{}
	for i, c := range points {
		id := string(rune('a' + i))
		b, err := domain.NewBuilding("Wall", c, domain.North, "WoodLog")
		if err != nil {
			t.Fatal(err)
		}
		census.Buildings = append(census.Buildings, CurrentBuilding{ID: id, Building: b, Cells: []domain.Cell{c}})
		home.Targets = append(home.Targets, HomeCoverageTarget{ID: id, Cells: []domain.Cell{c}, Shape: domain.Known("shape"), Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0)), ExtentGeometry: domain.Known(HomeExtentGeometry{})})
	}
	return ColonyExtentRequest{Bounds: domain.Known(Bounds{Width: 30, Height: 30}), Construction: domain.Known(census), Stockpiles: domain.Known([]OwnedStockpile{}), Home: domain.Known(home)}
}

func extentReasons(e ColonyExtent, c domain.Cell) []ExtentProvenance {
	var reasons []ExtentProvenance
	for _, r := range e.Regions {
		for _, row := range r.Cells {
			if row.Cell == c {
				reasons = append(reasons, row.Provenance...)
			}
		}
	}
	return reasons
}

func TestColonyExtentGeometry(t *testing.T) {
	for _, tt := range []struct {
		name     string
		points   []domain.Cell
		geometry HomeExtentGeometry
		margin   int32
		regions  int
		present  domain.Cell
		origin   ExtentOrigin
		absent   domain.Cell
	}{
		{"disjoint facilities", []domain.Cell{{X: 2, Z: 2}, {X: 6, Z: 2}}, HomeExtentGeometry{}, 0, 2, domain.Cell{X: 6, Z: 2}, ExtentFacility, domain.Cell{X: 4, Z: 2}},
		{"lone wall fragment", []domain.Cell{{X: 2, Z: 2}}, HomeExtentGeometry{}, 0, 1, domain.Cell{X: 2, Z: 2}, ExtentFacility, domain.Cell{X: 3, Z: 2}},
		{"distant island", []domain.Cell{{X: 2, Z: 2}, {X: 25, Z: 25}}, HomeExtentGeometry{}, 1, 2, domain.Cell{X: 25, Z: 25}, ExtentFacility, domain.Cell{X: 14, Z: 14}},
		{"walled corridor", []domain.Cell{{X: 2, Z: 2}, {X: 6, Z: 2}}, HomeExtentGeometry{Corridor: []domain.Cell{{X: 3, Z: 2}, {X: 4, Z: 2}, {X: 5, Z: 2}}}, 0, 1, domain.Cell{X: 4, Z: 2}, ExtentCorridor, domain.Cell{X: 4, Z: 3}},
		{"enclosed perimeter", []domain.Cell{{X: 2, Z: 2}}, HomeExtentGeometry{EnclosedInterior: []domain.Cell{{X: 3, Z: 2}, {X: 3, Z: 3}, {X: 2, Z: 3}}}, 0, 1, domain.Cell{X: 3, Z: 3}, ExtentEnclosedInterior, domain.Cell{X: 4, Z: 3}},
		{"map edge margin", []domain.Cell{{X: 0, Z: 0}}, HomeExtentGeometry{}, 2, 1, domain.Cell{X: 2, Z: 2}, ExtentMargin, domain.Cell{X: 3, Z: 0}},
		{"overlapping margins do not connect", []domain.Cell{{X: 2, Z: 2}, {X: 6, Z: 2}}, HomeExtentGeometry{}, 2, 2, domain.Cell{X: 4, Z: 2}, ExtentMargin, domain.Cell{X: 10, Z: 2}},
	} {
		t.Run(tt.name, func(t *testing.T) {
			r := extentFixture(t, tt.points...)
			h, _ := r.Home.Value()
			h.Targets[0].ExtentGeometry = domain.Known(tt.geometry)
			r.Home = domain.Known(h)
			r.Margin = tt.margin
			got, err := DeriveColonyExtent(r)
			e, known := got.Value()
			if err != nil || !known || len(e.Regions) != tt.regions {
				t.Fatalf("extent=%+v known=%v err=%v", e, known, err)
			}
			if !slices.ContainsFunc(extentReasons(e, tt.present), func(p ExtentProvenance) bool { return p.Origin == tt.origin }) {
				t.Fatalf("missing %s at %v", tt.origin, tt.present)
			}
			if len(extentReasons(e, tt.absent)) != 0 {
				t.Fatalf("invented membership at %v", tt.absent)
			}
			for _, region := range e.Regions {
				for _, row := range region.Cells {
					if row.Cell.X < 0 || row.Cell.Z < 0 || row.Cell.X >= 30 || row.Cell.Z >= 30 {
						t.Fatal("margin escaped map", row)
					}
				}
			}
		})
	}
}

func TestColonyExtentUnknown(t *testing.T) {
	for _, kind := range []string{"bounds", "construction", "stockpiles", "home", "exact refresh", "missing footprint", "missing target", "batch only", "blocked"} {
		t.Run(kind, func(t *testing.T) {
			r := extentFixture(t, domain.Cell{X: 2, Z: 2})
			c, _ := r.Construction.Value()
			h, _ := r.Home.Value()
			switch kind {
			case "bounds":
				r.Bounds = domain.Unknown[Bounds]()
			case "construction":
				r.Construction = domain.Unknown[CurrentConstruction]()
			case "stockpiles":
				r.Stockpiles = domain.Unknown[[]OwnedStockpile]()
			case "home":
				r.Home = domain.Unknown[HomeCoverageObservation]()
			case "exact refresh":
				c.Colony = false
				r.Construction = domain.Known(c)
			case "missing footprint":
				c.Buildings[0].Cells = nil
				r.Construction = domain.Known(c)
			case "missing target":
				h.Targets = nil
				r.Home = domain.Known(h)
			case "batch only":
				h.Targets[0].ExtentGeometry = domain.Unknown[HomeExtentGeometry]()
				r.Home = domain.Known(h)
			case "blocked":
				h.Targets[0].Blocker = "unavailable"
				r.Home = domain.Known(h)
			}
			got, err := DeriveColonyExtent(r)
			if _, known := got.Value(); known || err != nil {
				t.Fatal(got, err)
			}
		})
	}
}

func TestColonyExtentValidation(t *testing.T) {
	for _, kind := range []string{"margin", "negative margin", "bounds", "out of bounds", "disconnected geometry", "duplicate geometry", "duplicate target", "duplicate stockpile"} {
		t.Run(kind, func(t *testing.T) {
			r := extentFixture(t, domain.Cell{X: 2, Z: 2})
			h, _ := r.Home.Value()
			switch kind {
			case "margin":
				r.Margin = 9
			case "negative margin":
				r.Margin = -1
			case "bounds":
				r.Bounds = domain.Known(Bounds{})
			case "out of bounds":
				r.Bounds = domain.Known(Bounds{Width: 2, Height: 2})
			case "disconnected geometry":
				h.Targets[0].ExtentGeometry = domain.Known(HomeExtentGeometry{Corridor: []domain.Cell{{X: 20, Z: 20}}})
			case "duplicate geometry":
				h.Targets[0].ExtentGeometry = domain.Known(HomeExtentGeometry{Corridor: []domain.Cell{{X: 3, Z: 2}, {X: 3, Z: 2}}})
			case "duplicate target":
				h.Targets = append(h.Targets, h.Targets[0])
			case "duplicate stockpile":
				r.Stockpiles = domain.Known([]OwnedStockpile{{ID: "a", Cells: []domain.Cell{{X: 3, Z: 2}}}})
			}
			r.Home = domain.Known(h)
			got, err := DeriveColonyExtent(r)
			if _, known := got.Value(); known || err == nil {
				t.Fatal(got, err)
			}
		})
	}
}

func TestColonyExtentStableProvenanceAndIsolation(t *testing.T) {
	r := extentFixture(t, domain.Cell{X: 2, Z: 2}, domain.Cell{X: 6, Z: 2})
	c, _ := r.Construction.Value()
	h, _ := r.Home.Value()
	h.Targets[0].ExtentGeometry = domain.Known(HomeExtentGeometry{Corridor: []domain.Cell{{X: 3, Z: 2}, {X: 4, Z: 2}, {X: 5, Z: 2}}})
	r.Home = domain.Known(h)
	b := c.Buildings[0]
	r.Claims = domain.Known([]ConstructionClaim{{Plan: "plan", Action: "action", Goal: "goal", Identity: domain.ConstructionIdentity{Origin: "blueprint", Current: b.ID}, Building: b.Building, Cells: b.Cells}})
	r.Stockpiles = domain.Known([]OwnedStockpile{{ID: "stockpile", Cells: []domain.Cell{{X: 20, Z: 20}, {X: 20, Z: 21}}}})
	first, err := DeriveColonyExtent(r)
	e, known := first.Value()
	if err != nil || !known {
		t.Fatal(first, err)
	}
	if p := extentReasons(e, b.Building.Cell()); len(p) != 1 || p[0].Action != "action" {
		t.Fatal(p)
	}
	if p := extentReasons(e, domain.Cell{X: 20, Z: 21}); len(p) != 1 || p[0].Facility != "stockpile" || p[0].Action != "" {
		t.Fatal(p)
	}
	want, _ := json.Marshal(e)
	for range 10 {
		slices.Reverse(c.Buildings)
		slices.Reverse(h.Targets)
		for i := range h.Targets {
			g, _ := h.Targets[i].ExtentGeometry.Value()
			slices.Reverse(g.Corridor)
			h.Targets[i].ExtentGeometry = domain.Known(g)
		}
		r.Construction = domain.Known(c)
		r.Home = domain.Known(h)
		got, err := DeriveColonyExtent(r)
		value, k := got.Value()
		data, _ := json.Marshal(value)
		if err != nil || !k || !bytes.Equal(data, want) {
			t.Fatalf("unstable output: %s, %v", data, err)
		}
	}
	e.Regions[0].Cells[0].Cell.X = 29
	e.Regions[0].Cells[0].Provenance[0].Action = "changed"
	again, err := DeriveColonyExtent(r)
	value, _ := again.Value()
	data, _ := json.Marshal(value)
	if err != nil || !bytes.Equal(data, want) {
		t.Fatal("output aliases input")
	}
	empty, err := DeriveColonyExtent(extentFixture(t))
	v, k := empty.Value()
	if err != nil || !k || len(v.Regions) != 0 {
		t.Fatal(empty, err)
	}
}
