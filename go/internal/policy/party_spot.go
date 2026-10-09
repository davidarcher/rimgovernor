package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PartySpotDefinition is the free, instant-build spot the game's gathering
// worker picks: RCellFinder.TryFindGatheringSpot takes a built colony building
// whose def is in GatheringDef.gatherSpotDefs (Core Party lists PartySpot) at a
// cell that passes GatheringsUtility.ValidateGatheringSpot (standable, safe,
// roofed, reservable). EnsureComfort keeps one in the best shared room.
const PartySpotDefinition = "PartySpot"

// PartySpotInput is what the PartySpot review reads.
type PartySpotInput struct {
	Rooms     domain.Fact[RoomObservation]
	Qualities domain.Fact[[]UpkeepRoom]
	Levels    ImpressivenessLevels
	Census    domain.Fact[CurrentConstruction]
	Cells     []SiteCell
	// Placeable is whether a PartySpot can be raised at all: the definition is
	// available and a colonist can build it.
	Placeable bool
	// Spent reports whether this Episode already spent the one-shot for the
	// building to retire (retire true) or for the room to place in (room "" is
	// anywhere). A spent one-shot drops the deficit instead of holding it.
	Spent func(retire bool, id string) bool
}

// PartySpotNeed is the one step the colony owes its PartySpot; the zero value
// owes none.
type PartySpotNeed struct {
	// Retire is the standing spot to deconstruct: a duplicate, or the spot of
	// a room a strictly higher impressiveness stage outranks.
	Retire    CurrentBuilding
	HasRetire bool
	// Place owes a spot, in Room's Cells (Room "" places in any roofed indoor
	// cell, Cells nil).
	Place bool
	Room  string
	Cells []domain.Cell
}

// Owed reports whether any step is owed.
func (n PartySpotNeed) Owed() bool { return n.HasRetire || n.Place }

// ReviewPartySpot decides the PartySpot step. The best shared room is the
// dining or rec room of the highest observed impressiveness stage, ties broken
// by cell count. The spot moves only to a room of a strictly higher stage than
// the incumbent's room, read from the observed spot; same-stage rooms never
// move it. Duplicates are retired. Unknown rooms, qualities or census leave
// the need unknown.
func ReviewPartySpot(in PartySpotInput) domain.Fact[PartySpotNeed] {
	rooms, rk := in.Rooms.Value()
	qualities, qk := in.Qualities.Value()
	census, ck := in.Census.Value()
	if !rk || !qk || !ck || !census.Colony {
		return domain.Unknown[PartySpotNeed]()
	}
	for _, site := range census.Sites {
		if site.Building.Definition() == PartySpotDefinition {
			// A blueprint or frame is the step in flight.
			return domain.Known(PartySpotNeed{})
		}
	}
	quality := map[string]RoomQuality{}
	for _, q := range qualities {
		if v, known := q.Quality.Value(); known {
			quality[q.ID] = v
		}
	}
	free := map[domain.Cell]bool{}
	for _, c := range in.Cells {
		roofed, _ := c.Roofed.Value()
		indoors, _ := c.Indoors.Value()
		if roofed && indoors && !c.Occupied() {
			free[c.Cell] = true
		}
	}
	type candidate struct {
		id    string
		stage int
		size  int
		cells []domain.Cell
	}
	stages := map[string]int{}
	var candidates []candidate
	for _, room := range rooms.Rooms {
		role, known := room.Role.Value()
		q, scored := quality[room.ID]
		if !known || role != RoomRoleDiningRoom && role != RoomRoleRecRoom || !scored {
			continue
		}
		stages[room.ID] = in.Levels.Stage(q.Impressiveness)
		var cells []domain.Cell
		for _, c := range room.Cells {
			if free[c] {
				cells = append(cells, c)
			}
		}
		candidates = append(candidates, candidate{room.ID, stages[room.ID], len(room.Cells), cells})
	}
	// The best room with a free cell: highest stage, then the larger room.
	var best *candidate
	for i := range candidates {
		c := &candidates[i]
		if len(c.cells) == 0 {
			continue
		}
		if best == nil || c.stage > best.stage || c.stage == best.stage && (c.size > best.size || c.size == best.size && c.id < best.id) {
			best = c
		}
	}
	var spots []CurrentBuilding
	for _, b := range census.Buildings {
		if b.Building.Definition() == PartySpotDefinition && len(b.Cells) > 0 {
			spots = append(spots, b)
		}
	}
	// stageOf is the stage of the room holding a standing spot: -1 outside
	// every shared room; not scored (false) in a shared room with no quality
	// read, which never moves the spot.
	stageOf := func(b CurrentBuilding) (int, bool) {
		for _, room := range rooms.Rooms {
			if slices.Contains(room.Cells, b.Cells[0]) {
				role, known := room.Role.Value()
				if !known || role != RoomRoleDiningRoom && role != RoomRoleRecRoom {
					return -1, true
				}
				stage, scored := stages[room.ID]
				return stage, scored
			}
		}
		return -1, true
	}
	spent := func(retire bool, id string) bool { return in.Spent != nil && in.Spent(retire, id) }
	if len(spots) > 1 {
		// Keep the spot in the highest stage room (then the lowest ID); the
		// rest are duplicates, each retired once.
		keep := 0
		for i, b := range spots {
			si, _ := stageOf(b)
			sk, _ := stageOf(spots[keep])
			if si > sk || si == sk && b.ID < spots[keep].ID {
				keep = i
			}
		}
		for i, b := range spots {
			if i != keep && !spent(true, b.ID) {
				return domain.Known(PartySpotNeed{Retire: b, HasRetire: true})
			}
		}
		return domain.Known(PartySpotNeed{})
	}
	if len(spots) == 1 {
		incumbent, scored := stageOf(spots[0])
		if scored && best != nil && best.stage > incumbent && !spent(true, spots[0].ID) {
			return domain.Known(PartySpotNeed{Retire: spots[0], HasRetire: true})
		}
		return domain.Known(PartySpotNeed{})
	}
	if !in.Placeable {
		return domain.Known(PartySpotNeed{})
	}
	need := PartySpotNeed{Place: true}
	if best != nil {
		need.Room, need.Cells = best.id, best.cells
	} else if len(free) == 0 {
		return domain.Known(PartySpotNeed{})
	}
	if spent(false, need.Room) {
		return domain.Known(PartySpotNeed{})
	}
	return domain.Known(need)
}
