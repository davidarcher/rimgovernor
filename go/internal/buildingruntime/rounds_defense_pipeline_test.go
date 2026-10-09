package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestDefenseTierOpenMatchesAttemptsAndRepairs(t *testing.T) {
	t.Parallel()
	open := []domain.MethodID{"defense-perimeter-r1-07-0", "defense-perimeter-03-r2-1"}
	for name, want := range map[policy.DefenseTierName]bool{
		"perimeter-r1-07": true,
		"perimeter-03":    true,
		"perimeter-r1-0":  false,
		"perimeter-r1-08": false,
		"perimeter-3":     false,
	} {
		if got := defenseTierOpen(open, name); got != want {
			t.Fatalf("%s: open = %v, want %v", name, got, want)
		}
	}
}

func TestDefensePipelineKeepsTiersOffOpenCells(t *testing.T) {
	t.Parallel()
	p := newDefensePipeline()
	wall := func(x, z int32) domain.Building {
		b, err := domain.NewBuilding("Wall", domain.Cell{X: x, Z: z}, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		return b
	}
	p.add([]domain.Building{wall(5, 5), wall(6, 5)})
	if !p.overlaps([]domain.Building{wall(5, 5)}) {
		t.Fatal("an open tier's own cell was allowed")
	}
	if p.overlaps([]domain.Building{wall(7, 6), wall(7, 5), wall(5, 6)}) {
		t.Fatal("a tier beside an open tier was held")
	}
}
