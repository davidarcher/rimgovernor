package policy

import (
	"fmt"
)

// The wants of each role the region placer furnishes. A want's count is the
// capacity its template had on the same room (a bench row along the entrance
// wall, an aisle with a piece each side); the placer seats as many as the
// floor holds, and a want's minimum is what the role cannot do without. Pieces
// worked from the floor in front of them (a bench, a stove) stand against a
// wall, as their template stood them on the back wall.

// workshopWants: benches, a tool cabinet between each two, an input shelf per
// bench. A cabinet feeds the two benches beside it (the facility's
// maxSimultaneous 2), and shelves score 1 each toward Storeroom against 27 per
// bench toward Workshop, so one shelf per bench never flips the room's role.
func workshopWants(room InteriorRoom, piece InteriorPieceDef, f regionFrame) ([]RegionWant, bool) {
	bench, ok := room.benchDef(piece, RoomRoleWorkshop)
	cabinet, cok := room.Shapes.Get(room.Shapes.Furniture.Cabinet.Def)
	shelf, sok := room.Shapes.Get(interiorTable.ShelfDef)
	n := benchRowCapacity(f, bench, 1)
	if !ok || !cok || !sok || n < 1 {
		return nil, false
	}
	benches := shapeWant("benches", bench, n, 1, numbered("bench", 0))
	benches.AgainstWall = true
	return []RegionWant{
		benches,
		shapeWant("cabinets", cabinet, n-1, 0, numbered("cabinet", 0)),
		shapeWant("shelves", shelf, n, 0, numbered("shelf", 0)),
	}, true
}

// laboratoryWants: research benches and the analyzer they share. The analyzer
// is optional: it links only the benches within its reach, which the search
// prices as links.
func laboratoryWants(room InteriorRoom, piece InteriorPieceDef, f regionFrame) ([]RegionWant, bool) {
	bench, ok := room.benchDef(piece, RoomRoleLaboratory)
	n := benchRowCapacity(f, bench, 1)
	if !ok || n < 1 {
		return nil, false
	}
	benches := shapeWant("benches", bench, n, 1, numbered("bench", 1))
	benches.AgainstWall = true
	wants := []RegionWant{benches}
	if analyzer, ok := room.Shapes.Get(room.Shapes.Furniture.Analyzer.Def); ok {
		wants = append(wants, shapeWant("analyzer", analyzer, 1, 0, func(int) string { return labAnalyzerSlot }))
	}
	return wants, true
}

// kitchenWants: stoves, at most InteriorTable.KitchenStoves. A kitchen's second
// door is its freezer's; where the stoves stand against the walls is the
// search's to price by the walks between stove, freezer door and storage.
func kitchenWants(room InteriorRoom, piece InteriorPieceDef, f regionFrame) ([]RegionWant, bool) {
	stove, ok := room.benchDef(piece, RoomRoleKitchen)
	if !ok || !frontInteraction(stove) || min(f.Width, f.Depth) < stove.Size.Z+2 {
		return nil, false
	}
	n := min(int(RowCapacity(max(f.Width, f.Depth), stove.Size.X, 0)), interiorTable.KitchenStoves)
	if n < 1 {
		return nil, false
	}
	stoves := shapeWant("stoves", stove, n, 1, func(i int) string { return fmt.Sprintf("stove.%c", rune('1'+i)) })
	stoves.AgainstWall = true
	return []RegionWant{stoves}, true
}

// aisleWants is a double-sided room (the tomb, the jail): one piece on each
// side of a 1-cell aisle straight in from the door, one every piece-depth, the
// piece's heads against the side walls. The names are the rows' and sides'
// (prefix.w1 ... prefix.e1 ...).
func aisleWants(slot, prefix string, d InteriorPieceDef, f regionFrame) ([]RegionWant, bool) {
	// Turned to face the aisle a piece is Size.Z cells wide and Size.X deep.
	reach := d.Size.Z
	var names []string
	for _, v := range AisleRows(f.Depth, d.Size.X) {
		if v+d.Size.X > f.Depth {
			continue
		}
		if f.Entrance >= reach {
			names = append(names, fmt.Sprintf("%s.w%d", prefix, v+1))
		}
		if f.Entrance+1+reach <= f.Width {
			names = append(names, fmt.Sprintf("%s.e%d", prefix, v+1))
		}
	}
	if len(names) == 0 {
		return nil, false
	}
	w := shapeWant(slot, d, len(names), 1, func(i int) string { return names[i] })
	w.AgainstWall = true
	return []RegionWant{w}, true
}

// tombWants: sarcophagi along both sides of the aisle.
func tombWants(room InteriorRoom, _ InteriorPieceDef, f regionFrame) ([]RegionWant, bool) {
	sarcophagus, ok := room.Shapes.Get(room.Shapes.Furniture.Sarcophagus)
	if !ok {
		return nil, false
	}
	return aisleWants("sarcophagi", "sarcophagus", sarcophagus, f)
}

// jailWants: prisoner beds along both sides of the aisle.
func jailWants(room InteriorRoom, _ InteriorPieceDef, f regionFrame) ([]RegionWant, bool) {
	bed, ok := room.Shapes.Get(room.Shapes.Furniture.PrimaryBed())
	if !ok {
		return nil, false
	}
	return aisleWants("beds", "bed", bed, f)
}

// hospitalWants: beds, heads against a wall, in pairs that share a vitals
// monitor between them (a monitor must stand adjacent to the bed it serves and
// links every adjacent bed).
func hospitalWants(room InteriorRoom, _ InteriorPieceDef, f regionFrame) ([]RegionWant, bool) {
	bed, bok := room.Shapes.Get(room.Shapes.Furniture.PrimaryBed())
	monitor, mok := room.Shapes.Get(room.Shapes.Furniture.Monitor.Def)
	if !bok || !mok || f.Depth < bed.Size.Z+1 {
		return nil, false
	}
	pairs := int(RowCapacity(f.Width, 2*bed.Size.X+monitor.Size.X, 1))
	if pairs < 1 {
		return nil, false
	}
	beds := shapeWant("beds", bed, 2*pairs, 1, numbered("bed", 1))
	beds.AgainstWall = true
	return []RegionWant{beds, shapeWant("monitors", monitor, pairs, 0, numbered("monitor", 1))}, true
}
