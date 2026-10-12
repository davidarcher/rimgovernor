package policy

import (
	"errors"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// passThroughRow is dining, a bedroom and a second bedroom in a row sharing
// walls, joined by link doors: the far bedroom is reached through the near one.
func passThroughRow(through bool) LayoutPlan {
	cell := func(x, z int32) domain.Cell { return domain.Cell{X: x, Z: z} }
	room := func(id int32, role PlannedRole, x int32, door domain.Cell, rot domain.Rotation) PlannedRoom {
		return PlannedRoom{ID: id, Role: role, Crossing: crossingFacts(role), Region: RectRegion(Rectangle{X: x, Z: 20, Width: 4, Height: 4}), Door: door, DoorRot: rot}
	}
	b1 := room(2, PlannedBedroom, 25, cell(24, 21), domain.West)
	b1.Through = through
	return LayoutPlan{
		Rooms: []PlannedRoom{
			room(1, PlannedDining, 20, cell(19, 21), domain.West),
			b1,
			room(3, PlannedBedroom, 30, cell(29, 21), domain.West),
		},
		Links:      []RoomLink{{A: 1, B: 2, Cell: cell(24, 21)}, {A: 2, B: 3, Cell: cell(29, 21)}},
		Entrances:  []domain.Cell{cell(18, 21)},
		NextRoomID: 4,
	}
}

// A bedroom is crossed only when the plan marks it; a room nothing reaches is
// still an error.
func TestCheckRoutesPassThroughAndReachability(t *testing.T) {
	if _, err := CheckRoutes(passThroughRow(true)); err != nil {
		t.Fatalf("marked pass-through rejected: %v", err)
	}
	var through *ThroughfareError
	if _, err := CheckRoutes(passThroughRow(false)); !errors.As(err, &through) || through.Room != 2 {
		t.Fatalf("unmarked bedroom crossed: %v", err)
	}
	cut := passThroughRow(true)
	cut.Links = cut.Links[:1]
	cut.Rooms[2].Door, cut.Rooms[2].DoorRot = domain.Cell{}, domain.East
	if _, err := CheckRoutes(cut); err == nil || !strings.Contains(err.Error(), "no route bedroom -> dining") {
		t.Fatalf("a bedroom with no door is reachable: %v", err)
	}
}

// Through is for a room whose crossing costs something and is not cold; the
// facts decide, not the role.
func TestThroughIsForCostlyUncoldRoomsOnly(t *testing.T) {
	p := passThroughRow(true)
	if !p.Valid() {
		t.Fatal("a bedroom marked through is invalid")
	}
	for name, tc := range map[string]struct {
		crossing *RoomCrossing
		want     bool
	}{
		"beds":              {&RoomCrossing{Beds: 1}, true},
		"cleanliness":       {&RoomCrossing{Clean: true}, true},
		"both":              {&RoomCrossing{Beds: 2, Clean: true}, true},
		"nothing to cost":   {&RoomCrossing{}, false},
		"cold":              {&RoomCrossing{Cold: true}, false},
		"cold with a bed":   {&RoomCrossing{Cold: true, Beds: 1}, false},
		"no facts (legacy)": {nil, false},
	} {
		q := passThroughRow(false)
		q.Rooms[1].Crossing, q.Rooms[1].Through = tc.crossing, true
		if q.Valid() != tc.want {
			t.Errorf("%s marked through: valid %v, want %v", name, q.Valid(), tc.want)
		}
	}
}

// Trips are weighed by the room graph's own frequency, and a plan without any
// keeps the role-pair walk.
func TestTripWalkWeighsRoomTrips(t *testing.T) {
	p := passThroughRow(true)
	base := planWalk(p) // no trips: the role table (bedroom -> dining)
	p.Trips = []RoomTrip{{A: 1, B: 3, Weight: 1}}
	one := planWalk(p)
	p.Trips[0].Weight = 5
	if five := planWalk(p); one == 0 || five != 5*one {
		t.Fatalf("trip walk %d at weight 1, %d at weight 5", one, five)
	}
	p.Trips = []RoomTrip{{A: 1, B: 9, Weight: 5}}
	if got := planWalk(p); got != 0 {
		t.Errorf("a trip to a room the plan lacks costs %d", got)
	}
	p.Trips = nil
	if planWalk(p) != base {
		t.Error("clearing the trips changes the role-pair walk")
	}
	if got := Score(p, plainScoreSurvey()).Walk; got != -planWeights.Walk*base {
		t.Errorf("walk term %d, want %d", got, -planWeights.Walk*base)
	}
}

// The hand plan has no room trips, so the trip term leaves its walk as it was.
func TestHandPlanWalkIsTheRolePairWalk(t *testing.T) {
	p := handPlan(12)
	if len(p.Trips) != 0 {
		t.Fatal("hand plan carries trips")
	}
	if got := planWalk(p); got != 59 {
		t.Errorf("hand plan walk %d, was 59", got)
	}
}

// Without circulation, packed bedrooms with no way out but through each other are walked through
// rather than failing closed.
func TestTilePassesThroughPackedRooms(t *testing.T) {
	in, _ := tileInputCirculation(t, plainScoreSurvey(), 30, true, false)
	res, err := TileEmbedding(in)
	if err != nil {
		t.Fatal(err)
	}
	checkTiled(t, in, res)
	marked := 0
	for _, r := range res.Plan.Rooms {
		if r.Through {
			marked++
			if !r.MayBeThrough() {
				t.Errorf("room %d of role %s is marked through", r.ID, r.Role)
			}
		}
	}
	decided := 0
	for _, d := range res.Decisions {
		if d.Reason == ReasonTilePassThrough {
			decided++
		}
	}
	if marked == 0 || marked != decided {
		t.Fatalf("%d rooms marked through, %d decisions", marked, decided)
	}
	if len(res.Plan.Trips) == 0 {
		t.Fatal("tiled plan carries no room trips")
	}
	if got := Score(res.Plan, in.Survey).Walk; got != -planWeights.Walk*tripWalk(res.Plan) || got == 0 {
		t.Errorf("walk term %d is not the trip walk %d", got, tripWalk(res.Plan))
	}
}
