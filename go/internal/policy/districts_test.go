package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestDistrictsAreCompassWedgesAroundThePlaza(t *testing.T) {
	g := ColonyGrid{Origin: domain.Cell{X: 100, Z: 100}, Pitch: GridPitch, Axes: ColonyGridAxes}
	pitch := GridPitch
	cases := []struct {
		cell domain.Cell
		want District
	}{
		{domain.Cell{X: 100, Z: 100}, DistrictPlaza},
		{domain.Cell{X: 112, Z: 115}, DistrictPlaza},
		{domain.Cell{X: 100, Z: 100 + pitch}, DistrictHousing},
		{domain.Cell{X: 100 + pitch, Z: 100 + pitch}, DistrictHousing},
		{domain.Cell{X: 100 + pitch, Z: 100}, DistrictProduction},
		{domain.Cell{X: 100 - pitch, Z: 100}, DistrictStorage},
		{domain.Cell{X: 100, Z: 100 - pitch}, DistrictFields},
		{domain.Cell{X: 100 - pitch, Z: 100 - pitch}, DistrictFields},
		{domain.Cell{X: 100 + 2*pitch, Z: 100 + pitch}, DistrictProduction},
		{domain.Cell{X: 100 + 3*pitch, Z: 100}, DistrictDefense},
		{domain.Cell{X: 100 - 3*pitch, Z: 100 + 3*pitch}, DistrictDefense},
		{domain.Cell{X: 100 + 4*pitch, Z: 100}, DistrictProduction},
	}
	for _, c := range cases {
		if got := g.District(c.cell); got != c.want {
			t.Fatalf("District(%v) = %s, want %s", c.cell, got, c.want)
		}
		if again := g.District(c.cell); again != c.want {
			t.Fatalf("District(%v) is not deterministic", c.cell)
		}
	}
	if (ColonyGrid{}).District(domain.Cell{X: 5, Z: 5}) != DistrictPlaza {
		t.Fatal("an invalid grid is all plaza")
	}
}

func TestDistrictsWindPutsFieldsDownwindOfStorage(t *testing.T) {
	g := ColonyGrid{Origin: domain.Cell{X: 100, Z: 100}, Pitch: GridPitch, Axes: ColonyGridAxes}
	// Wind blowing east: fields east, storage west, production north
	// (a quarter turn on from east), housing south.
	d := Districts{Grid: g, Wind: domain.Known(domain.Cell{X: 1, Z: 0})}
	pitch := GridPitch
	want := map[District]domain.Cell{
		DistrictFields:     {X: 100 + pitch, Z: 100},
		DistrictStorage:    {X: 100 - pitch, Z: 100},
		DistrictProduction: {X: 100, Z: 100 + pitch},
		DistrictHousing:    {X: 100, Z: 100 - pitch},
	}
	for district, cell := range want {
		if got := d.District(cell); got != district {
			t.Fatalf("District(%v) = %s, want %s", cell, got, district)
		}
	}
	if anchor, ok := d.Anchor(DistrictFields, nil); !ok || anchor != (domain.Cell{X: 100 + pitch + 6, Z: 106}) {
		t.Fatalf("fields anchor %v %v", anchor, ok)
	}
}

func TestDistrictsForExtentWidensTheDefenseRing(t *testing.T) {
	g := ColonyGrid{Origin: domain.Cell{X: 100, Z: 100}, Pitch: GridPitch, Axes: ColonyGridAxes}
	extent := ColonyExtent{Regions: []ExtentRegion{{Cells: []ExtentCell{{Cell: domain.Cell{X: 100 + 4*GridPitch, Z: 100}}}}}}
	d := DistrictsFor(g, domain.Known(extent))
	if d.Radius != 5 {
		t.Fatalf("radius %d", d.Radius)
	}
	if d.District(domain.Cell{X: 100 + 5*GridPitch, Z: 100}) != DistrictDefense || d.District(domain.Cell{X: 100 + 3*GridPitch, Z: 100}) != DistrictProduction {
		t.Fatal("the defense ring did not move past the extent")
	}
	if DistrictsFor(g, domain.Unknown[ColonyExtent]()).Radius != 0 {
		t.Fatal("no extent keeps the default ring")
	}
}

func TestDistrictAnchorTakesTheNearestFreeModuleOrFallsBack(t *testing.T) {
	g := ColonyGrid{Origin: domain.Cell{X: 100, Z: 100}, Pitch: GridPitch, Axes: ColonyGridAxes}
	d := Districts{Grid: g}
	// A bedroom anchors in Housing, the module north of the plaza; a
	// workshop in Production, the module east of it.
	housing, ok := d.Anchor(RoomDistrict(RoomRoleBedroom), nil)
	if !ok || housing != (domain.Cell{X: 106, Z: 100 + GridPitch + 6}) {
		t.Fatalf("housing anchor %v %v", housing, ok)
	}
	production, ok := d.Anchor(RoomDistrict(RoomRoleWorkshop), nil)
	if !ok || production != (domain.Cell{X: 100 + GridPitch + 6, Z: 106}) {
		t.Fatalf("production anchor %v %v", production, ok)
	}
	if g.District(housing) != DistrictHousing || g.District(production) != DistrictProduction {
		t.Fatal("anchors lie outside their districts")
	}
	// With the first Housing module taken, the anchor moves to the next
	// module of the wedge in the same ring, then the next ring.
	taken := map[Rectangle]bool{g.Module(housing): true}
	free := func(m Rectangle) bool { return !taken[m] }
	next, ok := d.Anchor(DistrictHousing, free)
	if !ok || next == housing || g.District(next) != DistrictHousing || max(next.Z-housing.Z, housing.Z-next.Z) > GridPitch {
		t.Fatalf("next housing anchor %v %v", next, ok)
	}
	// A district with no free module reports none.
	if _, ok := d.Anchor(DistrictHousing, func(Rectangle) bool { return false }); ok {
		t.Fatal("a full district has no anchor")
	}
	if _, ok := (Districts{}).Anchor(DistrictHousing, nil); ok {
		t.Fatal("an invalid grid has no anchor")
	}
}

func TestRoomDistrictTable(t *testing.T) {
	for _, f := range FacilityCatalog() {
		switch d := RoomDistrict(f.Role); d {
		case DistrictHousing, DistrictProduction, DistrictStorage, DistrictFields, DistrictPlaza:
		default:
			t.Fatalf("%s: district %q", f.Role, d)
		}
	}
	if RoomDistrict(RoomRoleBedroom) != DistrictHousing || RoomDistrict(RoomRoleWorkshop) != DistrictProduction || RoomDistrict(RoomRoleStoreroom) != DistrictStorage || RoomDistrict(RoomRoleDiningRoom) != DistrictPlaza {
		t.Fatal("room role table")
	}
}
