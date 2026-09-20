package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestColonyGridAislesBandsFromOrigin(t *testing.T) {
	grid := ColonyGrid{Origin: domain.Cell{X: 10, Z: 20}, Pitch: GridPitch}
	aisles := map[domain.Cell]bool{}
	for _, c := range grid.Aisles(Bounds{Width: 64, Height: 64}) {
		aisles[c] = true
	}
	// Module walls at offsets 0 and 12, interior 1..11, aisle 13..15.
	for _, off := range []int32{0, 1, 6, 11, 12} {
		if aisles[domain.Cell{X: 10 + off, Z: 20 + off}] {
			t.Fatalf("offset %d inside the module is an aisle", off)
		}
	}
	for _, off := range []int32{13, 14, 15} {
		if !aisles[domain.Cell{X: 10 + off, Z: 20}] || !aisles[domain.Cell{X: 10, Z: 20 + off}] {
			t.Fatalf("offset %d is not an aisle on both axes", off)
		}
	}
	// The band repeats every pitch and runs back past the origin.
	if !aisles[domain.Cell{X: 10 + 16 + 13, Z: 25}] || !aisles[domain.Cell{X: 10 - 3, Z: 25}] || aisles[domain.Cell{X: 10 - 4, Z: 25}] {
		t.Fatalf("aisle band does not repeat around the origin")
	}
	// Clipped to bounds; ordered x-major then z; no duplicates.
	list := grid.Aisles(Bounds{Width: 64, Height: 64})
	for i, c := range list {
		if c.X < 0 || c.Z < 0 || c.X >= 64 || c.Z >= 64 {
			t.Fatalf("aisle %v outside bounds", c)
		}
		if i > 0 && (list[i-1].X > c.X || list[i-1].X == c.X && list[i-1].Z >= c.Z) {
			t.Fatalf("aisles unordered at %d: %v after %v", i, c, list[i-1])
		}
	}
	// 3/16 of columns plus 3/16 of the remaining rows: 64 = 4 periods.
	want := 12*64 + 52*12
	if len(list) != want {
		t.Fatalf("aisle count %d, want %d", len(list), want)
	}
}

func TestColonyGridAislesEdgeCases(t *testing.T) {
	if got := (ColonyGrid{}).Aisles(Bounds{Width: 32, Height: 32}); got != nil {
		t.Fatalf("zero pitch grid has aisles: %d", len(got))
	}
	grid := ColonyGrid{Pitch: GridPitch}
	if got := grid.Aisles(Bounds{}); got != nil {
		t.Fatalf("empty bounds has aisles: %d", len(got))
	}
	// Origin off the map still lays a consistent lattice.
	grid.Origin = domain.Cell{X: -5, Z: 300}
	list := grid.Aisles(Bounds{Width: 16, Height: 16})
	for _, c := range list {
		if !grid.aisleOffset(c.X-grid.Origin.X) && !grid.aisleOffset(c.Z-grid.Origin.Z) {
			t.Fatalf("%v is not on an aisle band", c)
		}
	}
	if len(list) != 3*16+13*3 {
		t.Fatalf("aisle count %d", len(list))
	}
}

func TestColonyGridAislesWithinClipsRegionToBounds(t *testing.T) {
	grid := ColonyGrid{Pitch: GridPitch}
	bounds := Bounds{Width: 40, Height: 40}
	all := map[domain.Cell]bool{}
	for _, c := range grid.Aisles(bounds) {
		all[c] = true
	}
	region := Rectangle{X: -4, Z: 30, Width: 20, Height: 20}
	within := grid.AislesWithin(bounds, region)
	if len(within) == 0 {
		t.Fatal("no aisles in region")
	}
	for _, c := range within {
		if !all[c] {
			t.Fatalf("%v not a map aisle", c)
		}
		if c.X < 0 || c.X >= 16 || c.Z < 30 || c.Z >= 40 {
			t.Fatalf("%v outside region/bounds", c)
		}
	}
	if got := grid.AislesWithin(bounds, Rectangle{}); got != nil {
		t.Fatalf("empty region has aisles: %d", len(got))
	}
}
