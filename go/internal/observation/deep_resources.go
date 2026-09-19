package observation

import (
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
type DeepResources struct {
	Lumps                             []DeepResourceLump
	GroundScanners, LongRangeScanners []MineralScanner
}

// DecodeColony validates the census before projection. Missing sections remain
// unknown; an observed empty census establishes that no lumps/scanners exist.
func colonyDeepResources(section *o.DeepResourcesSection) domain.Fact[DeepResources] {
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
			result = append(result, MineralScanner{ID: row.GetBuildingId(), Definition: row.GetDefName(), Position: domain.Cell{X: row.Position.GetX(), Z: row.Position.GetZ()}, Built: optional(row.Built), Powered: optional(row.Powered), Working: optional(row.Working), TicksToNextFind: optional(row.TicksToNextFind), TargetResource: optional(row.TargetResource)})
		}
		return result
	}
	r.GroundScanners, r.LongRangeScanners = scanners(f.GroundScanners), scanners(f.LongRangeScanners)
	return domain.Known(r)
}
