package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Suite growth on the ground (#1218, epic #1200). Once the plan row holds
// a suite's grown interior (growSuitesOutward), the sleeping planner walks
// it there in order: the new ring goes up around the extension, and only
// once the extension stands enclosed and roofed as a room of its own does
// the old outer wall come down (a plain DECONSTRUCT Designate); then the
// furniture is re-sited onto the grown room's interior plan. The owner
// keeps the bed throughout. Nothing is stored: each step reads from the
// census how far the growth has got.

// SuiteGrowthKind is the next growth step.
type SuiteGrowthKind string

const (
	// SuiteGrowthShell: raise the grown room's ring; the old room stands
	// inside it and the extension is not yet enclosed and roofed.
	SuiteGrowthShell SuiteGrowthKind = "grow-shell"
	// SuiteGrowthOpen: deconstruct Walls, the old wall standing inside the
	// grown interior, now that the extension stands enclosed.
	SuiteGrowthOpen SuiteGrowthKind = "grow-open"
	// SuiteGrowthRelocate: re-site Moves, the suite's off-plan furniture,
	// onto the grown room's interior plan.
	SuiteGrowthRelocate SuiteGrowthKind = "grow-relocate"
)

// SuiteGrowth is one bounded step of growing a suite.
type SuiteGrowth struct {
	Kind  SuiteGrowthKind
	Room  LayoutRoom
	Walls []CurrentBuilding
	// RoomID is the census room the moves re-site in.
	RoomID string
	Moves  []TidyMove
}

// NextSuiteGrowth is the first suite (in plan order) with a growth step
// due. A suite is growing while no census room fills its planned interior
// and an enclosed room inside it holds a bed (the old suite). The wall
// step needs a second enclosed room inside the interior (the extension,
// Enclosed only with no open roof); walls still inside a standing suite
// come down before any furniture moves. tidy is TidyFurnitureRooms.
func NextSuiteGrowth(plan LayoutPlan, rooms RoomObservation, census CurrentConstruction, tidy []TidyRoom) (SuiteGrowth, bool) {
	for _, r := range plan.AllRooms() {
		if r.Role != ModuleSuite {
			continue
		}
		// The interior is covered once every cell is enclosed room floor
		// or a wall standing on it: the ring is complete and roofed.
		walls := wallsInside(r.Interior, census)
		parts := roomsInside(r.Interior, rooms)
		home, covered := false, 0
		for _, p := range parts {
			home = home || len(p.Beds) > 0
			covered += len(p.Cells)
		}
		for _, w := range walls {
			covered += len(w.Cells)
		}
		switch {
		case !home:
			continue
		case int64(covered) < int64(r.Interior.Width)*int64(r.Interior.Height):
			return SuiteGrowth{Kind: SuiteGrowthShell, Room: r}, true
		case len(walls) > 0:
			return SuiteGrowth{Kind: SuiteGrowthOpen, Room: r, Walls: walls}, true
		}
		room, full := roomFilling(r.Interior, rooms)
		if !full {
			continue
		}
		for _, t := range tidy {
			if t.ID != room.ID || t.Room.Interior != r.Interior {
				continue
			}
			if moves := tidyFurnitureOrder(t, tidyFurnitureMatch(t, nil)); len(moves) > 0 {
				return SuiteGrowth{Kind: SuiteGrowthRelocate, Room: r, RoomID: room.ID, Moves: moves}, true
			}
		}
	}
	return SuiteGrowth{}, false
}

// roomFilling is the enclosed census room whose floor covers all of in.
func roomFilling(in Rectangle, rooms RoomObservation) (Room, bool) {
	for _, room := range roomsInside(in, rooms) {
		seen := map[domain.Cell]bool{}
		for _, c := range room.Cells {
			seen[c] = true
		}
		if int64(len(seen)) == int64(in.Width)*int64(in.Height) {
			return room, true
		}
	}
	return Room{}, false
}

// wallsInside is the standing walls whose footprint lies inside in.
func wallsInside(in Rectangle, census CurrentConstruction) []CurrentBuilding {
	var out []CurrentBuilding
	for _, b := range census.Buildings {
		if b.ID == "" || b.Building.Definition() != "Wall" || len(b.Cells) == 0 {
			continue
		}
		if rectInside(in, cellsRectangle(b.Cells)) {
			out = append(out, b)
		}
	}
	return out
}
