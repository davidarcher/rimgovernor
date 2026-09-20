package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// District is the coarse sector of the colony grid a cell belongs to
// (#609). Sectors are assigned by direction from the origin module: the
// origin module itself is the plaza, and every other module joins the
// sector of the axis it lies furthest along, so a district is a wedge of
// modules the colony grows outward through. Districts never move once a
// grid is fixed unless wind or the colony extent changes them, and both
// only rotate or widen the wedges deterministically.
type District string

const (
	// DistrictPlaza is the origin module: the starter shell and the common
	// rooms around it.
	DistrictPlaza District = "plaza"
	// DistrictHousing holds bedrooms, barracks and hospitals.
	DistrictHousing District = "housing"
	// DistrictProduction holds workshops, laboratories and kitchens.
	DistrictProduction District = "production"
	// DistrictStorage holds storerooms and covered stockpiles.
	DistrictStorage District = "storage"
	// DistrictFields holds growing zones, pens and barns, downwind of
	// storage when the prevailing wind is known.
	DistrictFields District = "fields"
	// DistrictDefense is the ring of modules on the colony's perimeter.
	DistrictDefense District = "defense"
)

// districtDefaultRadius is the Chebyshev module distance of the defense
// ring when no colony extent widens it: three modules out, one past the
// two rings the wedges fill first.
const districtDefaultRadius int32 = 3

// Districts is the district assignment over one grid.
type Districts struct {
	Grid ColonyGrid
	// Wind is the unit vector, along the grid's axes, the prevailing wind
	// blows toward when known: Fields lie in that sector, Storage upwind of
	// them and Production and Housing across. Unknown keeps the default
	// compass: Housing along +axis1, Production +axis0, Storage -axis0 and
	// Fields -axis1.
	Wind domain.Fact[domain.Cell]
	// Radius is the Chebyshev module distance of the defense ring; zero is
	// districtDefaultRadius.
	Radius int32
}

// DistrictsFor derives the districts of a grid from the colony extent
// (#453) when it is known: the defense ring sits one module past the
// furthest module the extent reaches, never inside the default ring.
func DistrictsFor(grid ColonyGrid, extent domain.Fact[ColonyExtent]) Districts {
	d := Districts{Grid: grid}
	if e, known := extent.Value(); known && grid.Valid() {
		for _, region := range e.Regions {
			for _, c := range region.Cells {
				mu, mv := grid.module(c.Cell)
				d.Radius = max(d.Radius, max(mu, -mu), max(mv, -mv))
			}
		}
		d.Radius++
	}
	return d
}

// module returns a cell's module coordinates: the origin module is (0,0)
// and each pitch square along an axis counts one.
func (g ColonyGrid) module(c domain.Cell) (mu, mv int32) {
	u, v := g.local(c)
	return floorDiv(u, g.Pitch), floorDiv(v, g.Pitch)
}

// radius is the defense ring's module distance.
func (d Districts) radius() int32 {
	if d.Radius > 0 {
		return d.Radius
	}
	return districtDefaultRadius
}

// sectors lists the four wedge districts in the order +axis1, +axis0,
// -axis0, -axis1 (north, east, west, south on the map-aligned grid).
func (d Districts) sectors() [4]District {
	out := [4]District{DistrictHousing, DistrictProduction, DistrictStorage, DistrictFields}
	w, known := d.Wind.Value()
	if !known || (w.X == 0) == (w.Z == 0) {
		return out
	}
	// Fields downwind, Storage upwind, Production a quarter turn on from
	// the wind, Housing opposite Production.
	index := func(v domain.Cell) int {
		switch {
		case v.Z > 0:
			return 0
		case v.X > 0:
			return 1
		case v.X < 0:
			return 2
		}
		return 3
	}
	turn := domain.Cell{X: -w.Z, Z: w.X}
	out[index(w)], out[index(domain.Cell{X: -w.X, Z: -w.Z})] = DistrictFields, DistrictStorage
	out[index(turn)], out[index(domain.Cell{X: -turn.X, Z: -turn.Z})] = DistrictProduction, DistrictHousing
	return out
}

// districtOf assigns module coordinates a district.
func (d Districts) districtOf(mu, mv int32) District {
	if mu == 0 && mv == 0 {
		return DistrictPlaza
	}
	if max(mu, -mu, mv, -mv) == d.radius() {
		return DistrictDefense
	}
	sectors := d.sectors()
	switch {
	case max(mv, -mv) >= max(mu, -mu) && mv > 0:
		return sectors[0]
	case max(mv, -mv) >= max(mu, -mu):
		return sectors[3]
	case mu > 0:
		return sectors[1]
	}
	return sectors[2]
}

// District names the district a cell belongs to; an invalid grid puts
// every cell in the plaza.
func (d Districts) District(c domain.Cell) District {
	if !d.Grid.Valid() {
		return DistrictPlaza
	}
	return d.districtOf(d.Grid.module(c))
}

// District names the district a cell belongs to under the grid's default
// districts (no wind, the default defense ring).
func (g ColonyGrid) District(c domain.Cell) District { return Districts{Grid: g}.District(c) }

// Anchor is the centre cell of the district's module nearest the origin
// that free accepts (every module when free is nil), the point a routine
// anchors its site search on: modules are tried ring by ring outward, and
// within a ring nearest the origin first, then by their coordinates, so
// the choice is deterministic. It
// reports false when the district has no free module within the defense
// ring, and the caller falls back to the starter centre.
func (d Districts) Anchor(district District, free func(module Rectangle) bool) (domain.Cell, bool) {
	if !d.Grid.Valid() {
		return domain.Cell{}, false
	}
	for ring := int32(0); ring <= d.radius(); ring++ {
		var modules [][2]int32
		for mu := -ring; mu <= ring; mu++ {
			for mv := -ring; mv <= ring; mv++ {
				if max(mu, -mu, mv, -mv) == ring && d.districtOf(mu, mv) == district {
					modules = append(modules, [2]int32{mu, mv})
				}
			}
		}
		sort.Slice(modules, func(i, j int) bool {
			a, b := modules[i], modules[j]
			da, db := a[0]*a[0]+a[1]*a[1], b[0]*b[0]+b[1]*b[1]
			if da != db {
				return da < db
			}
			return a[0] < b[0] || a[0] == b[0] && a[1] < b[1]
		})
		for _, m := range modules {
			module := d.Grid.rectangle(m[0]*d.Grid.Pitch, m[1]*d.Grid.Pitch, ColonyGridModule, ColonyGridModule)
			if free == nil || free(module) {
				return domain.Cell{X: module.X + module.Width/2, Z: module.Z + module.Height/2}, true
			}
		}
	}
	return domain.Cell{}, false
}

// RoomDistrict is the district a room role is sited in: sleeping and care
// in Housing, benches and kitchens in Production, stores in Storage, barns
// with the Fields, and the common rooms on the plaza. Roles the catalog does
// not place yet default to the plaza beside the starter shell.
func RoomDistrict(role RoomRole) District {
	switch role {
	case RoomRoleBedroom, RoomRoleBarracks, RoomRoleHospital, RoomRolePrisonCell, RoomRolePrisonBarracks,
		RoomRoleNursery, RoomRoleDeathrestChamber, RoomRoleContainmentCell:
		return DistrictHousing
	case RoomRoleWorkshop, RoomRoleLaboratory, RoomRoleKitchen:
		return DistrictProduction
	case RoomRoleStoreroom:
		return DistrictStorage
	case RoomRoleBarn:
		return DistrictFields
	}
	return DistrictPlaza
}
