package policy

import (
	"math"
	"testing"
)

// bedroomWingReach bounds the bedroom centroid's distance from the storage
// door (#1178). Bedrooms are 5x5 on a 6-cell pitch down both sides of the
// hallway, so eight of them run about 24 cells beside the 9-wide storage
// room; their centroid then sits about 12-17 cells off its door. 20 leaves a
// few cells for a crossing column the wing steps past.
const bedroomWingReach = 20

func TestBedroomsClusterNearStorage(t *testing.T) {
	zones := coreTestZones()
	for _, pawns := range []int{3, 5, 8} {
		// A fresh plan: growing a full plan keeps its rooms where they are, so
		// late bedrooms may spill onto another hallway.
		for name, p := range map[string]LayoutPlan{"fresh": PlanCore(zones, pawns)} {
			var store *LayoutRoom
			var beds []LayoutRoom
			for i, r := range p.Rooms {
				switch r.Role {
				case ModuleStorage:
					store = &p.Rooms[i]
				case ModuleBedroom:
					beds = append(beds, r)
				}
			}
			if store == nil || len(beds) != pawns {
				t.Fatalf("%s %d: storage %v, %d bedrooms", name, pawns, store != nil, len(beds))
			}
			seg := -1
			for i, s := range p.Spine {
				all := true
				for _, b := range beds {
					all = all && onSegment(b, s)
				}
				if all {
					seg = i
					break
				}
			}
			if seg < 0 {
				t.Errorf("%s %d: bedrooms span hallways: %+v", name, pawns, beds)
			}
			var cx, cz float64
			for _, b := range beds {
				cx += float64(b.Door.X)
				cz += float64(b.Door.Z)
			}
			n := float64(len(beds))
			d := math.Hypot(cx/n-float64(store.Door.X), cz/n-float64(store.Door.Z))
			t.Logf("%s %d: centroid %.1f from storage", name, pawns, d)
			if d > bedroomWingReach {
				t.Errorf("%s %d: bedroom centroid %.1f cells from storage, want <= %d", name, pawns, d, bedroomWingReach)
			}
		}
	}
}
