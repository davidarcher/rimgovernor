package policy

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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

// namedPiece is a want for one piece called name.
func namedPiece(name string, d InteriorPieceDef, min int) RegionWant {
	return shapeWant(name, d, 1, min, func(int) string { return name })
}

// sleepingBed is the bed a bedroom plans around: the requested bed, else the
// room's standing one, else the primary bed.
func (r InteriorRoom) sleepingBed(piece InteriorPieceDef) (InteriorPieceDef, bool) {
	if piece.Family == RoomRoleBedroom {
		return piece, true
	}
	for _, d := range r.Standing {
		if s := r.Shapes.standing(d); s.Family == RoomRoleBedroom {
			return s, true
		}
	}
	return r.Shapes.Get(r.Shapes.Furniture.PrimaryBed())
}

// furnishingWants are the levers beside a bed or throne: an end table, a
// dresser and a standing lamp, each in its slot and each optional (the room
// keeps its bed or throne when one does not fit); skip names the ones a room
// leaves out. A catalog without a row for one has no plan.
func furnishingWants(room InteriorRoom, skip func(slot string, d InteriorPieceDef) bool) ([]RegionWant, bool) {
	table, tok := room.Shapes.Get(room.Shapes.Furniture.EndTable.Def)
	dresser, dok := room.Shapes.Get(room.Shapes.Furniture.Dresser.Def)
	lamp, lok := room.Shapes.Get(interiorTable.LampDef)
	if !tok || !dok || !lok {
		return nil, false
	}
	var out []RegionWant
	for _, w := range []struct {
		slot string
		d    InteriorPieceDef
	}{{"end_table", table}, {"dresser", dresser}, {"lamp", lamp}} {
		if skip == nil || !skip(w.slot, w.d) {
			out = append(out, namedPiece(w.slot, w.d, 0))
		}
	}
	return out, true
}

// bedroomWants: the bed, then an end table, a dresser and a lamp while the
// room keeps the Space stat the bedroom's quality needs (bedroomSpace). The
// small rooms of the first tiers hold the bed and end table only
// (bedroomFullSetTiles). Only the bed is required: a cramped room gets a bare
// bed rather than no plan. The end table links only beside the bed's head and
// the dresser within 6 cells: the search prices both as facility links.
func bedroomWants(room InteriorRoom, piece InteriorPieceDef, _ regionFrame) ([]RegionWant, bool) {
	bed, ok := room.sleepingBed(piece)
	if !ok {
		return nil, false
	}
	area := int32(room.Floor().Area())
	blocked := int(bed.Size.X * bed.Size.Z)
	wants, ok := furnishingWants(room, func(slot string, d InteriorPieceDef) bool {
		cells := int(d.Size.X * d.Size.Z)
		if slot != "end_table" && area < bedroomFullSetTiles || bedroomSpace(area, 1, blocked+cells) < bedroomMinSpace {
			return true
		}
		blocked += cells
		return false
	})
	if !ok {
		return nil, false
	}
	return append([]RegionWant{namedPiece("bed", bed, 1)}, wants...), true
}

// throneWants: the throne when one is being placed, the title's required
// pieces (each in its slot, left out when no wall cell fits), then the
// bedroom's furnishing levers. Asked for no piece, the plan is the furniture
// alone, around the cell the throne stands on.
func throneWants(room InteriorRoom, piece InteriorPieceDef, _ regionFrame) ([]RegionWant, bool) {
	var wants []RegionWant
	if piece.Def != "" && piece.Size.X > 0 && piece.Size.Z > 0 {
		throne := namedPiece(throneSlot, piece, 1)
		throne.AgainstWall = true
		wants = append(wants, throne)
	}
	for _, req := range room.Required {
		if req.Piece.Size.X > 0 && req.Piece.Size.Z > 0 {
			w := namedPiece(req.Slot, req.Piece, 0)
			w.AgainstWall = true
			wants = append(wants, w)
		}
	}
	furniture, ok := furnishingWants(room, nil)
	return append(wants, furniture...), ok
}

// childRoomWants: as many of the piece as the template's bands held on this
// room, one slot each ("child.0", ...); a room asked for no piece plans
// nothing.
func childRoomWants(_ InteriorRoom, piece InteriorPieceDef, f regionFrame) ([]RegionWant, bool) {
	n := len(childSlots(f.Width, f.Depth, f.Entrance, piece.Size))
	if piece.Def == "" || n == 0 {
		return nil, false
	}
	return []RegionWant{shapeWant("child", piece, n, 1, numbered("child", 0))}, true
}

