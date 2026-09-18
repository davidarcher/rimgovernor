package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// ExcavationCellState is the per-cell answer the review walks over,
// projected from a native site read. Cleared is open walkable ground;
// Blocked is a visible cell that is neither cleared nor natively eligible
// (a structure the fog hid, deep water, faction-owned rock, a native
// refusal) and stays standing as part of the room's walls; Rock is the
// visible mineable definition.
type ExcavationCellState struct {
	Cell     domain.Cell
	Fogged   bool
	Cleared  bool
	Blocked  bool
	Eligible bool
	Rock     string
}

// ExcavationReview is one bounded decision over a target's current cell
// states. Stage is the next cells to designate, in target order and at most
// stageLimit long: uncleared, visible, natively eligible cells a miner can
// reach through the access cell, a cleared cell or another cell of the same
// stage. Kept are the blocked cells the room is dug around. Remaining counts
// the uncleared cells that are neither kept nor stageable yet (fogged, or
// behind rock a later stage opens). Unknown means part of the remainder is
// still fogged, so the caller re-observes instead of deciding.
//
// Corridor reports whether every corridor cell (the door and the interior
// cell past it included) is cleared or still diggable; a blocked one means
// the room can no longer be entered through this target and the project
// must be re-sited.
// Complete is a target with nothing left to stage, nothing unknown and an
// open corridor: every interior cell is cleared, kept, or sealed off behind
// kept cells, and the door can close the room.
type ExcavationReview struct {
	Stage     []domain.Cell
	Kept      []domain.Cell
	Remaining int
	Unknown   bool
	Corridor  bool
	Complete  bool
}

// ReviewExcavation walks the target once and classifies every cell.
func ReviewExcavation(target ExcavationTarget, states []ExcavationCellState, stageLimit int) ExcavationReview {
	order := target.Cells()
	byCell := make(map[domain.Cell]ExcavationCellState, len(states))
	for _, s := range states {
		byCell[s.Cell] = s
	}
	open := map[domain.Cell]bool{target.Access: true}
	for _, c := range order {
		if s, ok := byCell[c]; ok && s.Cleared {
			open[c] = true
		}
	}
	adjacentOpen := func(c domain.Cell) bool {
		for _, d := range []domain.Cell{{X: 0, Z: 1}, {X: 1, Z: 0}, {X: 0, Z: -1}, {X: -1, Z: 0}} {
			if open[domain.Cell{X: c.X + d.X, Z: c.Z + d.Z}] {
				return true
			}
		}
		return false
	}
	// The entrance (the interior cell past the door) is part of the way in:
	// kept, it would leave the door facing a wall.
	corridor := map[domain.Cell]bool{{X: target.Door.X + target.Direction.X, Z: target.Door.Z + target.Direction.Z}: true}
	for _, c := range target.Corridor {
		corridor[c] = true
	}
	review := ExcavationReview{Corridor: true}
	corridorOpen := true
	for _, c := range order {
		s, ok := byCell[c]
		if !ok {
			review.Unknown = true
			review.Remaining++
			continue
		}
		if s.Cleared {
			continue
		}
		if s.Fogged {
			review.Unknown = true
			review.Remaining++
			continue
		}
		if s.Blocked || !s.Eligible {
			review.Kept = append(review.Kept, c)
			if corridor[c] {
				review.Corridor = false
			}
			continue
		}
		if corridor[c] {
			corridorOpen = false
		}
		if len(review.Stage) >= stageLimit || !adjacentOpen(c) {
			review.Remaining++
			continue
		}
		review.Stage = append(review.Stage, c)
		open[c] = true
	}
	review.Complete = review.Corridor && corridorOpen && !review.Unknown && len(review.Stage) == 0
	return review
}
