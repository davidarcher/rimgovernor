package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// District names what a site search wants from the layout plan (#771):
// planners ask for Housing, Storage or Fields and the plan maps it onto
// its rooms and field zones (DistrictAnchor, District).
type District string

const (
	// DistrictPlaza is the common rooms: dining and rec.
	DistrictPlaza District = "plaza"
	// DistrictHousing holds bedrooms, barracks, hospitals and the jail.
	DistrictHousing District = "housing"
	// DistrictProduction holds workshops, laboratories and kitchens.
	DistrictProduction District = "production"
	// DistrictStorage holds storerooms and covered stockpiles.
	DistrictStorage District = "storage"
	// DistrictFields holds growing zones, pens and barns.
	DistrictFields District = "fields"
	// DistrictDefense is the perimeter; it has no planned room.
	DistrictDefense District = "defense"
)

// RoomDistrict is the district a room role is sited in: sleeping and care
// in Housing, benches and kitchens in Production, stores in Storage, barns
// with the Fields, and the common rooms on the plaza. Roles the catalog does
// not place yet default to the plaza.
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

// districtRooms is the room role a district's site search anchors on:
// Housing on the barracks, Production on the workshop, Storage on the
// storeroom and the plaza on the dining room.
var districtRooms = map[District]ModuleRole{
	DistrictHousing:    ModuleBarracks,
	DistrictProduction: ModuleWorkshop,
	DistrictStorage:    ModuleStorage,
	DistrictPlaza:      ModuleDining,
}

// roomDistricts is the district each planned room role belongs to.
var roomDistricts = map[ModuleRole]District{
	ModuleBedroom:    DistrictHousing,
	ModuleBarracks:   DistrictHousing,
	ModuleHospital:   DistrictHousing,
	ModulePrison:     DistrictHousing,
	ModuleWorkshop:   DistrictProduction,
	ModuleLab:        DistrictProduction,
	ModuleKitchen:    DistrictProduction,
	ModuleFreezer:    DistrictProduction,
	ModuleStorage:    DistrictStorage,
	ModuleDining:     DistrictPlaza,
	ModuleRec:        DistrictPlaza,
	ModuleMealCloset: DistrictPlaza,
	ModuleTomb:       DistrictHousing,
}

// DistrictAnchor is where a district's site search starts: the interior
// centre of the first free planned room of its role (Anchor), or for
// Fields the middle of the first free field zone run. Defense has no
// room and reports false.
func (p LayoutPlan) DistrictAnchor(district District, free func(Rectangle) bool) (domain.Cell, bool) {
	if district == DistrictFields {
		for _, z := range p.Zones {
			if z.Kind != ZoneField {
				continue
			}
			for _, run := range z.Runs {
				c := domain.Cell{X: run.X + run.Length/2, Z: run.Z}
				if free == nil || free(Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1}) {
					return c, true
				}
			}
		}
		return domain.Cell{}, false
	}
	role, ok := districtRooms[district]
	if !ok {
		return domain.Cell{}, false
	}
	return p.Anchor(role, free)
}

// District is the district a cell lies in: the district of the planned
// room whose interior holds it, Fields inside a field zone, and "" off
// every room and field.
func (p LayoutPlan) District(c domain.Cell) District {
	for _, r := range p.AllRooms() {
		in := r.Interior
		if c.X >= in.X && c.X < in.X+in.Width && c.Z >= in.Z && c.Z < in.Z+in.Height {
			return roomDistricts[r.Role]
		}
	}
	for _, z := range p.Zones {
		if z.Kind != ZoneField {
			continue
		}
		for _, run := range z.Runs {
			if run.Z == c.Z && c.X >= run.X && c.X < run.X+run.Length {
				return DistrictFields
			}
		}
	}
	return ""
}
