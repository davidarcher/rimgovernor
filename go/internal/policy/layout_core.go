package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Core spine and room slots (#779, A3). The core is a straight 3-wide main
// hallway along X, crossed by north-south hallways as it fills (#952,
// layout_spines.go), with rooms hung off both sides. Neighbouring rooms on a
// side share their side walls; every room's door sits in its hallway wall,
// so no room is a thoroughfare. The core takes cells only from the core
// candidates (ZoneCore); a room over rock (ZoneMining) is Dug. The other
// zones give way to the core wherever they overlap it.

// Room roles the v2 core adds beside the master-plan ones; the jail is
// ModulePrison.
const (
	ModuleBedroom ModuleRole = "bedroom"
	// ModuleShelter is the temporary starter room (#2037), sited apart from the
	// core (layout_shelter.go) so its ground frees cleanly once it is demolished.
	ModuleShelter ModuleRole = "shelter"
	ModuleDining  ModuleRole = "dining"
	ModuleRec     ModuleRole = "rec"
	ModuleLab     ModuleRole = "lab"
	// ModuleTomb is the sarcophagus room (#832), shelled only once a
	// colonist lies dead.
	ModuleTomb ModuleRole = "tomb"
	// ModuleMorgue is the cold room for fresh stranger corpses (#1820),
	// beside the tomb and shelled only once a butcherable stranger corpse
	// lies waiting.
	ModuleMorgue ModuleRole = "morgue"
)

// coreRoomSize is a role's interior: width along the spine, depth away
// from it. Bedrooms live in the wing (layout_wing.go).
var coreRoomSize = map[ModuleRole][2]int32{
	ModuleKitchen:  {6, 5},
	ModuleFreezer:  {5, 5},
	ModuleButchery: {4, 4},
	ModuleDining:   {9, 7},
	ModuleRec:      {9, 7},
	ModuleHospital: {7, 5},
	ModulePrison:   {5, 5},
	ModuleWorkshop: {7, 5},
	ModuleStorage:  {9, 7},
	ModuleLab:      {6, 5},
	ModuleTomb:     {5, 5},
	ModuleMorgue:   {5, 4},
	// The rooms below are added on demand (layout_demand_rooms.go,
	// layout_gear.go), not in coreBaseRooms.
	ModuleArmory:   {7, 5},
	ModuleWardrobe: {7, 5},
	ModuleBattery:  batteryRoomSize,
}

// coreBaseRooms is every colony's essential set, in placement order: pairs
// that trade goods sit side by side. Each room takes the nearest free slot,
// so order is centrality: dining lands near the centre (#1535). The bedroom
// wings are sited with them. Every other room is grown when a need shows
// (demandCoreRooms), so nothing is dug or reserved for a room that is not
// up for building.
var coreBaseRooms = []ModuleRole{
	ModuleKitchen, ModuleFreezer, ModuleDining,
	ModuleWorkshop, ModuleStorage,
}

// coreMaxDepth is the deepest interior, which bounds the core's cross-section.
const coreMaxDepth int32 = 7

type coreGrid struct {
	core, rock map[domain.Cell]bool
	// soil is each surveyed cell's build cost (#1284); nil costs nothing.
	soil map[domain.Cell]int
	// fixed are the interiors of the rooms a replan must not move or change
	// (#1958): the search operators and the second-door pass leave them be.
	fixed map[Rectangle]bool
	// skip are the room interiors the packer refuses: the slots SiteRoom has scored.
	skip map[Rectangle]bool
	// noShelter keeps the generator from siting a shelter: a replan never
	// regrows one a retirement (#2046) dropped.
	noShelter bool
}

// Soil build costs per cell (#1279/#1284): rich soil costs more than
// plain soil, which costs more than anything else (bare ground, rock).
// Rich soil (fertility above zoneRichFertility, e.g. 140%) is the best
// farmland on the map, so a room over it costs 50x plain soil: still a cost,
// not a ban, but only a site with no other ground pays it.
const (
	soilCostRich   = 100
	soilCostNormal = 2
	soilCostOther  = 0
)

// withSoil gives g a per-cell build cost from the survey's fertility.
func (g coreGrid) withSoil(s MapSurvey) coreGrid {
	g.soil = surveySoil(s)
	return g
}

// surveySoil is each surveyed cell's build cost.
func surveySoil(s MapSurvey) map[domain.Cell]int {
	soil := make(map[domain.Cell]int, len(s.Cells))
	for _, c := range s.Cells {
		switch {
		case c.Rock:
			soil[c.Cell] = soilCostOther
		case c.Fertility > zoneRichFertility:
			soil[c.Cell] = soilCostRich
		case c.Fertility >= zoneFieldFertility:
			soil[c.Cell] = soilCostNormal
		default:
			soil[c.Cell] = soilCostOther
		}
	}
	return soil
}

// soilCost sums the build cost of r's cells.
func (g coreGrid) soilCost(r Rectangle) int {
	n := 0
	for x := r.X; x < r.X+r.Width; x++ {
		for z := r.Z; z < r.Z+r.Height; z++ {
			n += g.soil[domain.Cell{X: x, Z: z}]
		}
	}
	return n
}

// newCoreGrid takes reserved sites out of the core candidates.
func newCoreGrid(zones []LayoutZone, reserved []LayoutReservation) coreGrid {
	g := coreGrid{core: map[domain.Cell]bool{}, rock: map[domain.Cell]bool{}}
	for _, z := range zones {
		var set map[domain.Cell]bool
		switch z.Kind {
		case ZoneCore:
			set = g.core
		case ZoneMining:
			set = g.rock
		default:
			continue
		}
		for _, r := range z.Runs {
			for x := r.X; x < r.X+r.Length; x++ {
				set[domain.Cell{X: x, Z: r.Z}] = true
			}
		}
	}
	for _, r := range reserved {
		for x := r.Area.X; x < r.Area.X+r.Area.Width; x++ {
			for z := r.Area.Z; z < r.Area.Z+r.Area.Height; z++ {
				delete(g.core, domain.Cell{X: x, Z: z})
			}
		}
	}
	return g
}

// column reports the whole core cross-section at x around spine row z:
// hallway plus the deepest room and its walls on both sides.
func (g coreGrid) column(x, z int32) bool {
	half := SpineWidth/2 + coreMaxDepth + 2
	for dz := -half; dz <= half; dz++ {
		if !g.core[domain.Cell{X: x, Z: z + dz}] {
			return false
		}
	}
	return true
}

// seed picks the spine start: the column-fitting cell nearest the core
// candidates' centroid.
func (g coreGrid) seed() (domain.Cell, bool) {
	var sx, sz int64
	for c := range g.core {
		sx += int64(c.X)
		sz += int64(c.Z)
	}
	n := int64(len(g.core))
	cx, cz := int32(sx/n), int32(sz/n)
	best, found, bestD := domain.Cell{}, false, int64(-1)
	for c := range g.core {
		dx, dz := int64(c.X-cx), int64(c.Z-cz)
		d := dx*dx + dz*dz
		if found && (d > bestD || d == bestD && (c.Z > best.Z || c.Z == best.Z && c.X > best.X)) {
			continue
		}
		// The seed is a hallway end: its one-cell cap needs core too (#1982).
		if g.column(c.X-1, c.Z) && g.column(c.X, c.Z) && g.column(c.X+1, c.Z) {
			best, found, bestD = c, true, d
		}
	}
	return best, found
}

func (g coreGrid) dug(r LayoutRoom) bool {
	in := r.Interior
	for x := in.X; x < in.X+in.Width; x++ {
		for z := in.Z; z < in.Z+in.Height; z++ {
			if g.rock[domain.Cell{X: x, Z: z}] {
				return true
			}
		}
	}
	return false
}
