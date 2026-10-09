package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The Industry department's stores: one ingredient stockpile per bench
// that works a standing bill (stone chunks at the stonecutter, ingredients at
// every other bench; the kitchen's and butcher's benches are the food stores'),
// a free roofed 2x2 patch in the bench's room nearest the bench, holding that
// bench's recipe inputs at a priority above the general store so hauling brings
// them to the bench. It is keyed by bench id, sized once, and retired only when
// its bench is gone.

type industryOwner struct{}

func (industryOwner) Department() Department { return DepartmentIndustry }

// benchStoreSize is the side of a bench store's square patch.
const benchStoreSize int32 = 2

func (industryOwner) Stores(view StoreView) []Store {
	var out []Store
	if view.Rooms != nil {
		for _, input := range view.BenchInputs {
			room, ok := roomHolding(view.Rooms.Rooms, input.Cell)
			if !ok {
				continue
			}
			filter, err := domain.AllowOnlyFilter(input.Inputs)
			if err != nil {
				continue
			}
			out = append(out, Store{StoreSite: StoreSite{
				Role: domain.IngredientsPrefix + input.Bench, exact: true, regions: cellRects(cellSet(room.Cells)),
				Width: benchStoreSize, Height: benchStoreSize, Roofed: true, Anchor: input.Cell,
				Filter: filter, Priority: domain.ImportantPriority,
			}})
		}
	}
	return append(out, retiredBenchStores(view)...)
}

// retiredBenchStores are the stores whose standing zone names a bench the
// census no longer lists. An unread census retires nothing.
func retiredBenchStores(view StoreView) []Store {
	standing, known := view.Benches.Value()
	if !known {
		return nil
	}
	seen := map[string]bool{}
	var out []Store
	for _, z := range view.Zones {
		bench, ok := strings.CutPrefix(z.Role, domain.IngredientsPrefix)
		if !ok || bench == "" || standing[bench] || seen[z.Role] {
			continue
		}
		seen[z.Role] = true
		out = append(out, Store{StoreSite: StoreSite{Role: z.Role, exact: true}, Retired: true})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Role < out[j].Role })
	return out
}

func (o industryOwner) RoomDemand(v StoreView) RoomDemand {
	return DeclaredDemand(v, o.Stores(v))
}

func roomHolding(rooms []Room, cell domain.Cell) (Room, bool) {
	for _, room := range rooms {
		for _, c := range room.Cells {
			if c == cell {
				return room, true
			}
		}
	}
	return Room{}, false
}
