package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Staging the throne room (#1601, epic #1598). A colonist who holds an
// Empire title, or has the favor to claim the next one, is owed the throne
// room of the next title that asks for one (RoyalRung.Throne*): the plan
// grows a ThroneRoomSize room (GrowThroneRoom, layout_throne.go), the
// sleeping planner raises its shell, places one of the title's throne
// definitions at the template slot, and furnishes the room to the title's
// minimum impressiveness through the room quality levers (ThroneRoomTargets).
// The throne's footprint is the native catalog's, never a constant here.
//
// Assigning the throne to its holder is a seam: NextThroneStep reports
// ThroneAssign once the throne stands, and no runtime acts on it until a
// ThroneAssign action kind exists (#1601 awaits that decision), so the step
// is never owed.

// ThroneNeed is the throne room a colonist is owed: the first title above
// the holder's current one (the current one at the top) whose requirement
// names a throne and a minimum area.
type ThroneNeed struct {
	// Holder is the colonist the room is for and Title the rung it is
	// sized to.
	Holder PawnID
	Title  string
	// MinArea and MinImpressiveness are the title's minimums; the
	// impressiveness is zero when the read left it absent.
	MinArea, MinImpressiveness int
	// Things are the throne definitions the title accepts.
	Things []string
	// Assigned is whether the throne must be assigned to the holder.
	Assigned bool
}

// rungRequirement is rung's throne requirement, false for a title that asks
// for no throne or no known area.
func rungRequirement(rung RoyalRung) (ThroneNeed, bool) {
	area, ak := rung.ThroneMinArea.Value()
	if len(rung.ThroneThings) == 0 || !ak || area <= 0 {
		return ThroneNeed{}, false
	}
	impressiveness, _ := rung.ThroneMinImpressiveness.Value()
	assigned, _ := rung.ThroneAssigned.Value()
	return ThroneNeed{Title: rung.Title, MinArea: area, MinImpressiveness: max(impressiveness, 0), Things: rung.ThroneThings, Assigned: assigned}, true
}

// NextThroneNeed is the largest throne room any colonist is owed, false
// when none holds or can claim a title that asks for one. A colonist holds
// a title or can claim the next rung when its favor reaches the rung's
// FavorNeeded; the room sized is the first rung above its current title
// with a throne requirement, else the current title's own.
func NextThroneNeed(f RoyaltyFacts) (ThroneNeed, bool) {
	index := map[string]int{}
	for i, rung := range f.Ladder {
		index[rung.Title] = i
	}
	holders := make([]PawnID, 0, len(f.Holders))
	for id := range f.Holders {
		holders = append(holders, id)
	}
	sort.Slice(holders, func(i, j int) bool { return holders[i] < holders[j] })
	var best ThroneNeed
	found := false
	for _, id := range holders {
		for _, h := range f.Holders[id] {
			current := -1
			if h.Title != "" {
				i, ok := index[h.Title]
				if !ok {
					continue
				}
				current = i
			} else {
				// No title yet: owed only once the first rung is within
				// reach of the favor held.
				favor, fk := h.Favor.Value()
				if len(f.Ladder) == 0 {
					continue
				}
				needed, nk := f.Ladder[0].FavorNeeded.Value()
				if !fk || !nk || favor < needed {
					continue
				}
			}
			need, ok := ThroneNeed{}, false
			for i := current + 1; i < len(f.Ladder) && !ok; i++ {
				need, ok = rungRequirement(f.Ladder[i])
			}
			if !ok && current >= 0 {
				need, ok = rungRequirement(f.Ladder[current])
			}
			if !ok {
				continue
			}
			need.Holder = id
			if !found || need.MinArea > best.MinArea || need.MinArea == best.MinArea && need.MinImpressiveness > best.MinImpressiveness {
				best, found = need, true
			}
		}
	}
	return best, found
}

// ThroneDefinition is a throne definition as the native catalog describes
// it.
type ThroneDefinition struct {
	Name      string
	Available domain.Fact[bool]
	Size      domain.Fact[Bounds]
}

// ThroneStepKind is the next throne step.
type ThroneStepKind string

const (
	// ThroneNone: nothing is due, or a fact is unknown.
	ThroneNone ThroneStepKind = ""
	// ThroneShell: raise the walls and door of Room.
	ThroneShell ThroneStepKind = "shell"
	// ThronePlace: place Piece, a throne, in Room.
	ThronePlace ThroneStepKind = "place"
	// ThroneAssign: the throne stands; assign it to Need.Holder. This is
	// the assignment seam: no action kind carries it yet, so the step is
	// reported but never owed (ThroneStepOwed).
	ThroneAssign ThroneStepKind = "assign"
)

// ThroneStep is one bounded step towards the title's throne room.
type ThroneStep struct {
	Kind  ThroneStepKind
	Room  LayoutRoom
	Piece InteriorPiece
	Need  ThroneNeed
}

// Owed reports whether the planner can act on the step now; the assignment
// seam is not owed.
func (s ThroneStep) Owed() bool {
	return s.Kind == ThroneShell || s.Kind == ThronePlace
}

// throneDefinition is the first of need's throne definitions the catalog
// makes available with a known footprint.
func throneDefinition(need ThroneNeed, defs []ThroneDefinition) (InteriorPieceDef, bool) {
	for _, thing := range need.Things {
		for _, d := range defs {
			if d.Name != thing {
				continue
			}
			available, ak := d.Available.Value()
			size, sk := d.Size.Value()
			if ak && available && sk && size.Width > 0 && size.Height > 0 {
				return InteriorPieceDef{Def: d.Name, Size: domain.Cell{X: size.Width, Z: size.Height}}, true
			}
		}
	}
	return InteriorPieceDef{}, false
}

// throneStands is the standing throne of need inside r's interior.
func throneStands(r LayoutRoom, need ThroneNeed, built []CurrentBuilding) bool {
	for _, b := range built {
		if len(b.Cells) == 0 || !rectInside(r.Interior, cellsRectangle(b.Cells)) {
			continue
		}
		for _, thing := range need.Things {
			if b.Building.Definition() == thing {
				return true
			}
		}
	}
	return false
}

// NextThroneStep picks the next throne step for need from the plan, the
// room census, the colony's buildings and the throne definitions. None
// while the plan holds no room of the title's area (the layout review owes
// it) or no throne definition is available with a known size.
func NextThroneStep(plan LayoutPlan, rooms RoomObservation, built []CurrentBuilding, need ThroneNeed, defs []ThroneDefinition) ThroneStep {
	room, ok := plan.ThroneRoomFor(need.MinArea)
	if !ok {
		return ThroneStep{}
	}
	step := ThroneStep{Room: room, Need: need}
	if _, ok := PlannedRoomStanding(room, rooms); !ok {
		step.Kind = ThroneShell
		return step
	}
	if !throneStands(room, need, built) {
		def, ok := throneDefinition(need, defs)
		in, rok := InteriorRoomFromLayout(room)
		if !ok || !rok {
			return ThroneStep{}
		}
		interior, ok := PlanInterior(in, def)
		if !ok {
			return ThroneStep{}
		}
		taken := map[domain.Cell]bool{}
		for _, b := range built {
			for _, c := range b.Cells {
				taken[c] = true
			}
		}
		for _, p := range interior.Pieces {
			if p.Slot != throneSlot || p.Def != def.Def {
				continue
			}
			for _, c := range rectCells(p.Rect) {
				if taken[c] {
					return ThroneStep{}
				}
			}
			step.Kind, step.Piece = ThronePlace, p
			return step
		}
		return ThroneStep{}
	}
	if need.Assigned {
		step.Kind = ThroneAssign
	}
	return step
}

// ThroneRoomTargets is the impressiveness target of the standing throne
// room, keyed by census room id, for the room quality levers (the room
// upgrade and the beauty upgrade): the title's minimum, reason "title".
// Empty when the plan holds no standing room of the title's area or the
// title asks for no impressiveness.
func ThroneRoomTargets(plan LayoutPlan, rooms RoomObservation, need ThroneNeed) map[string]RoomTarget {
	room, ok := plan.ThroneRoomFor(need.MinArea)
	if !ok || need.MinImpressiveness <= 0 {
		return nil
	}
	standing, ok := PlannedRoomStanding(room, rooms)
	if !ok {
		return nil
	}
	return map[string]RoomTarget{standing.ID: {Room: standing.ID, Min: float64(need.MinImpressiveness), Reasons: []string{"title"}}}
}

// ThroneAreaOwed is the room area the plan must grow for need, zero when it
// already holds one (or nobody is owed a throne).
func ThroneAreaOwed(plan LayoutPlan, need ThroneNeed, owed bool) int {
	if !owed {
		return 0
	}
	if _, ok := plan.ThroneRoomFor(need.MinArea); ok {
		return 0
	}
	return need.MinArea
}
