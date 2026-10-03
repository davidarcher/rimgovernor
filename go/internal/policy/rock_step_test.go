package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rockSite(x, z int32) SiteCell {
	return SiteCell{Cell: domain.Cell{X: x, Z: z},
		Occupied: domain.Known(true), Walkable: domain.Known(false), Roof: domain.Known("RoofRockThick")}
}

func openSite(x, z int32) SiteCell {
	return SiteCell{Cell: domain.Cell{X: x, Z: z},
		Occupied: domain.Known(false), Walkable: domain.Known(true), Roof: domain.Known("")}
}

func TestRockStep(t *testing.T) {
	cell := func(x, z int32) domain.Cell { return domain.Cell{X: x, Z: z} }
	sites := []SiteCell{openSite(0, 0), rockSite(1, 0), rockSite(2, 0)}
	t.Run("open ground yields no dig", func(t *testing.T) {
		got := RockStep([]RoleCell{{cell(0, 0), RockNeedsFloor}, {cell(0, 0), RockBlocks}}, sites)
		if len(got.Dig) != 0 || len(got.Left) != 0 {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("needs_floor on rock digs exactly that cell", func(t *testing.T) {
		got := RockStep([]RoleCell{{cell(0, 0), RockNeedsFloor}, {cell(1, 0), RockNeedsFloor}}, sites)
		if !reflect.DeepEqual(got.Dig, []domain.Cell{cell(1, 0)}) {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("blocks on rock is left and built", func(t *testing.T) {
		got := RockStep([]RoleCell{{cell(2, 0), RockBlocks}}, sites)
		if len(got.Dig) != 0 || !reflect.DeepEqual(got.Left, []domain.Cell{cell(2, 0)}) {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("fogged rock is dug", func(t *testing.T) {
		got := RockStep([]RoleCell{{cell(9, 9), RockNeedsFloor}}, sites)
		if !reflect.DeepEqual(got.Dig, []domain.Cell{cell(9, 9)}) {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("floor role wins over wall role for one cell", func(t *testing.T) {
		got := RockStep([]RoleCell{{cell(1, 0), RockBlocks}, {cell(1, 0), RockNeedsFloor}}, sites)
		if !reflect.DeepEqual(got.Dig, []domain.Cell{cell(1, 0)}) || len(got.Left) != 0 {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("overlapping plans dedupe to one excavation", func(t *testing.T) {
		a := RockStep([]RoleCell{{cell(1, 0), RockNeedsFloor}, {cell(2, 0), RockNeedsFloor}}, sites).Dig
		b := RockStep([]RoleCell{{cell(2, 0), RockNeedsFloor}, {cell(1, 0), RockNeedsFloor}}, sites).Dig
		want := []domain.Cell{cell(1, 0), cell(2, 0)}
		if got := MergeRockDigs(a, b); !reflect.DeepEqual(got, want) {
			t.Fatalf("got %v", got)
		}
	})
}
