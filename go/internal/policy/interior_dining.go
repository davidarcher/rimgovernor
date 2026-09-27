package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The dining/rec template (#804): tables on the room's centre line, running
// away from the entrance, each with a chair on every edge; a horseshoes pin
// against the back wall with its throw lane kept clear. A dining room and a
// rec room host each other's furniture, so one template serves both and
// only the priority differs: a dining room drops the pin before its first
// table, a rec room drops tables before its pin.

const (
	// A table unit is end chair, 1x2 table, end chair along the centre
	// line, with side chairs either side of the table.
	diningUnitDepth = 4
	// horseshoesLane is how far from the pin a thrower stands
	// (JoyGiver_WatchBuilding: five cells out, three cells wide), so the
	// lane is the pin plus five cells toward the entrance.
	horseshoesLane = 6
)

func init() {
	RegisterInteriorTemplate(RoomRoleDiningRoom, InteriorTemplate{Name: "dining", Plan: func(f InteriorFrame, _ InteriorPieceDef) ([]InteriorPiece, bool) { return planDiningRec(f, false) }})
	RegisterInteriorTemplate(RoomRoleRecRoom, InteriorTemplate{Name: "rec", Plan: func(f InteriorFrame, _ InteriorPieceDef) ([]InteriorPiece, bool) { return planDiningRec(f, true) }})
}

func planDiningRec(f InteriorFrame, recFirst bool) ([]InteriorPiece, bool) {
	// Side aisles either side of the chairs keep the back reachable.
	if f.Width < 5 {
		return nil, false
	}
	c := CentreStart(f.Width, 1)
	// Row 0 stays floor for the entrance; units are separated by one cell.
	units := func(depth int32) int32 { return RowCapacity(depth-1, diningUnitDepth, 1) }
	pin := f.Depth >= horseshoesLane+1
	tables := units(f.Depth)
	if pin {
		if withPin := units(f.Depth - horseshoesLane); withPin > 0 || recFirst {
			tables = withPin
		} else {
			pin = false
		}
	}
	if tables == 0 && !pin {
		return nil, false
	}
	var out []InteriorPiece
	chair := func(slot string, rot domain.Rotation, u, v int32) InteriorPiece {
		p := NewInteriorPiece(slot, "DiningChair", domain.Cell{X: 1, Z: 1}, rot, domain.Cell{X: u, Z: v})
		p.Centred = u == c
		return p
	}
	for i := int32(0); i < tables; i++ {
		v := 1 + i*(diningUnitDepth+1)
		table := NewInteriorPiece(fmt.Sprintf("table.%d", i), "Table1x2c", domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: c, Z: v + 1})
		table.Centred = true
		out = append(out, table,
			chair(fmt.Sprintf("chair.%d.front", i), domain.North, c, v),
			chair(fmt.Sprintf("chair.%d.back", i), domain.South, c, v+3))
		for j := int32(1); j <= 2; j++ {
			left := chair(fmt.Sprintf("chair.%d.left.%d", i, j), domain.East, c-1, v+j)
			right := chair(fmt.Sprintf("chair.%d.right.%d", i, j), domain.West, c+1, v+j)
			if f.Width%2 == 1 {
				left.Pair = fmt.Sprintf("chairs.%d.%d", i, j)
				right.Pair = left.Pair
			}
			out = append(out, left, right)
		}
	}
	if pin {
		p := NewInteriorPiece("horseshoes", "HorseshoesPin", domain.Cell{X: 1, Z: 1}, domain.North, domain.Cell{X: c, Z: f.Depth - 1})
		p.Centred = true
		out = append(out, p)
	}
	// A standing lamp in a back corner is the common-room quality lever
	// (#816): the gap closer places it only for a room below its target.
	// It is left out when the corner would cut a path.
	blocked := map[domain.Cell]bool{}
	for _, p := range out {
		for _, c := range rectCells(p.Rect) {
			blocked[c] = true
		}
	}
	room := InteriorRoom{Interior: Rectangle{Width: f.Width, Height: f.Depth}, Doors: f.Doors}
	lamp := NewInteriorPiece("lamp", "StandingLamp", standLampSize, domain.South, domain.Cell{X: f.Width - 1, Z: f.Depth - 1})
	if cells := rectCells(lamp.Rect); !blocked[cells[0]] && InteriorPlacementWalkable(room, blocked, cells) {
		out = append(out, lamp)
	}
	return out, true
}
