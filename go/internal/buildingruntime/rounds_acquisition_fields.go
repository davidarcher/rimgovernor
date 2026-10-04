package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// withoutFieldSources drops plant sources standing in a growing zone (#1361):
// one observed in the planning window, or one a growing-zone create of this
// world planned, open or completed (a pending one carries no snapshot yet). Native
// refuses a plant acquisition inside a growing zone, and the census can
// predate a zone the fields plan just created. Hunts move and are kept.
func withoutFieldSources(sources domain.Fact[[]policy.AcquisitionSource], projection observation.ColonyProjection, plans []store.PlanState, current domain.GenerationSnapshot) domain.Fact[[]policy.AcquisitionSource] {
	rows, known := sources.Value()
	if !known {
		return sources
	}
	fields := fieldCells(projection, plans, current)
	if len(fields) == 0 {
		return sources
	}
	kept := make([]policy.AcquisitionSource, 0, len(rows))
	for _, row := range rows {
		if !row.Hunt && fields[row.Cell] {
			continue
		}
		kept = append(kept, row)
	}
	return domain.Known(kept)
}

func fieldCells(projection observation.ColonyProjection, plans []store.PlanState, current domain.GenerationSnapshot) map[domain.Cell]bool {
	cells := map[domain.Cell]bool{}
	farms := map[string]bool{}
	for _, farm := range projection.Farms {
		farms[farm.ID] = true
	}
	for _, cell := range projection.Cells {
		if id, known := cell.ZoneID.Value(); known && farms[id] {
			cells[cell.Cell] = true
		}
	}
	for _, plan := range plans {
		for _, progress := range plan.Progress {
			zone, ok := progress.Action().ZoneCreate()
			v := progress.View()
			if !ok || zone.Kind() != domain.GrowingZone || v.Stage == domain.Cancelled || v.Stage == domain.Unsuccessful || v.Snapshot != (domain.GenerationSnapshot{}) && !v.Snapshot.SameWorld(current) {
				continue
			}
			for _, cell := range zone.Cells() {
				cells[cell] = true
			}
		}
	}
	return cells
}
