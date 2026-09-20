package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func drillFacts() observation.ColonyProjection {
	return observation.ColonyProjection{
		Center:      domain.Cell{X: 10, Z: 10},
		Resources:   domain.Known(map[policy.Resource]int64{"Steel": 0, "Plasteel": 0}),
		Facts:       policy.RoutineFacts{Research: domain.Known(policy.ResearchFacts{Finished: []policy.ResearchProjectID{"DeepDrilling", "GroundPenetratingScanner"}})},
		Definitions: []observation.PlanningDefinition{{Name: "DeepDrill", Available: domain.Known(true), PowerW: domain.Known(200.0)}},
		DeepResources: domain.Known(observation.DeepResources{GroundScanners: []observation.MineralScanner{{Definition: "GroundPenetratingScanner", Built: domain.Known(true)}}, Lumps: []observation.DeepResourceLump{
			{Definition: "Steel", Count: 100, Centre: domain.Cell{X: 15, Z: 10}},
			{Definition: "Steel", Count: 100, Centre: domain.Cell{X: 11, Z: 10}},
			{Definition: "Plasteel", Count: 100, Centre: domain.Cell{X: 10, Z: 10}},
		}}),
	}
}

func TestDeepDrillRanksNeededLumpsAndGatesPrerequisites(t *testing.T) {
	runways := []policy.ResourceRunway{{Resource: "Steel", Deficit: domain.Known(true), Target: 100}}
	f := drillFacts()
	sites := deepDrillSites(f, runways)
	if len(sites) != 2 || sites[0].Centre.X != 11 || sites[1].Centre.X != 15 {
		t.Fatal(sites)
	}
	for _, tc := range []struct {
		name   string
		change func(*observation.ColonyProjection)
	}{
		{"unknown deposits", func(f *observation.ColonyProjection) { f.DeepResources = domain.Unknown[observation.DeepResources]() }},
		{"missing research", func(f *observation.ColonyProjection) {
			f.Facts.Research = domain.Known(policy.ResearchFacts{Finished: []policy.ResearchProjectID{"DeepDrilling"}})
		}},
		{"no scanner", func(f *observation.ColonyProjection) {
			d, _ := f.DeepResources.Value()
			d.GroundScanners = nil
			f.DeepResources = domain.Known(d)
		}},
		{"unbuilt scanner", func(f *observation.ColonyProjection) {
			d, _ := f.DeepResources.Value()
			d.GroundScanners[0].Built = domain.Known(false)
			f.DeepResources = domain.Known(d)
		}},
		{"unavailable drill", func(f *observation.ColonyProjection) { f.Definitions = nil }},
		{"recovered stock", func(f *observation.ColonyProjection) {
			f.Resources = domain.Known(map[policy.Resource]int64{"Steel": 100})
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			f := drillFacts()
			tc.change(&f)
			if sites := deepDrillSites(f, runways); len(sites) != 0 {
				t.Fatal(sites)
			}
		})
	}
	if sites := deepDrillSites(f, nil); len(sites) != 0 {
		t.Fatal(sites)
	}
	runways[0].Resource = "Plasteel"
	if sites := deepDrillSites(f, runways); len(sites) != 1 || sites[0].Definition != "Plasteel" {
		t.Fatal(sites)
	}
}

func TestDeepDrillFootprintRequiresClearUnroofedObservedCells(t *testing.T) {
	a, b := domain.Cell{X: 10, Z: 10}, domain.Cell{X: 11, Z: 10}
	clear := func(cell domain.Cell) policy.SiteCell {
		return policy.SiteCell{Cell: cell, Roofed: domain.Known(false), Occupied: domain.Known(false), Walkable: domain.Known(true)}
	}
	for _, tc := range []struct {
		name   string
		change func([]policy.SiteCell)
		want   bool
	}{
		{"clear", func([]policy.SiteCell) {}, true},
		{"roof", func(c []policy.SiteCell) { c[1].Roofed = domain.Known(true) }, false},
		{"unknown roof", func(c []policy.SiteCell) { c[1].Roofed = domain.Unknown[bool]() }, false},
		{"occupied", func(c []policy.SiteCell) { c[1].Occupied = domain.Known(true) }, false},
		{"blocked", func(c []policy.SiteCell) { c[1].Walkable = domain.Known(false) }, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			cells := []policy.SiteCell{clear(a), clear(b)}
			tc.change(cells)
			if got := deepDrillFootprint(cells, []domain.Cell{a, b}, a); got != tc.want {
				t.Fatal(got)
			}
		})
	}
	if deepDrillFootprint([]policy.SiteCell{clear(a)}, []domain.Cell{a, b}, a) || deepDrillFootprint([]policy.SiteCell{clear(b)}, []domain.Cell{b}, a) {
		t.Fatal("unobserved or off-lump footprint accepted")
	}
}

func TestPendingDeepDrillDrawEntersPowerBudget(t *testing.T) {
	b, _ := domain.NewBuilding("DeepDrill", domain.Cell{X: 10, Z: 10}, domain.North, "")
	a, _ := domain.NewBuildingAction("drill", b)
	plan, _ := domain.NewPlan("drill-plan", 1, []domain.Action{a})
	progress, err := domain.NewProgress(plan, a.ID())
	if err != nil {
		t.Fatal(err)
	}
	names := pendingBuildingDefinitions([]policy.Reservation{{Action: a, Progress: progress}})
	if got := pendingDemand(drillFacts(), names); got != 200 {
		t.Fatal(names, got)
	}
}
