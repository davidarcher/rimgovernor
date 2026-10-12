package policy

import (
	"errors"
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// crossingFacts are the hand plans' crossing facts: what the station group of
// a room of that role would give it.
func crossingFacts(role PlannedRole) *RoomCrossing {
	if role == PlannedBedroom {
		return &RoomCrossing{Beds: 1}
	}
	return &RoomCrossing{}
}

// labBeyond is passThroughRow with a laboratory behind the far bedroom, joined
// to it by a link door: no trip ends in a laboratory, so only the
// reachability demand sees it.
func labBeyond(linked bool) LayoutPlan {
	p := passThroughRow(true)
	lab := PlannedRoom{ID: 4, Role: PlannedLab, Crossing: &RoomCrossing{Clean: true}, Region: RectRegion(Rectangle{X: 35, Z: 20, Width: 4, Height: 4}), Door: domain.Cell{X: 34, Z: 21}, DoorRot: domain.West}
	p.Rooms = append(p.Rooms, lab)
	if !linked {
		// Its door lies nowhere near the base.
		p.Rooms[3].Door, p.Rooms[3].DoorRot = domain.Cell{}, domain.East
	}
	if linked {
		p.Links = append(p.Links, RoomLink{A: 3, B: 4, Cell: domain.Cell{X: 34, Z: 21}})
	}
	p.NextRoomID = 5
	return p
}

// Every room must be reachable from an entrance, whatever trips exist.
func TestCheckRoutesDemandsEveryRoomReachable(t *testing.T) {
	// A sealed room: a door in a wall nothing joins to the rest.
	if _, err := CheckRoutes(labBeyond(false)); err == nil || !strings.Contains(err.Error(), "room 4 (lab) is not reachable") {
		t.Fatalf("a sealed room is reachable: %v", err)
	}
	// Reached only across a bedroom that is not marked Through.
	var through *ThroughfareError
	if _, err := CheckRoutes(labBeyond(true)); !errors.As(err, &through) || through.Room != 3 || through.To != PlannedLab {
		t.Fatalf("a room behind a closed bedroom is reachable: %v", err)
	}
	// Marked Through, the bedroom lets the way in.
	open := labBeyond(true)
	open.Rooms[2].Through = true
	if _, err := CheckRoutes(open); err != nil {
		t.Fatalf("flagged room still blocks: %v", err)
	}
	// A room that costs nothing to cross needs no mark.
	free := labBeyond(true)
	free.Rooms[2].Crossing = &RoomCrossing{}
	if _, err := CheckRoutes(free); err != nil {
		t.Fatalf("a free room blocks: %v", err)
	}
}

// A room can be reached around a closed room that a shorter path crosses.
func TestReachabilityTakesTheWayAround(t *testing.T) {
	p := labBeyond(true)
	// Lab gets a second door onto the entrance side, so the closed bedroom is
	// not the only way in (the shortest path may still lead through it).
	p.Rooms[3].Door = domain.Cell{X: 36, Z: 19}
	p.Rooms[3].DoorRot = domain.North
	p.Entrances = append(p.Entrances, domain.Cell{X: 36, Z: 18})
	p.Rooms[2].Through = false
	if _, err := CheckRoutes(p); err != nil {
		t.Fatalf("lab with its own door is rejected: %v", err)
	}
}

// A cold room is never crossed, marked or not; a free room is crossed unmarked.
func TestColdRoomIsNeverCrossed(t *testing.T) {
	cold := passThroughRow(true)
	cold.Rooms[1].Crossing = &RoomCrossing{Cold: true}
	var through *ThroughfareError
	if _, err := CheckRoutes(cold); !errors.As(err, &through) || through.Room != 2 {
		t.Fatalf("cold room crossed: %v", err)
	}
	free := passThroughRow(false)
	free.Rooms[1].Crossing = &RoomCrossing{}
	if _, err := CheckRoutes(free); err != nil {
		t.Fatalf("a room with nothing to cost is not crossable: %v", err)
	}
}

func TestCrossingReadsTheGroup(t *testing.T) {
	station := func(purpose StationPurpose, count int) GroupStation {
		return GroupStation{Purpose: purpose, Count: count}
	}
	for _, tc := range []struct {
		name string
		g    RoomGroup
		want RoomCrossing
	}{
		{"bedroom", RoomGroup{Stations: []GroupStation{station(PurposeSleep, 2)}}, RoomCrossing{Beds: 2}},
		{"patients and babies sleep too", RoomGroup{Stations: []GroupStation{station(PurposeSleepMed, 1), station(PurposeSleepBaby, 1)}}, RoomCrossing{Beds: 2}},
		{"prisoners and animals do not count", RoomGroup{Stations: []GroupStation{station(PurposeSleepPrison, 3), station(PurposeSleepAnimal, 2)}}, RoomCrossing{}},
		{"cleanliness", RoomGroup{Requires: []EnvRequirement{EnvClean}, Stations: []GroupStation{station(PurposeWork, 1)}}, RoomCrossing{Clean: true}},
		{"cold storage", RoomGroup{Stations: []GroupStation{station(PurposeStoreCold, 1)}}, RoomCrossing{Cold: true}},
		{"cooler", RoomGroup{Stations: []GroupStation{station(PurposeCool, 1), station(PurposeWork, 1)}}, RoomCrossing{Cold: true}},
		{"cold need", RoomGroup{Requires: []EnvRequirement{EnvCold}}, RoomCrossing{Cold: true}},
		{"dining", RoomGroup{Stations: []GroupStation{station(PurposeDine, 2)}}, RoomCrossing{}},
	} {
		if got := crossingOf(tc.g); got != tc.want {
			t.Errorf("%s: %+v, want %+v", tc.name, got, tc.want)
		}
	}
}

// The neighbour a sealed room is joined through is the one whose crossing
// triggers the fewest mechanisms; a cold room is last.
func TestThroughRankOrdersByMechanismsTriggered(t *testing.T) {
	room := func(c RoomCrossing) PlannedRoom { return PlannedRoom{Crossing: &c} }
	ranked := []PlannedRoom{
		room(RoomCrossing{}),
		room(RoomCrossing{Clean: true}),
		room(RoomCrossing{Beds: 4, Clean: true}),
		room(RoomCrossing{Cold: true}),
	}
	for i := 1; i < len(ranked); i++ {
		if !(throughRank(ranked[i-1]) > throughRank(ranked[i])) {
			t.Errorf("rank %d (%d) is not above rank %d (%d)", i-1, throughRank(ranked[i-1]), i, throughRank(ranked[i]))
		}
	}
	if throughRank(room(RoomCrossing{Cold: true, Beds: 1, Clean: true})) != math.MinInt {
		t.Error("a cold room is not last")
	}
	if throughRank(room(RoomCrossing{Beds: 1})) != throughRank(room(RoomCrossing{Clean: true})) {
		t.Error("one mechanism ranks by which it is")
	}
}

// A tiled plan never crosses a cold room, and marks Through only the rooms
// whose crossing costs something.
func TestTiledThroughFollowsTheFacts(t *testing.T) {
	in, _ := tileInput(t, plainScoreSurvey(), 30, true)
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	for _, r := range res.Plan.Rooms {
		if r.Crossing == nil {
			t.Fatalf("room %d carries no crossing facts", r.ID)
		}
		if r.Through && (r.Crossing.Cold || r.Crossing.Mechanisms() == 0) {
			t.Errorf("room %d (%s) is marked through with %+v", r.ID, r.Role, *r.Crossing)
		}
		if r.Role == PlannedFreezer && !r.Crossing.Cold {
			t.Errorf("freezer %d is not cold: %+v", r.ID, *r.Crossing)
		}
	}
}
