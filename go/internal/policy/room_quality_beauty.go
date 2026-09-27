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
	census, ok := obs.Rooms.Value()
	if !ok {
		return RoomUpgrade{}, false
	}
	quality := map[string]RoomQuality{}
	for _, r := range census {
		if q, ok := r.Quality.Value(); ok {
			quality[r.ID] = q
		}
	}
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
	ids := make([]string, 0, len(targets))
	for id := range targets {
		ids = append(ids, id)
	}
	sort.Strings(ids)
	for _, id := range ids {
		t := targets[id]
		q, qk := quality[id]
		room, rk := furniture[id]
		if !qk || !rk || t.NeverUpgrade || t.Min <= 0 || q.Impressiveness >= t.Min || (t.Max > 0 && q.Impressiveness >= t.Max) || WeakestRoomStat(q) != RoomStatBeauty {
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
