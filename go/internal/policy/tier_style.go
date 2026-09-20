package policy

import "sort"

// Tier-styled buildings (#610): each build tier looks different because the
// wall stuff, the floor under each room role, the door and the lighting
// fixture advance with BuildTier, all through existing Core defs and stuff
// choices. Every rule here is a pure f(tier, role, stock) -> def/stuff with
// one stock fallback: when the tier's rung names a material the colony does
// not hold, the rung one tier down is tried, and nothing further. A rule
// never proposes a stuff the colony has none of, so a Camp colony never
// receives a stone, powered or floored proposal.
//
// Deferred to C6 (#609): the shape-family rules (double-module hall, paired
// wings, courtyard). The routines still choose their own materials; wiring
// these rules into the shell, door and lighting planners follows.

// TierStyleStock is the resource census the rules read: available count per
// resource. Zero or absent means the colony holds none.
type TierStyleStock map[Resource]int64

// TierStyleStockOf folds a stock census into the map the rules read,
// summing repeated rows and ignoring negative counts.
func TierStyleStockOf(rows []Amount) TierStyleStock {
	stock := TierStyleStock{}
	for _, row := range rows {
		if row.Count > 0 {
			stock[row.Resource] += row.Count
		}
	}
	return stock
}

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

// WallPart distinguishes the parts of a shell the Industrial rung accents in
// steel (door frames and corners) from the plain runs between them.
type WallPart int

const (
	WallRun WallPart = iota
	WallCorner
	WallDoorFrame
)

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

// WallStuff is the wall stuff ladder: wood at Camp, the quarried stone
// blocks at Masonry and Powered, steel accents on corners and door frames
// over stone runs at Industrial, plasteel at Spacer. The fallback is one
// rung down; with nothing stocked on either rung there is no proposal.
func WallStuff(tier BuildTier, part WallPart, stock TierStyleStock) (Resource, bool) {
	return oneRungDown(tier, func(t BuildTier) (Resource, bool) {
		var want Resource
		switch {
		case t >= BuildTierSpacer:
			want = "Plasteel"
		case t >= BuildTierIndustrial && part != WallRun:
			want = "Steel"
		case t >= BuildTierMasonry:
			stone, ok := stock.QuarriedStone()
			if !ok {
				return "", false
			}
			want = stone
		default:
			want = "WoodLog"
		}
		if !stock.has(want, 1) {
			return "", false
		}
		return want, true
	})
}

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
// the issue calls Complex Furniture) and sterile tile (SterileMaterials).
type FloorStyleFacts struct {
	CarpetMaking, SterileMaterials bool
}

// Carpet is the generated Core carpet definition the soft rooms take; one
// colour keeps the proposal deterministic. carpetCloth is its cost per cell.
const (
	Carpet              = "CarpetRed"
	carpetCloth   int64 = 7
	sterileSteel  int64 = 3
	sterileSilver int64 = 12
)

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
			if facts.SterileMaterials && stock.has("Steel", sterileSteel) && stock.has("Silver", sterileSilver) {
				return "SterileTile", true
			}
			// Sterile tile unmet: the rung below Industrial is the stone
			// floor, and Spacer shares Industrial's rung.
			if t > BuildTierIndustrial {
				return stoneFloor()
			}
			return "", false
		case class == floorSoft && facts.CarpetMaking && stock.has("Cloth", carpetCloth):
			return Carpet, true
		}
		return stoneFloor()
	})
}

// DoorStyle is a door proposal: the definition and its stuff.
type DoorStyle struct{ Definition, Stuff string }

// DoorDef is the door ladder: a wood Door at Camp, a stone Door at Masonry
// and Powered, a steel Door at Industrial, and an Autodoor (steel, plasteel
// at Spacer) at Industrial once Autodoors is finished and the colony has
// power. The fallback is one rung down.
func DoorDef(tier BuildTier, stock TierStyleStock, autodoors, powered bool) (DoorStyle, bool) {
	// autodoorSteel is the flat steel an Autodoor costs beside its stuff.
	const autodoorSteel int64 = 40
	return oneRungDown(tier, func(t BuildTier) (DoorStyle, bool) {
		if t >= BuildTierIndustrial {
			stuff := Resource("Steel")
			if t >= BuildTierSpacer && stock.has("Plasteel", 1) {
				stuff = "Plasteel"
			}
			if !stock.has(stuff, 1) {
				return DoorStyle{}, false
			}
			if autodoors && powered && stock.has("Steel", autodoorSteel) {
				return DoorStyle{"Autodoor", string(stuff)}, true
			}
			return DoorStyle{"Door", string(stuff)}, true
		}
		stuff, ok := WallStuff(t, WallRun, stock)
		if !ok {
			return DoorStyle{}, false
		}
		return DoorStyle{"Door", string(stuff)}, true
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

// ModuleLighting is the lighting rule: nothing at Camp; a torch per module
// at Masonry; at Powered with a powered source, a standing lamp per 5x5
// sub-cell, or one sun lamp per farm module. A powered rung without power
// or the steel for a lamp falls back to the torch, and a torch without
// wood to nothing.
func ModuleLighting(tier BuildTier, farm bool, stock TierStyleStock, powered bool) (LightingStyle, bool) {
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
