package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Beauty levers (#830): an owned bedroom below its target whose weakest
// stat is beauty gets, one change at a time,
//   - a plant pot on free floor (native growers sow its default plant);
//   - a prettier floor: every cell whose terrain is less beautiful than the
//     most beautiful affordable floor the colony can lay, up to one plan's
//     worth of cells.
//
// The template furniture (NextRoomUpgrade) and bed (NextBedReplacement)
// levers go first; these only add beauty.

// PlantPotDefinition is the plant pot the beauty lever places.
const PlantPotDefinition = "PlantPot"

// maxBeautyFloorCells bounds one floor upgrade plan, as MaintainFlooring's
// MaxCellsPerPlan does.
const maxBeautyFloorCells = 24

// NextBeautyUpgrade returns the first (by room id) beauty upgrade due,
// false when none. A floor upgrade carries its cells in Cells (Def placed
// at each, North); a plant pot is one piece at Anchor.
func NextBeautyUpgrade(obs SleepingObservation, targets map[string]RoomTarget, rooms []TidyRoom, available func(string) bool, flooring domain.Fact[FlooringObservation], floors FlooringFacts) (RoomUpgrade, bool) {
	furniture := map[string]TidyRoom{}
	for _, r := range rooms {
		furniture[r.ID] = r
	}
	floorRooms := map[string]FloorRoom{}
	fo, fk := flooring.Value()
	if fk {
		for _, r := range fo.Rooms {
			floorRooms[r.ID] = r
		}
	}
	for _, id := range beautyRooms(obs, targets) {
		room, rk := furniture[id]
		if !rk {
			continue
		}
		pot := false
		for _, p := range room.Pieces {
			pot = pot || p.Def == PlantPotDefinition
		}
		if !pot && available(PlantPotDefinition) {
			if cell, _, ok := freeSpot(room, domain.Cell{X: 1, Z: 1}); ok {
				return RoomUpgrade{Room: id, Slot: "plant_pot", Def: PlantPotDefinition, Anchor: cell, Rot: domain.North, Weakest: RoomStatBeauty}, true
			}
		}
		if fr, ok := floorRooms[id]; ok && fk {
			if u, ok := floorUpgrade(id, fr, fo.Terrains, floors); ok {
				return u, true
			}
		}
	}
	return RoomUpgrade{}, false
}

// The sculpture lever (#830), after pots and floors: a finished packed
// small sculpture installed on free floor in the room. The sculpture itself
// is MaintainArt's pinned bill (#1190). Sculptures pack as the generic
// MinifiedThing (Buildings_Art.xml), so the packed read is filtered by
// inner definition (#1195).
const (
	SculptureDefinition       = "SculptureSmall"
	SculptureRecipe           = "Make_SculptureSmall"
	PackedSculptureDefinition = "MinifiedThing"
)

// SculptureStep installs Packed (a packed item's id) at Anchor, Rot.
type SculptureStep struct {
	Room   string
	Packed string
	Anchor domain.Cell
	Rot    domain.Rotation
}

// PackedSculpture is a packed sculpture in stock: its id, the sculpture
// definition inside, its quality (QualityCategory ordinal, -1 unknown) and
// market value (#1194).
type PackedSculpture struct {
	ID, Def     string
	Quality     int
	MarketValue float64
}

// rankSculptures orders packed best first: quality, then market value,
// then id.
func rankSculptures(packed []PackedSculpture) []PackedSculpture {
	out := append([]PackedSculpture(nil), packed...)
	sort.SliceStable(out, func(i, j int) bool {
		a, b := out[i], out[j]
		if a.Quality != b.Quality {
			return a.Quality > b.Quality
		}
		if a.MarketValue != b.MarketValue {
			return a.MarketValue > b.MarketValue
		}
		return a.ID < b.ID
	})
	return out
}

// NextSculpture returns the install due for the first (by room id) bedroom
// below target whose weakest stat is beauty, of the best packed sculpture
// (rankSculptures) whose footprint has a free spot there (#1191: a large
// sculpture needs a free multi-cell spot); the caller asks only once
// NextBeautyUpgrade has nothing.
func NextSculpture(obs SleepingObservation, targets map[string]RoomTarget, rooms []TidyRoom, packed []PackedSculpture) (SculptureStep, bool) {
	for _, room := range sculptureRooms(obs, targets, rooms) {
		if p, cell, rot, ok := fitSculpture(room.Room, rankSculptures(packed), nil); ok {
			return SculptureStep{Room: room.ID, Packed: p.ID, Anchor: cell, Rot: rot}, true
		}
	}
	return SculptureStep{}, false
}

// fitSculpture is the first of ranked, not in taken, with a free spot in
// room.
func fitSculpture(room TidyRoom, ranked []PackedSculpture, taken map[string]bool) (PackedSculpture, domain.Cell, domain.Rotation, bool) {
	for _, p := range ranked {
		size, ok := SculptureSize(p.Def)
		if !ok || taken[p.ID] {
			continue
		}
		if cell, rot, ok := freeSpotFacing(room, size, domain.North); ok {
			return p, cell, rot, true
		}
	}
	return PackedSculpture{}, domain.Cell{}, domain.North, false
}

// SaleSculptures is the packed art the trade selector may sell (#1194):
// every piece but those NextSculpture would install, one per owed room,
// best first.
func SaleSculptures(obs SleepingObservation, targets map[string]RoomTarget, rooms []TidyRoom, packed []PackedSculpture) map[string]bool {
	ranked := rankSculptures(packed)
	reserved := map[string]bool{}
	for _, room := range sculptureRooms(obs, targets, rooms) {
		if p, _, _, ok := fitSculpture(room.Room, ranked, reserved); ok {
			reserved[p.ID] = true
		}
	}
	out := map[string]bool{}
	for _, p := range packed {
		if !reserved[p.ID] {
			out[p.ID] = true
		}
	}
	return out
}

// beautyRooms are the target rooms (by id) below target whose weakest
// stat is beauty.
func beautyRooms(obs SleepingObservation, targets map[string]RoomTarget) []string {
	census, ok := obs.Rooms.Value()
	if !ok {
		return nil
	}
	var out []string
	for _, r := range census {
		t, tk := targets[r.ID]
		q, qk := r.Quality.Value()
		if tk && qk && !t.NeverUpgrade && t.Min > 0 && q.Impressiveness < t.Min && (t.Max <= 0 || q.Impressiveness < t.Max) && WeakestRoomStat(q) == RoomStatBeauty {
			out = append(out, r.ID)
		}
	}
	sort.Strings(out)
	return out
}

// floorUpgrade picks the most beautiful affordable floor and the room's
// cells whose terrain is less beautiful than it (none already ordered).
func floorUpgrade(id string, room FloorRoom, terrains map[string]FloorTerrain, floors FlooringFacts) (RoomUpgrade, bool) {
	names := make([]string, 0, len(floors.Definitions))
	for name := range floors.Definitions {
		names = append(names, name)
	}
	sort.Strings(names)
	best, bestBeauty := "", 0.0
	for _, name := range names {
		d := floors.Definitions[name]
		avail, ak := d.Available.Value()
		terrain, tk := d.Terrain.Value()
		beauty, bk := d.Beauty.Value()
		if !ak || !avail || !tk || !terrain || !bk || affordableCells(d, floors.Stock, 1) == 0 {
			continue
		}
		if best == "" || beauty > bestBeauty {
			best, bestBeauty = name, beauty
		}
	}
	if best == "" {
		return RoomUpgrade{}, false
	}
	var cells []domain.Cell
	for _, c := range room.Cells {
		t, ok := terrains[c.Terrain]
		if c.Pending != "" || !ok || c.Terrain == best || t.Beauty >= bestBeauty {
			continue
		}
		cells = append(cells, c.Cell)
		if len(cells) == maxBeautyFloorCells {
			break
		}
	}
	if len(cells) == 0 {
		return RoomUpgrade{}, false
	}
	return RoomUpgrade{Room: id, Slot: "floor", Def: best, Anchor: cells[0], Rot: domain.North, Cells: cells, Weakest: RoomStatBeauty}, true
}
