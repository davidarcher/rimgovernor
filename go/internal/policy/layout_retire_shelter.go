package policy

import (
	"fmt"
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Shelter retirement (#2046, epic #2037): the temporary shelter leaves the
// plan once every colonist owns a bed in a built bedroom and the workshop and
// laboratory rooms stand. It is re-evaluated every review with no latch, so a
// joiner without a bed brings it back into the gate. A research table still
// standing in the shelter holds retirement until it is relocated.

// ShelterRetirable reports whether the plan's shelter has done its job: it
// holds a shelter room, every colonist owns a bed in a built bedroom outside
// the shelter, every planned workshop and laboratory room stands (and there is
// one of each), and no research table stands in a shelter. Unknown or
// partial facts never retire it.
func ShelterRetirable(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, built []CurrentBuilding) bool {
	return ShelterEmptied(plan, rooms, sleeping) && len(shelterTables(plan, built)) == 0
}

// ShelterEmptied is ShelterRetirable but for the research table: the shelter
// is housed and the work rooms stand, so only a table still in it keeps it
// planned. The table's relocation (#2047) starts from here.
func ShelterEmptied(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation) bool {
	shelters := plan.roomsOf(PlannedShelter)
	if len(shelters) == 0 || len(sleeping.People) == 0 || len(sleeping.People) != sleeping.Colonists {
		return false
	}
	for _, role := range []PlannedRole{PlannedWorkshop, PlannedLab} {
		planned := plan.roomsOf(role)
		if len(planned) == 0 {
			return false
		}
		for _, r := range planned {
			if _, ok := PlannedRoomStanding(r, rooms); !ok {
				return false
			}
		}
	}
	inShelter := map[string]bool{}
	for _, s := range shelters {
		if room, ok := PlannedRoomStanding(s, rooms); ok {
			for _, b := range room.Beds {
				inShelter[b] = true
			}
		}
	}
	bedroomBed := map[string]bool{}
	for _, room := range rooms.Rooms {
		if role, known := room.Role.Value(); known && role == RoomRoleBedroom {
			for _, b := range room.Beds {
				bedroomBed[b] = !inShelter[b]
			}
		}
	}
	for _, p := range sleeping.People {
		if bed, known := p.OwnedBed.Value(); !known || !bedroomBed[bed] {
			return false
		}
	}
	return true
}

// shelterTables are the research tables standing inside a planned shelter.
func shelterTables(plan LayoutPlan, built []CurrentBuilding) []CurrentBuilding {
	var out []CurrentBuilding
	for _, s := range plan.roomsOf(PlannedShelter) {
		for _, b := range built {
			if len(b.Cells) > 0 && rectInside(s.Interior, cellsRectangle(b.Cells)) && strings.Contains(b.Building.Definition(), "ResearchBench") {
				out = append(out, b)
			}
		}
	}
	return out
}

// ShelterTableMove is the reinstall that carries the shelter's research table
// into a free bench slot of a planned laboratory (#2047), once the shelter is
// otherwise retirable. It is the TidyLayout furniture proposal: the table
// keeps its quality and hit points, research continues at the new spot, and
// the retirement gate opens when it is gone. None while the table was already
// tried (tidied), no laboratory slot is free, or the facts are partial.
func ShelterTableMove(plan LayoutPlan, rooms RoomObservation, sleeping SleepingObservation, built []CurrentBuilding, tidied map[string]bool) (TidyProposal, bool) {
	if !ShelterEmptied(plan, rooms, sleeping) {
		return TidyProposal{}, false
	}
	for _, table := range shelterTables(plan, built) {
		if tidied[table.ID] || table.ID == "" {
			continue
		}
		from := cellsRectangle(table.Cells)
		rot := table.Building.Rotation()
		size := domain.Cell{X: from.Width, Z: from.Height}
		if rot == domain.East || rot == domain.West {
			size = domain.Cell{X: from.Height, Z: from.Width}
		}
		def := table.Building.Definition()
		for _, lab := range plan.roomsOf(PlannedLab) {
			input, ok := InteriorRoomFromLayout(lab, rooms.Shapes)
			if !ok {
				continue
			}
			taken := map[domain.Cell]bool{}
			standing := map[string]bool{}
			for _, b := range built {
				if len(b.Cells) > 0 && rectInside(lab.Interior, cellsRectangle(b.Cells)) {
					standing[b.Building.Definition()] = true
					for _, c := range b.Cells {
						taken[c] = true
					}
				}
			}
			for d := range standing {
				input.Standing = append(input.Standing, d)
			}
			sort.Strings(input.Standing)
			interior, ok := PlanInterior(input, InteriorPieceDef{})
			if !ok {
				continue
			}
			for _, slot := range interior.Pieces {
				if slot.Def != def || slot.Size != size || overlapsCells(slot.Rect, taken) {
					continue
				}
				move := TidyMove{Thing: table.ID, Def: def, Slot: slot.Slot, Size: size, From: from, To: slot.Rect, Rot: slot.Rot}
				return TidyProposal{
					Item:        TidyItem{Kind: TidyFurniture, ID: "shelter-table-" + table.ID, Footprint: lab.Interior, Cells: 1},
					Gain:        1,
					Moves:       []TidyMove{move},
					Explanation: fmt.Sprintf("research table %s leaves the retiring shelter for laboratory slot %s", table.ID, slot.Slot),
				}, true
			}
		}
	}
	return TidyProposal{}, false
}

func overlapsCells(r Rectangle, cells map[domain.Cell]bool) bool {
	for _, c := range rectCells(r) {
		if cells[c] {
			return true
		}
	}
	return false
}

// InFlightRooms are the interiors of the plan's rooms an open journal plan is
// working on, keyed by origin as InFlightRoomCells is.
func InFlightRooms(plan LayoutPlan, origins map[domain.Cell]bool) map[Rectangle]bool {
	out := map[Rectangle]bool{}
	for _, r := range plan.AllRooms() {
		if origins[domain.Cell{X: r.Interior.X, Z: r.Interior.Z}] {
			out[r.Interior] = true
		}
	}
	return out
}

// retireShelter drops the shelter rooms with no open plan working on them
// once growth says the shelter is retirable.
func retireShelter(plan LayoutPlan, growth RoomGrowth) (LayoutPlan, bool) {
	if !growth.RetireShelter {
		return plan, false
	}
	drop := map[Rectangle]bool{}
	var ground []Rectangle
	for _, r := range plan.roomsOf(PlannedShelter) {
		if !growth.InFlight[r.Interior] {
			drop[r.Interior] = true
			ground = append(ground, roomGround(r.Interior))
		}
	}
	next, dropped := dropRooms(plan, drop)
	if dropped {
		next.RetiredGround = append(slices.Clone(plan.RetiredGround), ground...)
	}
	return next, dropped
}
