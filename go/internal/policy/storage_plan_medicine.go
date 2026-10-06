package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// maxMedicineHaulConsumers bounds the beds the haul-distance ranking walks
// from: each medical bed is one unit of traffic.
const maxMedicineHaulConsumers = 64

// medicineStore is the hospital's medicine store (#1776): the hospital-hosting
// room with the most medical beds (room id order on a tie) gets one 2x2
// medicine zone on the free roofed patch nearest those beds by
// traffic-weighted walking distance (#723). No site while the room census or
// the bed census is unknown, no hosted room has a medical bed, or nothing
// fits. The role registry retires the zone once the room stops hosting a
// hospital.
func (r StorageRequest) medicineStore() (Store, bool) {
	if r.Rooms == nil || r.Sleeping == nil {
		return Store{}, false
	}
	facility, err := Facility(RoomRoleHospital)
	if err != nil {
		return Store{}, false
	}
	beds := map[string][]domain.Cell{}
	for _, bed := range r.Sleeping.Beds {
		medical, mk := bed.Medical.Value()
		room, rk := bed.Room.Value()
		if mk && rk && medical {
			beds[room] = append(beds[room], bed.Cell)
		}
	}
	var hosted []Room
	for _, room := range r.Rooms.Rooms {
		role, known := room.Role.Value()
		if known && facility.Hosts(role) && len(beds[room.ID]) > 0 && len(room.Cells) > 0 {
			hosted = append(hosted, room)
		}
	}
	if len(hosted) == 0 {
		return Store{}, false
	}
	sort.Slice(hosted, func(i, j int) bool {
		if a, b := len(beds[hosted[i].ID]), len(beds[hosted[j].ID]); a != b {
			return a > b
		}
		return hosted[i].ID < hosted[j].ID
	})
	room, bedCells := hosted[0], beds[hosted[0].ID]
	inside := cellSet(room.Cells)
	var scoped []SiteCell
	for _, c := range r.Cells {
		if inside[c.Cell] {
			scoped = append(scoped, c)
		}
	}
	if len(scoped) == 0 {
		return Store{}, false
	}
	rects, err := CoveredStorageSites(CoveredStorageRequest{Bounds: r.Bounds, Anchor: bedCells[0], Cells: scoped, Protected: r.Protected})
	if err != nil || len(rects) == 0 {
		return Store{}, false
	}
	consumers := make([]HaulConsumer, 0, len(bedCells))
	for _, c := range bedCells {
		consumers = append(consumers, HaulConsumer{Cells: []domain.Cell{c}, Weight: 1})
	}
	if len(consumers) > maxMedicineHaulConsumers {
		consumers = consumers[:maxMedicineHaulConsumers]
	}
	costs, err := HaulCosts(r.Cells, consumers)
	if err != nil {
		return Store{}, false
	}
	best := RankSitesByHaul(rects, costs)[0]
	// The anchor is the best patch's centre: the store takes that patch, else
	// the free one nearest it.
	site := StoreSite{Role: domain.MedicineRolePrefix + room.ID, Width: best.Width, Height: best.Height, Anchor: domain.Cell{X: best.X + best.Width/2, Z: best.Z + best.Height/2},
		Filter: domain.MedicineFilter(), Priority: domain.ImportantPriority, room: room.Cells}
	return Store{StoreSite: site}, true
}