// shelterWants: the research table, a crafting spot, the campfires and coolers
// the climate asks for, then one bunk per occupant (every bunk that fits for
// 0). A piece is planned only for a capability the room hosts, and only the
// bunks matter: a cramped room, or a catalog without a row for a piece, gets
// the bunks alone rather than no plan. A bunk is a 1x2 whatever it is made of
// (a sleeping spot, a bed, a bedroll), the slot a spot and its bed share.
func shelterWants(room InteriorRoom, piece InteriorPieceDef, f regionFrame) ([]RegionWant, bool) {
	hosts := func(c Capability) bool { return slices.Contains(room.Capabilities, c) }
	var wants []RegionWant
	if bench, ok := room.benchDef(piece, RoomRoleLaboratory); ok && hosts(CapResearch) && benchRowCapacity(f, bench, 1) >= 1 {
		research := namedPiece("research", bench, 0)
		research.AgainstWall = true
		wants = append(wants, research)
	}
	single := func(slot, def string, count int, name func(i int) string) {
		if shape, ok := room.Shapes.Get(def); ok && shape.Size == (domain.Cell{X: 1, Z: 1}) && count > 0 {
			wants = append(wants, shapeWant(slot, shape, count, 0, name))
		}
	}
	if hosts(CapCraft) {
		single("craft", shelterCraftingDef, 1, func(int) string { return "craft" })
	}
	if hosts(CapHeat) {
		single("campfire", ShelterHeatDef, room.Campfires, numbered("campfire", 1))
	}
	single("cooler", ShelterCoolDef, room.Coolers, numbered(ShelterCoolerSlotPrefix[:len(ShelterCoolerSlotPrefix)-1], 1))
	bunkDef := room.Shapes.Furniture.PrimaryBed()
	if piece.Family == RoomRoleBedroom && piece.Size == shelterBunkSize {
		bunkDef = piece.Def
	}
	if bunkDef == "" {
		bunkDef = SleepingSpotDefinition
	}
	n := room.Occupants
	if n <= 0 {
		n = room.Floor().Area() / 2
	}
	bunk := InteriorPieceDef{Def: bunkDef, Size: shelterBunkSize}
	wants = append(wants, shapeWant("bunk", bunk, n, 0, numbered(shelterBunkSlotPrefix[:len(shelterBunkSlotPrefix)-1], 1)))
	return wants, true
}

// barnWants: the animal beds, as many as the barn's bed grid held (an aisle
// from the door and a bed row between free rows, herdGridBeds sizes a barn by
// it), each tended from a free cell beside it, and the climate heater. A bed
// is tended from the cell in front of it (an interaction cell, which the placer
// keeps open floor and which beds may share), so every animal can be reached
// whatever the placer does with the rest of the floor.
func barnWants(room InteriorRoom, piece InteriorPieceDef, f regionFrame) ([]RegionWant, bool) {
	size := piece.Size
	if piece.Def == "" || size.X < 1 || size.Z < 1 {
		return nil, false
	}
	var rows int32
	for v := size.Z; v+size.Z <= f.Depth; v += 2 * size.Z {
		rows++
	}
	n := int(rows) * int(f.Entrance/size.X+(f.Width-f.Entrance-1)/size.X)
	if n == 0 {
		return nil, false
	}
	beds := shapeWant("bed", piece, n, 1, numbered("bed", 1))
	if beds.Interaction == nil {
		beds.Interaction = &domain.Cell{Z: -1}
	}
	// The heater places first: the beds' tended cells use every cell the beds
	// leave, so the heater would find none after them.
	var wants []RegionWant
	if heater, ok := room.Shapes.Get(room.Shapes.Furniture.Heater); ok && heater.Size == (domain.Cell{X: 1, Z: 1}) {
		wants = append(wants, namedPiece(herdHeaterSlot, heater, 0))
	}
	return append(wants, beds), true
}

// diningWants: the throw pin with its clear lane, then tables each ringed by
// six chairs that face them, then a lamp. The counts are the template's: a
// table unit is end chair, 1x2 table, end chair along a line with a side chair
// each side of the table, one cell between units, row 0 left for the entrance.
// A dining room drops the pin before its first table and a rec room drops
// tables before its pin, so the first of the two is the minimum.
func diningWants(recFirst bool) regionRole {
	return func(room InteriorRoom, _ InteriorPieceDef, f regionFrame) ([]RegionWant, bool) {
		d := room.Dining
		if d.Validate() != nil || f.Width < 5 {
			return nil, false
		}
		units := func(depth int32) int { return int(RowCapacity(depth-1, diningUnitDepth, 1)) }
		pin, tables := f.Depth >= d.Lane+1, units(f.Depth)
		if pin {
			if withPin := units(f.Depth - d.Lane); withPin > 0 || recFirst {
				tables = withPin
			} else {
				pin = false
			}
		}
		if tables == 0 && !pin {
			return nil, false
		}
		var wants []RegionWant
		if pin {
			w := namedPiece("horseshoes", d.Pin, 0)
			if recFirst {
				w.Min = 1
			}
			w.Clear, w.AgainstWall = d.Lane-1, true
			wants = append(wants, w)
		}
		for i := range tables {
			table := namedPiece(fmt.Sprintf("table.%d", i), d.Table, 0)
			if i == 0 && !recFirst {
				table.Min = 1
			}
			table.Open = true
			chairs := shapeWant(fmt.Sprintf("chair.%d", i), d.Chair, 6, 0, numbered(fmt.Sprintf("chair.%d", i), 0))
			chairs.Beside = table.Slot
			wants = append(wants, table, chairs)
		}
		if lamp, ok := room.Shapes.Get(interiorTable.LampDef); ok {
			wants = append(wants, namedPiece("lamp", lamp, 0))
		}
		return wants, true
	}
}
