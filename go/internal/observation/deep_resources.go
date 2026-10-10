package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

type DeepResourceLump struct {
	Definition string
	Count      int64
	Centre     domain.Cell
	CellCount  uint32
}
type MineralScanner struct {
	ID, Definition          string
	Position                domain.Cell
	Built, Powered, Working domain.Fact[bool]
	TicksToNextFind         domain.Fact[int64]
	TargetResource          domain.Fact[string]
}

// DeepDrill is one spawned colonist drill. Depleted is the native verdict that
// no valuable deposit remains under it.
type DeepDrill struct {
	ID, Definition                string
	Position                      domain.Cell
	Powered, Depleted, Designated domain.Fact[bool]
	Resource                      domain.Fact[string]
	Remaining                     domain.Fact[int64]
}
type DeepResources struct {
	Lumps                             []DeepResourceLump
	GroundScanners, LongRangeScanners []MineralScanner
	Drills                            []DeepDrill
}

// DecodeColony validates the census before projection. Missing sections remain
// unknown; an observed empty census establishes that no lumps/scanners exist.
func colonyDeepResources(section *o.DeepResourcesSection, catalog *bridge.DefinitionCatalog) domain.Fact[DeepResources] {
	f := section.GetObserved()
	if f == nil {
		return domain.Fact[DeepResources]{}
	}
	r := DeepResources{}
	for _, row := range f.Lumps {
		r.Lumps = append(r.Lumps, DeepResourceLump{Definition: row.GetDefName(), Count: row.GetCount(), Centre: domain.Cell{X: row.Centre.GetX(), Z: row.Centre.GetZ()}, CellCount: row.GetCellCount()})
	}
	scanners := func(rows []*o.MineralScannerState) []MineralScanner {
		var result []MineralScanner
		for _, row := range rows {
			result = append(result, MineralScanner{ID: row.GetBuildingId(), Definition: row.GetDefName(), Position: domain.Cell{X: row.Position.GetX(), Z: row.Position.GetZ()}, Built: optional(row.Built), Powered: optional(row.Powered), Working: optional(row.Working), TicksToNextFind: optional(row.TicksToNextFind), TargetResource: targetResource(row, catalog)})
		}
		return result
	}
	r.GroundScanners, r.LongRangeScanners = scanners(f.GroundScanners), scanners(f.LongRangeScanners)
	for _, row := range f.Drills {
		r.Drills = append(r.Drills, DeepDrill{ID: row.GetBuildingId(), Definition: row.GetDefName(), Position: domain.Cell{X: row.Position.GetX(), Z: row.Position.GetZ()}, Powered: optional(row.Powered), Depleted: optional(row.Depleted), Designated: optional(row.Designated), Resource: optional(row.Resource), Remaining: optional(row.Remaining)})
	}
	return domain.Known(r)
}

// targetResource is what a long-range scanner's aimed mineable yields, read
// from the mineable's def row; unknown when the scanner reports no target or the
// catalog has no yield for it.
func targetResource(row *o.MineralScannerState, catalog *bridge.DefinitionCatalog) domain.Fact[string] {
	if row.TargetMineable == nil {
		return domain.Fact[string]{}
	}
	if yield := catalog.MineableYield(row.GetTargetMineable()); yield != "" {
		return domain.Known(yield)
	}
	return domain.Fact[string]{}
}
