package policy

import "slices"

// GlowDefNames are the definitions the title wants lit: every def of its
// Glowing requirement sets and of its any-of count requirements (the
// braziers), sorted and unique. Names are the mirror's, never a Go list.
func (n ThroneNeed) GlowDefNames() []string {
	var names []string
	for _, set := range n.Glowing {
		names = append(names, set...)
	}
	for _, r := range n.AnyOfCounts {
		names = append(names, r.Things...)
	}
	slices.Sort(names)
	return slices.Compact(names)
}

// NextThroneRefuel is the refuel due for the throne room's unlit lights: the
// lowest-ID lamp of a glowing definition standing inside the title's room
// that the census reads unlit and out of fuel, ordered through the refuel
// work giver for the best hauler (the rearm's pick). None while the room is
// not planned and standing, or when no unlit lamp is measured empty (an
// unknown fuel state is neither a deficit nor a refuel).
func NextThroneRefuel(plan LayoutPlan, rooms RoomObservation, need ThroneNeed, lamps []Lamp, workers []WorkPawn) ThroneStep {
	room, ok := plan.ThroneRoomFor(need.MinArea)
	if !ok {
		return ThroneStep{}
	}
	// Census: the lamp refuel needs the roofed room, not just a ring.
	if _, ok := CensusRoomIn(room, rooms); !ok {
		return ThroneStep{}
	}
	glow := need.GlowDefNames()
	best := ""
	for _, l := range lamps {
		if !slices.Contains(glow, l.Definition) || !cellInRect(room.Interior, l.Cell) || l.Lit {
			continue
		}
		if empty, known := l.OutOfFuel.Value(); !known || !empty {
			continue
		}
		if best == "" || l.ID < best {
			best = l.ID
		}
	}
	pawn, found := defenseRearmPawn(workers)
	if best == "" || !found {
		return ThroneStep{}
	}
	return ThroneStep{Kind: ThroneRefuel, Room: room, Need: need, Lamp: best, Pawn: pawn}
}
