package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// footprintNative reports a fixed native footprint for every preview.
type footprintNative struct {
	*rockCoolerNative
	footprint []domain.Cell
}

func (n *footprintNative) PreviewBuilding(ctx context.Context, action domain.Action, s domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	preview, raw, err := n.rockCoolerNative.PreviewBuilding(ctx, action, s)
	preview.Preview.Footprint = domain.Known(n.footprint)
	return preview, raw, err
}

func geothermalDigFixture(t *testing.T) (*RoutineBuildingPlanner, *footprintNative, excavationStep) {
	t.Helper()
	p, _, n, s, site, shaft := rockCoolerStep(t)
	fn := &footprintNative{rockCoolerNative: n, footprint: []domain.Cell{site.Cell, shaft}}
	p.native = fn
	p.definition = policy.GeothermalDefinition
	p.power = &policy.PowerProposal{Method: policy.PowerGenerate, Definition: policy.GeothermalDefinition, Center: site.Cell}
	return p, fn, s
}

func rockOn(s *excavationStep, cells ...domain.Cell) {
	for i, c := range s.facts.Cells {
		for _, r := range cells {
			if c.Cell == r {
				s.facts.Cells[i].Occupied, s.facts.Cells[i].Walkable, s.facts.Cells[i].Roof, s.facts.Cells[i].NaturalRock = domain.Known(true), domain.Known(false), domain.Known("RoofRockThick"), domain.Known(true)
			}
		}
	}
}

// A geothermal footprint on listed rock is dug; the generator is placed by
// the ordinary preview once it reads open (#1896).
func TestDigGeothermalMinesListedRockFootprint(t *testing.T) {
	t.Parallel()
	p, n, s := geothermalDigFixture(t)
	ctx := context.Background()
	check := func() error { return nil }
	if _, handled, err := p.digGeothermal(ctx, ctx, s, nil, check); err != nil || handled {
		t.Fatal("open footprint handled", handled, err)
	}
	rockOn(&s, n.footprint...)
	result, handled, err := p.digGeothermal(ctx, ctx, s, nil, check)
	if err != nil || !handled || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, handled, err)
	}
	if n.overRock != 0 {
		t.Fatal("generator previewed in the dig plan", n.overRock)
	}
}

// A footprint cell the frame does not list is fogged rock and is dug too.
func TestDigGeothermalMinesFoggedRockFootprint(t *testing.T) {
	t.Parallel()
	p, n, s := geothermalDigFixture(t)
	ctx := context.Background()
	var kept []policy.SiteCell
	for _, c := range s.facts.Cells {
		if c.Cell != n.footprint[1] {
			kept = append(kept, c)
		}
	}
	s.facts.Cells = kept
	rockOn(&s, n.footprint[0])
	result, handled, err := p.digGeothermal(ctx, ctx, s, nil, func() error { return nil })
	if err != nil || !handled || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, handled, err)
	}
}

// A protected footprint cell holds the build; rock with no open cell beside
// the footprint is refused.
func TestDigGeothermalRefusesWithoutAccessOrOnProtectedCell(t *testing.T) {
	t.Parallel()
	p, n, s := geothermalDigFixture(t)
	ctx := context.Background()
	rockOn(&s, n.footprint...)
	result, handled, err := p.digGeothermal(ctx, ctx, s, []domain.Cell{n.footprint[1]}, func() error { return nil })
	if err != nil || !handled || result.Verdict != BuildingReasonExistingWork {
		t.Fatal(result, handled, err)
	}
	for i := range s.facts.Cells {
		s.facts.Cells[i].Walkable = domain.Known(false)
	}
	result, handled, err = p.digGeothermal(ctx, ctx, s, nil, func() error { return nil })
	if err != nil || !handled || !result.Verdict.Is(RefusalRockNotDug) {
		t.Fatal(result, handled, err)
	}
}
