package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Furnishing the jail (#880). While a prisoner is held, MaintainPopulation
// keeps one bed set for prisoners in a planned jail (#835) per held prisoner:
// it sets an unflagged bed standing in a jail for prisoners first, and
// otherwise reconciles the room (ReconcileRoom) to the next free template
// bed: the ring, the floor and the bed, installed from packed stock first. A
// fresh bed is placed plain and flagged on a later step once it stands.
// Wardens bring the prisoners to the beds; nothing here hauls.

func init() {
	RegisterInteriorTemplate(RoomRolePrisonCell, InteriorTemplate{Name: "jail", Plan: planJail})
}

// planJail lays beds the tomb's way (#831): a 1-cell aisle straight in
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
	// JailMark: set Bed, standing in a jail, for prisoners.
	JailMark JailStepKind = "mark"
)

// JailStep is one bounded step towards a prisoner bed per prisoner.
type JailStep struct {
	Kind JailStepKind
	Room PlannedRoom
	// Template is a JailReconcile's wanted furniture: the next free bed.
	Template []WantedPiece
	Bed      string
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
	if held == 0 || step.Beds >= held {
		return JailStep{}
	}
	taken := map[domain.Cell]bool{}
	for _, b := range built {
		for _, c := range b.Cells {
			taken[c] = true
		}
	}
	for _, r := range plan.AllRooms() {
		if r.Role != PlannedPrison {
			continue
		}
		// Census: the room's id keys the beds read inside it.
		standing, stands := CensusRoomIn(r, rooms)
		for _, b := range beds {
			if !stands {
				break
			}
			humanlike, _ := b.Humanlike.Value()
			medical, _ := b.Medical.Value()
			prisoners, pk := b.Prisoners.Value()
			if room, _ := b.Room.Value(); room == standing.ID && humanlike && !medical && pk && !prisoners {
				step.Room, step.Kind, step.Bed = r, JailMark, b.ID
				return step
			}
		}
		if piece, ok := jailSlot(r, rooms.Shapes, taken); ok {
			step.Room, step.Kind, step.Template = r, JailReconcile, []WantedPiece{piece.Wanted()}
			return step
		}
	}
	return JailStep{}
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
	interior, ok := PlanInterior(in, bed)
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
