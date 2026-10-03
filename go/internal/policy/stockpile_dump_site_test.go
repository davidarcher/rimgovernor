package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func dumpCensus(w, h int32, roofed func(domain.Cell) bool) []SiteCell {
	var cells []SiteCell
	for x := int32(0); x < w; x++ {
		for z := int32(0); z < h; z++ {
			c := domain.Cell{X: x, Z: z}
			r := roofed(c)
			cells = append(cells, SiteCell{Cell: c, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false),
				Roofed: domain.Known(r), Indoors: domain.Known(r), StorageEmpty: domain.Known(true)})
		}
	}
	return cells
}

// An outdoor dump stands on open ground, clear of the rooms colonists sleep
// and eat in, nearest the anchor; a storeroom does not push it away.
func TestOutdoorDumpSitesAvoidLivingRooms(t *testing.T) {
	room := func(role RoomRole, x0, z0 int32) Room {
		var cells []domain.Cell
		for x := x0; x < x0+3; x++ {
			for z := z0; z < z0+3; z++ {
				cells = append(cells, domain.Cell{X: x, Z: z})
			}
		}
		return Room{ID: string(role), Role: domain.Known(role), Cells: cells}
	}
	bedroom, store := room(RoomRoleBedroom, 10, 10), room(RoomRoleStoreroom, 20, 10)
	inRoom := func(c domain.Cell) bool {
		for _, r := range []Room{bedroom, store} {
			for _, rc := range r.Cells {
				if rc == c {
					return true
				}
			}
		}
		return false
	}
	req := OutdoorDumpRequest{Bounds: Bounds{Width: 40, Height: 30}, Anchor: domain.Cell{X: 11, Z: 11}, Cells: dumpCensus(40, 30, inRoom), Rooms: []Room{bedroom, store}, Width: 2, Height: 2}
	sites, err := OutdoorDumpSites(req)
	if err != nil || len(sites) == 0 {
		t.Fatal(sites, err)
	}
	for _, s := range sites {
		for _, c := range rectCells(s) {
			if inRoom(c) {
				t.Fatal("dump inside a room", s)
			}
			if c.X > 4 && c.X < 18 && c.Z > 4 && c.Z < 18 {
				t.Fatal("dump within the bedroom clearance", s)
			}
		}
	}
	// A storeroom neighbour is fine: the nearest legal site hugs the
	// clearance square, not the storeroom's.
	if d := squaredDistance(domain.Cell{X: sites[0].X, Z: sites[0].Z}, req.Anchor); d > 60 {
		t.Fatal("nearest site too far", sites[0], d)
	}
	// An unknown room role counts as living.
	unknown := bedroom
	unknown.Role = domain.Unknown[RoomRole]()
	req.Rooms = []Room{unknown}
	again, err := OutdoorDumpSites(req)
	if err != nil || len(again) == 0 || again[0] != sites[0] {
		t.Fatal(again, err)
	}
	// Unknown cells are never free.
	req.Cells[0].Walkable = domain.Unknown[bool]()
	req.Anchor = domain.Cell{}
	req.Rooms = nil
	sites, _ = OutdoorDumpSites(req)
	if sites[0].X == 0 && sites[0].Z == 0 {
		t.Fatal("site on an unknown cell", sites[0])
	}
}

// The worn dump (#1813) is sited like the other dumps: every candidate patch
// is unroofed and clear of living rooms, and the roofed ground beside the
// anchor (a freezer, a shed) is never offered, however near.
func TestWornDumpSiteIsUnroofedAndClearOfRooms(t *testing.T) {
	roofed := func(c domain.Cell) bool { return c.X >= 8 && c.X < 16 && c.Z >= 8 && c.Z < 16 }
	var bedroom []domain.Cell
	for x := int32(30); x < 33; x++ {
		for z := int32(10); z < 13; z++ {
			bedroom = append(bedroom, domain.Cell{X: x, Z: z})
		}
	}
	rooms := []Room{{ID: "Room_1", Role: domain.Known(RoomRoleBedroom), Cells: bedroom}}
	plan := PlanStorage(StorageRequest{
		Bounds: Bounds{Width: 40, Height: 30}, Cells: dumpCensus(40, 30, roofed),
		Dumps: &DumpStore{Needs: map[string]int{domain.WornDumpRole: 3}, Rooms: rooms, Anchor: domain.Cell{X: 11, Z: 11}},
	})
	var found bool
	for _, s := range plan.Sites {
		if s.Role != domain.WornDumpRole {
			continue
		}
		found = true
		if len(s.Candidates) == 0 {
			t.Fatal("worn dump has no candidates")
		}
		for _, cand := range s.Candidates {
			for _, c := range cand {
				if roofed(c) {
					t.Fatal("roofed candidate", cand)
				}
				if c.X > 24 && c.X < 38 && c.Z > 4 && c.Z < 18 {
					t.Fatal("candidate within bedroom clearance", cand)
				}
			}
		}
	}
	if !found {
		t.Fatalf("no worn dump site: %+v", plan.Sites)
	}
}
