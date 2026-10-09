package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestWarmCoolingRoomsOweCoolingEmptyOrNot(t *testing.T) {
	plan, room := tombFixture()
	rooms := tombStanding(room)
	rooms.Rooms[0].Temperature = domain.Known(18.0)
	if got, known := WarmCoolingRooms(domain.Known(true), domain.Known(plan), domain.Known(rooms)).Value(); !known || !reflect.DeepEqual(got, []string{"r1"}) {
		t.Fatalf("an empty warm tomb owes cooling: %v %v", got, known)
	}
	if got, known := WarmCoolingRooms(domain.Known(false), domain.Known(plan), domain.Known(rooms)).Value(); !known || len(got) != 0 {
		t.Fatalf("no cooler research, no cooling: %v %v", got, known)
	}
	if _, known := WarmCoolingRooms(domain.Unknown[bool](), domain.Known(plan), domain.Known(rooms)).Value(); known {
		t.Fatal("unknown coolers are unknown")
	}
	rooms.Rooms[0].Temperature = domain.Known(-6.0)
	if got, known := WarmCoolingRooms(domain.Known(true), domain.Known(plan), domain.Known(rooms)).Value(); !known || len(got) != 0 {
		t.Fatalf("a frozen tomb is done: %v %v", got, known)
	}
	rooms.Rooms[0].Temperature = domain.Unknown[float64]()
	if _, known := WarmCoolingRooms(domain.Known(true), domain.Known(plan), domain.Known(rooms)).Value(); known {
		t.Fatal("an unmeasured tomb is unknown")
	}
	if got, known := WarmCoolingRooms(domain.Known(true), domain.Known(plan), domain.Known(RoomObservation{})).Value(); !known || len(got) != 0 {
		t.Fatalf("an unbuilt room owes nothing: %v %v", got, known)
	}
}

// The meal closet is owed a shell while its dining room stands, and
// once it stands it is cooled like a filled tomb, empty or not.
func TestMealClosetOwedThenCooled(t *testing.T) {
	dining := PlannedRoom{Role: PlannedDining, Interior: Rectangle{X: 10, Z: 20, Width: 9, Height: 7}, Door: domain.Cell{X: 14, Z: 19}, DoorRot: domain.North}
	closet := PlannedRoom{Role: PlannedMealCloset, Interior: Rectangle{X: 13, Z: 28, Width: 2, Height: 2}, Door: domain.Cell{X: 14, Z: 27}, DoorRot: domain.North}
	plan := LayoutPlan{Rooms: []PlannedRoom{dining, closet}}
	if _, owed := plan.MealClosetOwed(RoomObservation{Shapes: testShapes}); owed {
		t.Fatal("closet owed before its dining room stands")
	}
	rooms := tombStanding(dining)
	if got, owed := plan.MealClosetOwed(rooms); !owed || !got.Same(closet) {
		t.Fatalf("closet not owed beside a standing dining room: %+v %v", got, owed)
	}
	standing := tombStanding(closet)
	standing.Rooms[0].ID, standing.Rooms[0].Temperature = "r2", domain.Known(12.0)
	rooms.Rooms = append(rooms.Rooms, standing.Rooms[0])
	if _, owed := plan.MealClosetOwed(rooms); owed {
		t.Fatal("standing closet still owed")
	}
	if got, known := WarmCoolingRooms(domain.Known(true), domain.Known(plan), domain.Known(rooms)).Value(); !known || !reflect.DeepEqual(got, []string{"r2"}) {
		t.Fatalf("warm closet not cooled: %v %v", got, known)
	}
}

func TestRefrigerationReviewTakesWarmRooms(t *testing.T) {
	r := RefrigerationReview{Active: true, Rooms: []string{"r9"}, WarmNutrition: domain.Known(6.0)}
	got := r.WithWarmRooms(domain.Known([]string{"r1"}))
	if !got.Active || !reflect.DeepEqual(got.Rooms, []string{"r1", "r9"}) || !reflect.DeepEqual(got.Warm, []string{"r1"}) {
		t.Fatalf("%+v", got)
	}
	idle := RefrigerationReview{WarmNutrition: domain.Known(0.0)}
	if got := idle.WithWarmRooms(domain.Unknown[[]string]()); got.Active {
		t.Fatal("unknown tombs change nothing")
	}
	if got := idle.WithWarmRooms(domain.Known([]string{"r1"})); !got.Active {
		t.Fatal("a warm tomb alone activates refrigeration")
	}
}
