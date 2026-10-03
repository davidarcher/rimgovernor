package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A remainder site covers the room cells the sites ahead of it left open.
func TestRemainderSiteTakesWhatTheShelvesLeave(t *testing.T) {
	var cells []SiteCell
	var room []domain.Cell
	for x := int32(0); x < 3; x++ {
		for z := int32(0); z < 3; z++ {
			c := domain.Cell{X: x, Z: z}
			room = append(room, c)
			cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false)})
		}
	}
	shelf := []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}}
	r := StockpileRequest{Bounds: Bounds{Width: 10, Height: 10}, Cells: cells, Sited: []StockpileSite{
		{Role: "rawmeat:1", Room: room, Priority: domain.CriticalPriority, Candidates: [][]domain.Cell{shelf}},
		{Role: "perishables:1", Room: room, Priority: domain.PreferredPriority, Remainder: true, Candidates: [][]domain.Cell{room}},
	}}
	edits := stockpileSiteEdits(r, newStockpileOpen(r))
	if len(edits) != 2 || len(edits[0].Cells) != 2 || len(edits[1].Cells) != 7 || edits[1].Role != "perishables:1" {
		t.Fatalf("%+v", edits)
	}
}
