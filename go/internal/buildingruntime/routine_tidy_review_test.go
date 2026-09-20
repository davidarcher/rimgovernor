package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func tidyZoneRow(id, kind string, x, z, w, h int32, farm *o.FarmFacts) *o.ZoneState {
	return &o.ZoneState{Id: proto.String(id), Type: proto.String(kind), Bounds: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Maximum: &c.Cell{X: proto.Int32(x + w - 1), Z: proto.Int32(z + h - 1)}}, Farm: farm}
}

// TestTidyZoneItemsMeasureOnlyManagedZonesStillListed: a claimed growing
// zone becomes a field item with the census footprint and farm crop, a
// claimed stockpile a stockpile item, an unclaimed (player) zone nothing,
// and a claim the census no longer lists nothing.
func TestTidyZoneItemsMeasureOnlyManagedZonesStillListed(t *testing.T) {
	projection := &observation.ColonyProjection{Zones: facts.Held[bridge.ZonesRead]{Complete: true, Value: bridge.ZonesRead{Rows: []*o.ZoneState{
		tidyZoneRow("Zone_7", "growing", 20, 5, 2, 2, &o.FarmFacts{Crop: proto.String("Plant_Rice"), UsableCells: proto.Uint32(4)}),
		tidyZoneRow("Zone_8", "stockpile", 30, 30, 3, 3, nil),
		tidyZoneRow("Zone_9", "growing", 40, 40, 5, 5, nil),
	}}}}
	owned := []store.OwnedZone{
		{ID: "Zone_7", Kind: domain.GrowingZone, Crop: "Plant_Potato", Cells: []domain.Cell{{X: 20, Z: 5}, {X: 21, Z: 5}, {X: 20, Z: 6}, {X: 21, Z: 6}}},
		{ID: "Zone_8", Kind: domain.StockpileZone, Cells: make([]domain.Cell, 9)},
		{ID: "Zone_3", Kind: domain.GrowingZone, Cells: make([]domain.Cell, 4)},
	}
	items := tidyZoneItems(owned, projection)
	if len(items) != 2 {
		t.Fatalf("items %+v", items)
	}
	if items[0] != (policy.TidyItem{Kind: policy.TidyField, ID: "Zone_7", Footprint: policy.Rectangle{X: 20, Z: 5, Width: 2, Height: 2}, Cells: 4, Crop: "Plant_Rice", Managed: true}) {
		t.Fatalf("field %+v", items[0])
	}
	if items[1] != (policy.TidyItem{Kind: policy.TidyStockpile, ID: "Zone_8", Footprint: policy.Rectangle{X: 30, Z: 30, Width: 3, Height: 3}, Cells: 9, Managed: true}) {
		t.Fatalf("stockpile %+v", items[1])
	}
	projection.Zones.Complete = false
	if items := tidyZoneItems(owned, projection); items != nil {
		t.Fatalf("incomplete census measured %+v", items)
	}
}

func tidyRoom(id string, role policy.RoomRole, x, z, w, h int32, beds []string) policy.Room {
	room := policy.Room{ID: id, Role: domain.Known(role), Enclosed: domain.Known(true), Beds: beds, Contents: domain.Known([]policy.Amount{})}
	for dx := int32(0); dx < w; dx++ {
		for dz := int32(0); dz < h; dz++ {
			room.Cells = append(room.Cells, domain.Cell{X: x + dx, Z: z + dz})
		}
	}
	return room
}

func tidyRing(x, z, w, h int32) []domain.Cell {
	var out []domain.Cell
	for dx := int32(-1); dx <= w; dx++ {
		for dz := int32(-1); dz <= h; dz++ {
			if dx == -1 || dz == -1 || dx == w || dz == h {
				out = append(out, domain.Cell{X: x + dx, Z: z + dz})
			}
		}
	}
	return out
}

// TestTidyShellItemsMarkClaimedRingsReplacedByAnOnGridPeer: a room ringed
// by claimed walls is a managed shell; it is Replaced once an empty
// enclosed room of the same role sits on the grid; a room in use or with
// an unclaimed ring cell is never Replaced/managed.
func TestTidyShellItemsMarkClaimedRingsReplacedByAnOnGridPeer(t *testing.T) {
	grid := policy.ColonyGrid{Origin: domain.Cell{X: 16, Z: 16}, Pitch: policy.GridPitch, Axes: policy.ColonyGridAxes}
	// Old shell interior 3x3 at (5,5) -> exterior (4,4) 5x5 off grid; the
	// replacement's exterior is the module at the origin.
	old := tidyRoom("Room_1", policy.RoomRoleBedroom, 5, 5, 3, 3, nil)
	replacement := tidyRoom("Room_2", policy.RoomRoleBedroom, 17, 17, 11, 11, nil)
	claims := []policy.ConstructionClaim{{Cells: tidyRing(5, 5, 3, 3)}}
	projection := &observation.ColonyProjection{ColonyGrid: domain.Known(grid), Rooms: domain.Known(policy.RoomObservation{Rooms: []policy.Room{old, replacement}}), Facts: policy.RoutineFacts{ConstructionClaims: domain.Known(claims)}}
	items := tidyShellItems(projection)
	if len(items) != 1 || items[0].ID != "Room_1" || !items[0].Managed || !items[0].Replaced || items[0].InUse || items[0].Footprint != (policy.Rectangle{X: 4, Z: 4, Width: 5, Height: 5}) {
		t.Fatalf("items %+v", items)
	}
	replacement.Beds = []string{"Bed_1"}
	projection.Rooms = domain.Known(policy.RoomObservation{Rooms: []policy.Room{old, replacement}})
	if items := tidyShellItems(projection); len(items) != 1 || items[0].Replaced {
		t.Fatalf("replacement in use still counts %+v", items)
	}
	// Drop a side wall cell (a corner is no interior neighbour).
	ring := tidyRing(5, 5, 3, 3)
	projection.Facts.ConstructionClaims = domain.Known([]policy.ConstructionClaim{{Cells: append(ring[:1:1], ring[2:]...)}})
	if items := tidyShellItems(projection); len(items) != 0 {
		t.Fatalf("player ring managed %+v", items)
	}
}
