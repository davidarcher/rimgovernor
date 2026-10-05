package policy

import "sort"

// Tier-styled buildings (#610): each build tier looks different because the
// floor under each room role and the lighting fixture advance with
// BuildTier, all through existing Core defs and stuff choices. Every rule here is a pure f(tier, role, stock) -> def/stuff with
// one stock fallback: when the tier's rung names a material the colony does
// not hold, the rung one tier down is tried, and nothing further. A rule
// never proposes a stuff the colony has none of, so a Camp colony never
// receives a stone, powered or floored proposal.
//
// The shell planners take wall and door stuff from the def stats
// (ColonyProjection.BulkBuildStuff, not a tier rule); the flooring planner
// prefers FloorDef's floor for a deficient room's role and
// the lighting planner PlannedLighting's fixture (buildingruntime's
// rounds_tier_style.go). The shape-family rules (double-module hall,
// paired wings, courtyard) are shape_family.go's.

// TierStyleStock is the resource census the rules read: available count per
// resource. Zero or absent means the colony holds none.
type TierStyleStock map[Resource]int64

func (s TierStyleStock) has(resource Resource, count int64) bool { return s[resource] >= count }

// QuarriedStone is the biome's stone as the colony has quarried it: the
// stone block definition the census holds most of (ties break on name).
// A colony without blocks has no quarried stone.
func (s TierStyleStock) QuarriedStone() (Resource, bool) {
	var blocks []Resource
	for _, b := range stoneChunkBlocks {
		if s[b] > 0 {
			blocks = append(blocks, b)
		}
	}
	if len(blocks) == 0 {
		return "", false
	}
	sort.Slice(blocks, func(i, j int) bool {
		if s[blocks[i]] != s[blocks[j]] {
			return s[blocks[i]] > s[blocks[j]]
		}
		return blocks[i] < blocks[j]
	})
	return blocks[0], true
}

// stoneSuffix is the stone name a block definition carries, the suffix the
// generated floor definitions share: BlocksGranite -> Granite ->
// TileGranite, FlagstoneGranite.
func stoneSuffix(blocks Resource) string {
	const prefix = "Blocks"
	if len(blocks) <= len(prefix) || blocks[:len(prefix)] != prefix {
		return ""
	}
	return string(blocks[len(prefix):])
}

// oneRungDown tries rung(tier) and, when that rung names nothing the
// colony holds, rung(tier-1) once; Camp has no rung below it.
func oneRungDown[T any](tier BuildTier, rung func(BuildTier) (T, bool)) (T, bool) {
	if v, ok := rung(tier); ok {
		return v, true
	}
	if tier > BuildTierCamp {
		return rung(tier - 1)
	}
	var zero T
	return zero, false
}

// ShellWallBudget is the wall placements a shell and its rooms run to: a
// stuff builds the walls only when the stock, with the wood standing as
// trees, covers this many (a few hundred cells at 5 units each).
const ShellWallBudget int64 = 200

// floorClass groups the room roles the floor rule tells apart.
type floorClass int

const (
	floorNone floorClass = iota
	floorAisle
	floorLiving
	floorSoft
	floorHospital
)

// floorClassOf maps a room role to its floor class: aisles (RoomRoleNone,
// the cells between modules) and storage take flagstone; bedrooms, dining,
// workshops and the other lived-in rooms take stone tile; bedrooms and
// recreation may take carpet; the hospital takes sterile tile. Roles not
// listed (tombs, barns, prison cells) receive no floor.
func floorClassOf(role RoomRole) floorClass {
	switch role {
	case RoomRoleNone, RoomRoleStoreroom:
		return floorAisle
	case RoomRoleBedroom, RoomRoleRecRoom:
		return floorSoft
	case RoomRoleDiningRoom, RoomRoleWorkshop, RoomRoleKitchen, RoomRoleLaboratory, RoomRoleBarracks, RoomRoleRoom:
		return floorLiving
	case RoomRoleHospital:
		return floorHospital
	}
	return floorNone
}

