package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainShelter is the standing goal that keeps the colony's shelter
// settings current (#1294): the bot-owned "Safe" allowed area (#1325), and
// later sheltering pawns in it (#1326) and the killbox restriction (#1327).
const MaintainShelter GoalID = "MaintainShelter"

// SafeAreaKey is the bot area key of the Safe allowed area.
const SafeAreaKey = "Safe"

// SafeAreaCells is the union of the enclosed, fully roofed rooms (every
// role: workshops keep sheltered pawns working) minus the killbox cells;
// a room with an enemy-facing door is left out unless every roofed room
// has one: a roof with a door toward the killbox still shelters from
// fallout, weather and a pack better than no Safe area at all.
// Sorted, deduplicated.
func SafeAreaCells(rooms RoomObservation, killbox []domain.Cell) []domain.Cell {
	excluded := map[domain.Cell]bool{}
	for _, c := range killbox {
		excluded[c] = true
	}
	inner, exposed := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, room := range rooms.Rooms {
		if enclosed, known := room.Enclosed.Value(); !known || !enclosed {
			continue
		}
		if roofed, known := room.Roofed.Value(); !known || !roofed {
			continue
		}
		set := inner
		for _, d := range room.Doors {
			if d.EnemyFacing {
				set = exposed
			}
		}
		for _, c := range room.Cells {
			if !excluded[c] {
				set[c] = true
			}
		}
	}
	if len(inner) == 0 {
		return sortedCells(exposed)
	}
	return sortedCells(inner)
}

// PlanSafeArea returns the AreaIntent edits that bring the Safe area from
// current to the planned cells, and the planned cells. With current
// unknown (process start or reload) it resets: delete, then create with
// the full set. Otherwise it sends only real diffs (set_cells for added
// cells, clear_cells for removed ones); none when the area is stable.
func PlanSafeArea(rooms RoomObservation, killbox []domain.Cell, current []domain.Cell, currentKnown bool) ([]domain.Area, []domain.Cell, error) {
	want := SafeAreaCells(rooms, killbox)
	edits, err := PlanBotArea(SafeAreaKey, want, current, currentKnown)
	return edits, want, err
}

// PlanBotArea returns the AreaIntent edits that bring the bot area key from
// current to want: with current unknown a reset (delete, then create with
// want), otherwise only real diffs (set_cells, clear_cells).
func PlanBotArea(key string, want, current []domain.Cell, currentKnown bool) ([]domain.Area, error) {
	if !currentKnown {
		del, err := domain.NewArea(domain.AreaDelete, key, nil)
		if err != nil {
			return nil, err
		}
		create, err := domain.NewArea(domain.AreaCreate, key, want)
		if err != nil {
			return nil, err
		}
		return []domain.Area{del, create}, nil
	}
	have := map[domain.Cell]bool{}
	for _, c := range current {
		have[c] = true
	}
	wanted := map[domain.Cell]bool{}
	var added []domain.Cell
	for _, c := range want {
		wanted[c] = true
		if !have[c] {
			added = append(added, c)
		}
	}
	removed := map[domain.Cell]bool{}
	for c := range have {
		if !wanted[c] {
			removed[c] = true
		}
	}
	var out []domain.Area
	if len(added) > 0 {
		a, err := domain.NewArea(domain.AreaSetCells, key, added)
		if err != nil {
			return nil, err
		}
		out = append(out, a)
	}
	if len(removed) > 0 {
		a, err := domain.NewArea(domain.AreaClearCells, key, sortedCells(removed))
		if err != nil {
			return nil, err
		}
		out = append(out, a)
	}
	return out, nil
}
