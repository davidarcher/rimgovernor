package policy

import (
	"errors"
	"fmt"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// CleanlinessPolicy bounds MaintainCleanFacilities' direct cleaning response
// (issue #6 slice 2). RimWorld's room Cleanliness stat is 0 for a clean
// room and falls with filth; the game's food-poisoning and infection chances
// read that same stat, so the thresholds are in its units.
type CleanlinessPolicy struct {
	// EnterC latches a room dirty; ExitC releases it (hysteresis).
	EnterC, ExitC float64
	// GraceTicks is how long a room may stay dirty while colonists with
	// Cleaning enabled exist before the controller issues direct orders:
	// ordinary work coverage gets first chance. With no cleaner at all the
	// response is immediate.
	GraceTicks domain.Tick
	// MaxTargets bounds the filth one review may target across all rooms.
	MaxTargets int
}

func DefaultCleanlinessPolicy() CleanlinessPolicy {
	return CleanlinessPolicy{EnterC: -1, ExitC: -0.25, GraceTicks: 30000, MaxTargets: 8}
}

func (p CleanlinessPolicy) valid() bool {
	finite := func(n float64) bool { return !math.IsNaN(n) && !math.IsInf(n, 0) }
	return finite(p.EnterC) && finite(p.ExitC) && p.EnterC < p.ExitC && p.GraceTicks >= 0 && p.GraceTicks <= 600000 && p.MaxTargets >= 1 && p.MaxTargets <= 64
}

// DirtyRoom is one latched dirty room and the review tick it was first
// latched at; persisted in UpkeepHistory so the grace period survives
// restarts and never restarts on every review. Key is RoomLatchKey(room),
// not the native room ID: RimWorld renumbers rooms on every region rebuild
// (any nearby construction), which would otherwise reset the grace period.
type DirtyRoom struct {
	Key   string
	Since domain.Tick
}

// RoomLatchKey identifies a room across native renumbering by its
// lowest-sorted cell ("x,z"); a room without cells falls back to its ID.
// A room that merges or splits changes key and starts a fresh latch, which
// is right: it is a different room.
func RoomLatchKey(room Room) string {
	if len(room.Cells) == 0 {
		return room.ID
	}
	low := room.Cells[0]
	for _, c := range room.Cells[1:] {
		if cellLess(c, low) {
			low = c
		}
	}
	return fmt.Sprintf("%d,%d", low.X, low.Z)
}

// cookingDefinitions and butcherDefinitions name the vanilla food-processing
// benches the separation rule reasons about. Butchery generates blood filth
// (and corpse rot) wherever it runs, so a room holding one is inherently
// dirty; a room holding a cooking bench is a workspace whose cleanliness the
// game reads for food poisoning.
var (
	cookingDefinitions = map[Resource]bool{"Campfire": true, "FueledStove": true, "ElectricStove": true}
	butcherDefinitions = map[Resource]bool{"ButcherSpot": true, "TableButcher": true}
)

func roomHolds(room Room, set map[Resource]bool) bool {
	contents, known := room.Contents.Value()
	if !known {
		return false
	}
	for _, amount := range contents {
		if set[amount.Resource] && amount.Count > 0 {
			return true
		}
	}
	return false
}

// CleanWorkspace reports whether room is one the controller keeps clean:
// an enclosed kitchen, hospital or laboratory, or any enclosed room holding
// a cooking bench (a "Room" role kitchen with a campfire counts). Rooms that
// are inherently dirty -- barns, and any room holding a butcher bench -- are
// never workspaces, whatever else they contain: their filth is the cost of
// the work done there, not a coverage failure.
func CleanWorkspace(room Room) bool {
	enclosed, ek := room.Enclosed.Value()
	if !ek || !enclosed || InherentlyDirty(room) {
		return false
	}
	if role, known := room.Role.Value(); known && (role == RoomRoleKitchen || role == RoomRoleHospital || role == RoomRoleLaboratory) {
		return true
	}
	return roomHolds(room, cookingDefinitions)
}

// InherentlyDirty reports a room whose use produces filth as a matter of
// course (barn, butchery). Cleaning it is ordinary colonist work at most,
// never a controller intervention.
func InherentlyDirty(room Room) bool {
	if role, known := room.Role.Value(); known && role == RoomRoleBarn {
		return true
	}
	return roomHolds(room, butcherDefinitions)
}

// CleanlinessReview is the bounded cleaning response for one review.
type CleanlinessReview struct {
	// DirtyRooms is the new latch set: every workspace currently latched
	// dirty, with the tick it entered. Unknown room facts preserve the
	// previous latch for that room rather than releasing it.
	DirtyRooms []DirtyRoom
	// Targets is the filth the controller may order cleaned this review,
	// in dispatch order; unknown when the room or filth census is unknown.
	Targets domain.Fact[[]UpkeepFilth]
	// Metric is the summed thickness of Targets when known.
	Metric domain.Fact[float64]
}

// ReviewCleanliness latches workspaces dirty by measured room cleanliness
// and targets their filth only once ordinary work coverage has had its
// chance: after GraceTicks with cleaners available (an unknown cleaner
// count is treated as coverage present), or at once with none. A target
// room's filth is the set its Cleanliness stat sums: the filth the census
// places in the room plus home-area filth on a cell touching one of the
// room's cells (8-way), which is how RimWorld registers a doorway's filth
// in the regions on both sides of it. Without that a room whose only
// remaining filth sits in its doorway would stay latched with nothing to
// target (#324). Other filth -- other rooms, outdoors, inherently dirty
// rooms -- is never a target. A known empty filth census is a known empty
// target set whatever the room census says; a known non-empty one needs
// the room census to decide.
func ReviewCleanliness(rooms domain.Fact[RoomObservation], filth domain.Fact[[]UpkeepFilth], cleaners domain.Fact[int], previous []DirtyRoom, tick domain.Tick, p CleanlinessPolicy) (CleanlinessReview, error) {
	if !p.valid() {
		return CleanlinessReview{}, errors.New("invalid cleanliness policy")
	}
	if tick < 0 || len(previous) > 256 {
		return CleanlinessReview{}, errors.New("invalid cleanliness review input")
	}
	prior := map[string]domain.Tick{}
	for _, room := range previous {
		if room.Key == "" || room.Since < 0 || room.Since > tick {
			return CleanlinessReview{}, errors.New("invalid dirty room latch")
		}
		if _, dup := prior[room.Key]; dup {
			return CleanlinessReview{}, errors.New("duplicate dirty room latch")
		}
		prior[room.Key] = room.Since
	}
	rows, filthKnown := filth.Value()
	if filthKnown && len(rows) > 256 {
		return CleanlinessReview{}, errors.New("invalid cleanliness filth census")
	}
	census, roomsKnown := rooms.Value()
	if !roomsKnown {
		// No census: nothing can enter or leave the latch set.
		r := CleanlinessReview{DirtyRooms: append([]DirtyRoom(nil), previous...), Targets: domain.Unknown[[]UpkeepFilth](), Metric: domain.Unknown[float64]()}
		if filthKnown && len(rows) == 0 {
			r.Targets, r.Metric = domain.Known([]UpkeepFilth{}), domain.Known(0.0)
		}
		return r, nil
	}
	if len(census.Rooms) > 4096 {
		return CleanlinessReview{}, errors.New("room census exceeds bound")
	}
	r := CleanlinessReview{}
	// dirty and cleanliness are keyed by latch key; keyOf maps the native
	// room ID the filth census names to that key.
	dirty := map[string]domain.Tick{}
	cleanliness := map[string]float64{}
	keyOf := map[string]string{}
	seen := map[string]bool{}
	for _, room := range census.Rooms {
		key := RoomLatchKey(room)
		if room.ID == "" || seen[room.ID] || key == "" {
			return CleanlinessReview{}, errors.New("invalid room census identity")
		}
		seen[room.ID] = true
		if !CleanWorkspace(room) {
			continue
		}
		if _, dup := dirty[key]; dup {
			return CleanlinessReview{}, errors.New("room census cells overlap")
		}
		keyOf[room.ID] = key
		value, known := room.Cleanliness.Value()
		since, wasDirty := prior[key]
		switch {
		case !known:
			// Unknown stat: keep whatever the latch said.
			if wasDirty {
				dirty[key] = since
			}
		case wasDirty && value < p.ExitC:
			dirty[key] = since
			cleanliness[key] = value
		case !wasDirty && value < p.EnterC:
			dirty[key] = tick
			cleanliness[key] = value
		}
	}
	// A room that stopped being a workspace (role changed, butcher moved in,
	// wall opened) or vanished from the census releases its latch.
	for key, since := range dirty {
		r.DirtyRooms = append(r.DirtyRooms, DirtyRoom{Key: key, Since: since})
	}
	sort.Slice(r.DirtyRooms, func(i, j int) bool { return r.DirtyRooms[i].Key < r.DirtyRooms[j].Key })
	if !filthKnown {
		r.Targets, r.Metric = domain.Unknown[[]UpkeepFilth](), domain.Unknown[float64]()
		return r, nil
	}
	count, cleanersKnown := cleaners.Value()
	if cleanersKnown && count < 0 {
		return CleanlinessReview{}, errors.New("invalid cleaner count")
	}
	targetRooms := map[string]bool{}
	for key, since := range dirty {
		if _, measured := cleanliness[key]; !measured {
			continue
		}
		if cleanersKnown && count == 0 || tick-since >= p.GraceTicks {
			targetRooms[key] = true
		}
	}
	// cellKey maps every target room cell to its latch key so a filth
	// touching the room from outside (a doorway) attributes to it.
	cellKey := map[domain.Cell]string{}
	for _, room := range census.Rooms {
		if key := keyOf[room.ID]; targetRooms[key] {
			for _, c := range room.Cells {
				cellKey[c] = key
			}
		}
	}
	selected := []UpkeepFilth{}
	attributed := map[string]string{}
	for _, row := range rows {
		if !row.Home {
			continue
		}
		key := ""
		if id, known := row.RoomID.Value(); known && targetRooms[keyOf[id]] {
			key = keyOf[id]
		} else if len(cellKey) > 0 {
			key = touchingKey(cellKey, row.Cell)
		}
		if key == "" {
			continue
		}
		selected = append(selected, row)
		attributed[row.ID] = key
	}
	sort.Slice(selected, func(i, j int) bool {
		a, b := selected[i], selected[j]
		ka, kb := attributed[a.ID], attributed[b.ID]
		if ka != kb {
			// Dirtiest room first, then stable room order.
			if cleanliness[ka] != cleanliness[kb] {
				return cleanliness[ka] < cleanliness[kb]
			}
			return ka < kb
		}
		return a.ID < b.ID
	})
	if len(selected) > p.MaxTargets {
		selected = selected[:p.MaxTargets]
	}
	total := 0.0
	for _, row := range selected {
		total += float64(row.Thickness)
	}
	r.Targets, r.Metric = domain.Known(selected), domain.Known(total)
	return r, nil
}

// touchingKey returns the latch key of a room whose cell is at or 8-way
// adjacent to cell, the lowest key when several touch, or "" for none.
func touchingKey(cellKey map[domain.Cell]string, cell domain.Cell) string {
	best := ""
	for dx := int32(-1); dx <= 1; dx++ {
		for dz := int32(-1); dz <= 1; dz++ {
			key, ok := cellKey[domain.Cell{X: cell.X + dx, Z: cell.Z + dz}]
			if ok && (best == "" || key < best) {
				best = key
			}
		}
	}
	return best
}

// SeparationRoom is one room holding both a cooking bench and a butcher
// bench: butchery filth lands in the workspace the game reads for food
// poisoning.
type SeparationRoom struct {
	ID    string
	Cells []domain.Cell
}

// KitchenSeparation lists rooms where cooking and butchery share a room,
// with their cells so placement can keep new facilities out. Unknown when
// the room census is unknown.
func KitchenSeparation(rooms domain.Fact[RoomObservation]) domain.Fact[[]SeparationRoom] {
	census, known := rooms.Value()
	if !known {
		return domain.Unknown[[]SeparationRoom]()
	}
	result := []SeparationRoom{}
	for _, room := range census.Rooms {
		if roomHolds(room, cookingDefinitions) && roomHolds(room, butcherDefinitions) {
			result = append(result, SeparationRoom{ID: room.ID, Cells: append([]domain.Cell(nil), room.Cells...)})
		}
	}
	sort.Slice(result, func(i, j int) bool { return result[i].ID < result[j].ID })
	return domain.Known(result)
}

// SeparationProtectedCells returns every cell of every room holding the
// benches in set (cooking rooms for a butcher placement, butcher rooms for
// a cooking placement), so a placement search never proposes a site that
// would co-locate the two. Unknown room facts protect nothing: the
// placement's own native preview still owns legality.
func SeparationProtectedCells(rooms domain.Fact[RoomObservation], butcherPlacement bool) []domain.Cell {
	census, known := rooms.Value()
	if !known {
		return nil
	}
	set := butcherDefinitions
	if butcherPlacement {
		set = cookingDefinitions
	}
	var cells []domain.Cell
	for _, room := range census.Rooms {
		if roomHolds(room, set) {
			cells = append(cells, room.Cells...)
		}
	}
	sort.Slice(cells, func(i, j int) bool { return cellLess(cells[i], cells[j]) })
	return cells
}
