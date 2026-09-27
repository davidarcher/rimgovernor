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

// The sculpture lever (#830), after pots and floors: one small sculpture
// bill at an art bench, then the finished packed sculpture installed on
// free floor in the room.
const (
	SculptureDefinition       = "SculptureSmall"
	SculptureRecipe           = "Make_SculptureSmall"
	PackedSculptureDefinition = "MinifiedSculpture"
)

type SculptureKind string

const (
	SculptureBill    SculptureKind = "bill"
	SculptureInstall SculptureKind = "install"
)

// SculptureStep is one change: a bill on Bench, or Packed (a packed item's
// id) installed at Anchor, North.
type SculptureStep struct {
	Kind   SculptureKind
	Room   string
	Bench  string
	Packed string
	Anchor domain.Cell
}

// NextSculpture returns the sculpture step due for the first (by room id)
// bedroom below target whose weakest stat is beauty and that has a free
// cell; the caller asks only once NextBeautyUpgrade has nothing. A packed
// sculpture in stock goes first; otherwise one bill, none while any bench
// already carries one.
func NextSculpture(obs SleepingObservation, targets map[string]RoomTarget, rooms []TidyRoom, benches []GearBench, packed []string) (SculptureStep, bool) {
	for _, id := range beautyRooms(obs, targets) {
		for _, room := range rooms {
			if room.ID != id {
				continue
			}
			cell, _, ok := freeSpot(room, domain.Cell{X: 1, Z: 1})
			if !ok {
				continue
			}
			if len(packed) > 0 {
				return SculptureStep{Kind: SculptureInstall, Room: id, Packed: packed[0], Anchor: cell}, true
			}
			for _, b := range benches {
				bills, _ := b.Bills.Value()
				for _, bill := range bills {
					if bill.Recipe == SculptureRecipe {
						return SculptureStep{}, false
					}
				}
			}
			for _, b := range benches {
				recipes, _ := b.Recipes.Value()
				for _, r := range recipes {
					if avail, ok := r.Available.Value(); r.Definition == SculptureRecipe && ok && avail {
						return SculptureStep{Kind: SculptureBill, Room: id, Bench: b.ID}, true
					}
				}
			}
			return SculptureStep{}, false
		}
	}
	return SculptureStep{}, false
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
