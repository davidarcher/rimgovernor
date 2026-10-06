package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// unroofedGround is a 40x40 open map: walkable, unroofed, no walls or roof yet.
func unroofedGround() []SiteCell {
	var cells []SiteCell
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 40; z++ {
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Roofed: domain.Known(false), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), StorageEmpty: domain.Known(true)})
		}
	}
	return cells
}

// storeCreates are the creates the declared stores make on open ground, by
// role prefix.
func storeCreates(view StoreView) map[string]StockpileEdit {
	view.Bounds, view.Cells = Bounds{Width: 40, Height: 40}, unroofedGround()
	review := PlanStockpileMaintenance(StockpileRequest{Tick: 1, Bounds: view.Bounds, Cells: view.Cells, Protected: view.Protected, Stores: DeclareStores(view).Stores})
	out := map[string]StockpileEdit{}
	for _, e := range review.Edits {
		if e.Kind == StockpileCreate {
			out[e.Role] = e
		}
	}
	return out
}

func withinRect(cells []domain.Cell, r Rectangle) bool {
	for _, c := range cells {
		if c.X < r.X || c.X >= r.X+r.Width || c.Z < r.Z || c.Z >= r.Z+r.Height {
			return false
		}
	}
	return len(cells) > 0
}
