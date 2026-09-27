package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestMedicineStorageSitsBesideTheMedicalBeds(t *testing.T) {
	t.Parallel()
	var cells []policy.SiteCell
	var ward []domain.Cell
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			cell := domain.Cell{X: x, Z: z}
			cells = append(cells, policy.SiteCell{Cell: cell, Roofed: domain.Known(true), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), StorageEmpty: domain.Known(true)})
			if x >= 1 && x < 19 && z >= 1 && z < 19 {
				ward = append(ward, cell)
			}
		}
	}
	rooms := policy.RoomObservation{Rooms: []policy.Room{{ID: "ward", Role: domain.Known(policy.RoomRoleHospital), Cells: ward, Beds: []string{"b1", "b2"}}}}
	bed := func(id string, at domain.Cell) policy.SleepingBed {
		return policy.SleepingBed{ID: id, Medical: domain.Known(true), Room: domain.Known("ward"), Cell: at}
	}
	beds := []domain.Cell{{X: 16, Z: 16}, {X: 17, Z: 16}}
	sleeping := policy.SleepingObservation{Beds: []policy.SleepingBed{bed("b1", beds[0]), bed("b2", beds[1])}}
	site, err := medicineStorageSites(rooms, sleeping, policy.Bounds{Width: 20, Height: 20}, cells, beds)
	if err != nil || site.Room != "ward" || len(site.Sites) == 0 {
		t.Fatal(site, err)
	}
	for _, c := range site.Sites[0] {
		if c.X < 13 || c.Z < 13 {
			t.Fatal("medicine must sit beside the medical beds", site.Sites[0])
		}
	}
	filter, err := medicineFilter()
	if err != nil || filter.Base() != domain.BaseNothing || len(filter.Allow()) != 1 {
		t.Fatal(filter, err)
	}
	// No medical bed: no medicine store.
	sleeping.Beds[0].Medical, sleeping.Beds[1].Medical = domain.Known(false), domain.Known(false)
	if none, err := medicineStorageSites(rooms, sleeping, policy.Bounds{Width: 20, Height: 20}, cells, nil); err != nil || none.Room != "" {
		t.Fatal(none, err)
	}
}
