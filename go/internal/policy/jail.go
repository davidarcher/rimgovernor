package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Furnishing the jail. While a prisoner is held, MaintainPopulation
// keeps one bed set for prisoners in a planned jail per held prisoner:
// it sets every unflagged bed standing in a jail for prisoners at once (owner
// type is room-level in the game, so a jail is never left mixed), reports
// the crib and slave beds as strays (native refuses a crib; a slave bed is
// not the jail's to flip), and
// otherwise reconciles the room (ReconcileRoom) to the next free template
// bed: the ring, the floor and the bed, installed from packed stock first. A
// fresh bed is placed plain and flagged on a later step once it stands.
// Wardens bring the prisoners to the beds; nothing here hauls.

func init() {
	RegisterInteriorTemplate(RoomRolePrisonCell, InteriorTemplate{Name: "jail", Plan: planJail})
}

// planJail lays beds the tomb's way: a 1-cell aisle straight in
// from the door, beds on both sides, heads to the side walls.
func planJail(f InteriorFrame, _ InteriorPieceDef) ([]InteriorPiece, bool) {
	bed, ok := f.Shapes.Get(f.Shapes.Furniture.PrimaryBed())
	if !ok {
		return nil, false
	}
	aisle := f.Entrance
	// Turned to face the aisle a bed is Size.Z cells wide and Size.X deep.
	size, reach := bed.Size, bed.Size.Z
	var out []InteriorPiece
	for _, v := range AisleRows(f.Depth, size.X) {
		if v+size.X > f.Depth {
			continue
		}
		if aisle >= reach {
			p := NewInteriorPiece(fmt.Sprintf("bed.w%d", v+1), bed.Def, size, domain.West, domain.Cell{X: aisle - reach, Z: v})
			p.Row = "beds.west"
			out = append(out, p)
		}
		if aisle+1+reach <= f.Width {
			p := NewInteriorPiece(fmt.Sprintf("bed.e%d", v+1), bed.Def, size, domain.East, domain.Cell{X: aisle + 1, Z: v})
			p.Row = "beds.east"
			out = append(out, p)
		}
	}
	return out, len(out) > 0
}

// JailStepKind is the next jail step.
type JailStepKind string

const (
	// JailNone: no prisoner held, enough prisoner beds, no planned jail
	// with room, or a fact is unknown.
	JailNone JailStepKind = ""
	// JailReconcile: reconcile Room to Template, its next free bed
	// (ReconcileRoom): whatever the ring, floor and bed still owe.
	JailReconcile JailStepKind = "reconcile"
	// JailMark: set Marks, every markable bed standing in a planned jail,
	// for prisoners.
	JailMark JailStepKind = "mark"
)

// JailStep is one bounded step towards a prisoner bed per prisoner.
type JailStep struct {
	Kind JailStepKind
	Room PlannedRoom
	// Template is a JailReconcile's wanted furniture: the next free bed.
	Template []WantedPiece
	// Marks are a JailMark's beds. Bed owner type is room-level in the game
	// (SetBedOwnerTypeByInterface flips the whole room), so every markable
	// bed of the jail goes in one pass rather than one bed per pass.
	Marks []string
	// Strays are humanlike beds standing in a planned jail that native
	// refuses for prisoners (crib) or that are slave beds the plan did not put
	// there, and so stay as they are: to
	// remove or repurpose, never to mark. Reported on every step.
	Strays []string
	// Held is the living prisoners; Beds the beds set for prisoners.
	Held, Beds int
}

// NextJailStep picks the next jail step from the plan, the room census,
// the held prisoner count, the bed census and the colony's buildings.
func NextJailStep(plan LayoutPlan, rooms RoomObservation, held int, beds []SleepingBed, built []CurrentBuilding) JailStep {
	step := JailStep{Held: held}
	for _, b := range beds {
		if p, _ := b.Prisoners.Value(); p {
			step.Beds++
		}
	}
	if held == 0 {
		return JailStep{}
	}
	taken := map[domain.Cell]bool{}
	for _, b := range built {
		for _, c := range b.Cells {
			taken[c] = true
		}
	}
	var act JailStep
	for _, r := range plan.AllRooms() {
		if r.Role != PlannedPrison {
			continue
		}
		// Census: the room's id keys the beds read inside it. A room the
		// census does not show enclosed (a map-edge room is not) stands
		// for nothing: native would force its beds back to colonist.
		standing, stands := CensusRoomIn(r, rooms)
		var marks []string
		for _, b := range beds {
			humanlike, _ := b.Humanlike.Value()
			_, mk := b.Medical.Value()
			prisoners, pk := b.Prisoners.Value()
			if room, _ := b.Room.Value(); !stands || room != standing.ID || !humanlike || !mk || !pk || prisoners {
				continue
			}
			if b.Crib || b.Slaves {
				step.Strays = append(step.Strays, b.ID)
			} else {
				marks = append(marks, b.ID)
			}
		}
		if act.Kind != JailNone {
			continue
		}
		if len(marks) > 0 {
			sort.Strings(marks)
			act.Room, act.Kind, act.Marks = r, JailMark, marks
		} else if step.Beds < held {
			if piece, ok := jailSlot(r, rooms.Shapes, taken); ok {
				act.Room, act.Kind, act.Template = r, JailReconcile, []WantedPiece{piece.Wanted()}
			}
		}
	}
	sort.Strings(step.Strays)
	step.Room, step.Kind, step.Template, step.Marks = act.Room, act.Kind, act.Template, act.Marks
	return step
}

// jailSlot is the room's first template bed slot nothing stands on.
func jailSlot(r PlannedRoom, shapes PieceShapes, taken map[domain.Cell]bool) (InteriorPiece, bool) {
	in, ok := InteriorRoomFromLayout(r, shapes)
	if !ok {
		return InteriorPiece{}, false
	}
	bed, ok := in.Piece(shapes.Furniture.PrimaryBed())
	if !ok {
		return InteriorPiece{}, false
	}
	interior, ok := PlanRoomInterior(in, bed)
	if !ok {
		return InteriorPiece{}, false
	}
pieces:
	for _, p := range interior.Pieces {
		for _, c := range rectCells(p.Rect) {
			if taken[c] {
				continue pieces
			}
		}
		return p, true
	}
	return InteriorPiece{}, false
}
