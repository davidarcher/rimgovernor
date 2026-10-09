package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Containment upkeep: keep a held entity's cell closed
// and its captive tended. The rules are the game's own, read from the
// Building_Door and StatWorker_ContainmentStrength decompile:
//
//   - A door is ContainmentBreached once it has stood open 600 ticks
//     (Building_Door.TicksOpenToBreach); the strength worker then zeroes the
//     whole door term of the room (the margin the capture rule keeps,
//     CaptureMargin). A door stays open past its close delay only when it is
//     held open (Building_Door.HoldOpen, the player toggle that the combat
//     door potshot also sets) or something stands in the doorway
//     (BlockedOpenMomentary). The only write that closes a door is the
//     combat door CLOSE order, which clears the hold; a blocked doorway is
//     not something an order can clear, so it is reported, never ordered.
//   - A held pawn with a hediff that needs tending is tended like any
//     patient; a bleeding one bleeds out otherwise.
//
// Only a holder that holds a pawn is kept: an empty platform has nothing to
// breach. An unread fact is a loud issue, never a pass.

// UpkeepIssue is a containment upkeep fact the colony cannot act on: Reason
// says in plain English what is unread or cannot be ordered.
type UpkeepIssue struct {
	// Cell is the door the issue is about, or the zero cell for a holder
	// level issue.
	Cell   domain.Cell
	Reason string
}

// ContainmentUpkeep is what the colony owes its held entities now: doors to
// close and issues to report. Closes are by cell, sorted.
type ContainmentUpkeep struct {
	CloseDoors []domain.Cell
	Issues     []UpkeepIssue
}

// ContainmentDoorUpkeep reads every holder that holds a pawn. A door held
// open is closed; a door that is breached but is not held open and is
// blocked open is an issue (an order cannot clear what stands in it); an
// unread door fact is an issue. A door open and not held open closes by
// itself and is left alone.
func ContainmentDoorUpkeep(p ContainmentPlanning) ContainmentUpkeep {
	var out ContainmentUpkeep
	holders, ok := p.Holders.Value()
	if !ok {
		return out
	}
	seen := map[domain.Cell]bool{}
	for _, h := range holders {
		if h.HeldPawn == "" {
			continue
		}
		doors, known := h.Doors.Value()
		if !known {
			out.Issues = append(out.Issues, UpkeepIssue{Reason: fmt.Sprintf("the doors of the cell holding %s are unread", h.HeldPawn)})
			continue
		}
		for _, d := range doors {
			if seen[d.Cell] {
				continue
			}
			seen[d.Cell] = true
			hold, hk := d.HoldOpen.Value()
			breached, bk := d.Breached.Value()
			blocked, ek := d.BlockedOpen.Value()
			switch {
			case !hk:
				out.Issues = append(out.Issues, UpkeepIssue{Cell: d.Cell, Reason: "whether the cell door is held open is unread"})
			case hold:
				out.CloseDoors = append(out.CloseDoors, d.Cell)
			case !bk:
				out.Issues = append(out.Issues, UpkeepIssue{Cell: d.Cell, Reason: "whether the cell door is breached is unread"})
			case breached && !ek:
				out.Issues = append(out.Issues, UpkeepIssue{Cell: d.Cell, Reason: "the cell door is breached and whether it is blocked open is unread"})
			case breached && blocked:
				out.Issues = append(out.Issues, UpkeepIssue{Cell: d.Cell, Reason: "the cell door is breached and blocked open by a thing in the doorway; no order clears that"})
			}
		}
	}
	sort.Slice(out.CloseDoors, func(i, j int) bool {
		a, b := out.CloseDoors[i], out.CloseDoors[j]
		return a.Z < b.Z || a.Z == b.Z && a.X < b.X
	})
	return out
}

// entityDoorOwed is whether a held entity's cell door is to be closed: a
// standing work for MaintainPopulation, whose custody step carries it.
func entityDoorOwed(p ContainmentPlanning) bool {
	return len(ContainmentDoorUpkeep(p).CloseDoors) > 0
}

// ContainmentDoorTarget is the first cell door to close, by position.
func ContainmentDoorTarget(p ContainmentPlanning) (domain.Cell, bool) {
	doors := ContainmentDoorUpkeep(p).CloseDoors
	if len(doors) == 0 {
		return domain.Cell{}, false
	}
	return doors[0], true
}

// EntityTendTarget is the held entity to tend next: one that is alive, held
// and read as needing tend, a bleeding one first, then by pawn ID. An entity
// whose health facts are unread is skipped; a held entity is only ever read
// as one, so its absence of facts is not a pass but also not an order the
// colony can give.
func EntityTendTarget(p ContainmentPlanning) (domain.PawnID, bool) {
	entities, ok := p.Entities.Value()
	if !ok {
		return "", false
	}
	var best domain.PawnID
	bestBleeding := false
	for _, e := range entities {
		dead, dk := e.Dead.Value()
		held, hk := e.Held.Value()
		needs, nk := e.NeedsTend.Value()
		downed, wk := e.Downed.Value()
		if !dk || dead || !hk || !held || !nk || !needs {
			continue
		}
		if !wk || !downed {
			// SelectTend tends a patient only where they lie (downed or in a
			// bed, tendable), and an entity is in no bed: a standing entity
			// is no deficit the colony can work, so none is owed.
			continue
		}
		bleeding, _ := e.Bleeding.Value()
		if best == "" || bleeding && !bestBleeding || bleeding == bestBleeding && e.Pawn < best {
			best, bestBleeding = e.Pawn, bleeding
		}
	}
	return best, best != ""
}

// entityTendOwed is whether a held entity needs tending.
func entityTendOwed(p ContainmentPlanning) bool {
	_, ok := EntityTendTarget(p)
	return ok
}
