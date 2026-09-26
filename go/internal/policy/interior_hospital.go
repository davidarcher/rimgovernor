package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The hospital template (#806): beds in one row, heads against the back
// wall, in pairs that share a vitals monitor between their heads. A
// VitalsMonitor must stand adjacent to the bed it serves (mustBePlacedAdjacent
// in Buildings_Misc.xml) and one monitor links every adjacent bed, so each
// pair is bed, monitor, bed with one free cell before the next pair. The
// sterile floor is the flooring policy's clean tier (flooring.go).

func init() {
	RegisterInteriorTemplate(RoomRoleHospital, InteriorTemplate{Name: "hospital", Plan: planHospital})
}

const vitalsMonitorDefinition = "VitalsMonitor"

func planHospital(f InteriorFrame) ([]InteriorPiece, bool) {
	// Bed (two rows) and at least the entrance row in front of it.
	if f.Depth < 3 {
		return nil, false
	}
	pairs := RowCapacity(f.Width, 3, 1)
	beds, ok := RowStarts(f.Width, 1, 1, 2*pairs, RowCentred)
	if !ok {
		return nil, false
	}
	bedDef := HospitalBedDefinitions[0]
	var out []InteriorPiece
	for i, u := range beds {
		bed := NewInteriorPiece(fmt.Sprintf("bed.%d", i+1), bedDef, domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: u, Z: f.Depth - 2})
		bed.Row = "beds"
		out = append(out, bed)
		if i%2 == 0 {
			m := NewInteriorPiece(fmt.Sprintf("monitor.%d", i/2+1), vitalsMonitorDefinition, domain.Cell{X: 1, Z: 1}, domain.North, domain.Cell{X: u + 1, Z: f.Depth - 1})
			m.Row = "monitors"
			out = append(out, m)
		}
	}
	return out, true
}