// FloorStyleFacts is what FloorDef reads beyond tier, role and stock: the
// finished research that gates carpet (CarpetMaking, the Core prerequisite
// the issue calls Complex Furniture) and sterile tile (SterileMaterials),
// and the cost list per cell of each floor those research projects unlock
// (the terrain defs' cost lists, by name); a floor with no known cost list
// cannot be afforded.
type FloorStyleFacts struct {
	CarpetMaking, SterileMaterials bool
	Costs                          map[string][]Amount
}

// affordable reports that stock covers one cell of the floor def.
func (f FloorStyleFacts) affordable(def string, stock TierStyleStock) bool {
	costs, known := f.Costs[def]
	if !known {
		return false
	}
	for _, c := range costs {
		if !stock.has(c.Resource, c.Count) {
			return false
		}
	}
	return true
}

// Carpet is the generated Core carpet definition the soft rooms take; one
// colour keeps the proposal deterministic.
const Carpet = "CarpetRed"

// SterileTile is the hospital's sterile floor.
const SterileTile = "SterileTile"

// FloorDef is the floor rule: nothing at Camp; from Masonry flagstone of the
// quarried stone on aisles and in storage and stone tile in the lived-in
// rooms; sterile tile in the hospital at Industrial once SterileMaterials
// is finished and steel and silver stock allow; carpet in bedrooms and
// recreation once CarpetMaking is finished and cloth stock allows. Each
// upgrade falls back one rung to the stone floor, and the stone floor to
// nothing.
func FloorDef(tier BuildTier, role RoomRole, stock TierStyleStock, facts FloorStyleFacts) (string, bool) {
	class := floorClassOf(role)
	if class == floorNone {
		return "", false
	}
	stoneFloor := func() (string, bool) {
		stone, ok := stock.QuarriedStone()
		if !ok {
			return "", false
		}
		if class == floorAisle {
			return "Flagstone" + stoneSuffix(stone), true
		}
		return "Tile" + stoneSuffix(stone), true
	}
	return oneRungDown(tier, func(t BuildTier) (string, bool) {
		if t < BuildTierMasonry {
			return "", false
		}
		switch {
		case class == floorHospital && t >= BuildTierIndustrial:
			if facts.SterileMaterials && facts.affordable(SterileTile, stock) {
				return SterileTile, true
			}
			// Sterile tile unmet: the rung below Industrial is the stone
			// floor, and Spacer shares Industrial's rung.
			if t > BuildTierIndustrial {
				return stoneFloor()
			}
			return "", false
		case class == floorSoft && facts.CarpetMaking && facts.affordable(Carpet, stock):
			return Carpet, true
		}
		return stoneFloor()
	})
}

// LightingScope says how many fixtures a module takes.
type LightingScope int

const (
	// LightingPerModule places one fixture per module.
	LightingPerModule LightingScope = iota
	// LightingPerSubCell places one fixture per 5x5 sub-cell.
	LightingPerSubCell
)

// LightingStyle is a lighting proposal for one module.
type LightingStyle struct {
	Definition string
	Scope      LightingScope
}

// PlannedLighting is the lighting rule: nothing at Camp; a torch per module
// at Masonry; at Powered with a powered source, a standing lamp per 5x5
// sub-cell, or one sun lamp per farm module. A powered rung without power
// or the steel for a lamp falls back to the torch, and a torch without
// wood to nothing.
func PlannedLighting(tier BuildTier, farm bool, stock TierStyleStock, powered bool) (LightingStyle, bool) {
	const torchWood, lampSteel int64 = 20, 20
	return oneRungDown(tier, func(t BuildTier) (LightingStyle, bool) {
		switch {
		case t >= BuildTierPowered:
			if !powered || !stock.has("Steel", lampSteel) {
				return LightingStyle{}, false
			}
			if farm {
				return LightingStyle{"SunLamp", LightingPerModule}, true
			}
			return LightingStyle{"StandingLamp", LightingPerSubCell}, true
		case t >= BuildTierMasonry:
			if farm || !stock.has("WoodLog", torchWood) {
				return LightingStyle{}, false
			}
			return LightingStyle{"TorchLamp", LightingPerModule}, true
		}
		return LightingStyle{}, false
	})
}
