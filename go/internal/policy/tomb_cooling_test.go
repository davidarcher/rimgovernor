package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestWarmTombsOnlyWhileAColonistLiesThere(t *testing.T) {
	plan, room := tombFixture()
	rooms := tombStanding(room)
	rooms.Rooms[0].Temperature = domain.Known(18.0)
	in, _ := InteriorRoomFromLayout(room, testShapes)
	interior, _ := PlanInterior(in, testShapes.Defs[testSarcophagus])
	built := domain.Known(CurrentConstruction{Colony: true, Buildings: []CurrentBuilding{sarcophagus(t, "Sarcophagus_1", interior.Pieces[0])}})
	buried := domain.Known([]WasteItem{{ID: "Corpse_1", State: WasteBuried, CorpseOf: domain.CorpseColonist, Grave: "Sarcophagus_1"}})
	none := domain.Known([]WasteItem{})

	if got, known := WarmTombs(testShapes, domain.Known(true), domain.Known(plan), domain.Known(rooms), none, built).Value(); !known || len(got) != 0 {
		t.Fatalf("an empty tomb is left warm: %v %v", got, known)
	}
	if got, known := WarmTombs(testShapes, domain.Known(true), domain.Known(plan), domain.Known(rooms), buried, built).Value(); !known || !reflect.DeepEqual(got, []string{"r1"}) {
		t.Fatalf("a colonist's tomb owes cooling: %v %v", got, known)
	}
	if got, known := WarmTombs(testShapes, domain.Known(false), domain.Known(plan), domain.Known(rooms), buried, built).Value(); !known || len(got) != 0 {
		t.Fatalf("no cooler research, no cooling: %v %v", got, known)
	}
	rooms.Rooms[0].Temperature = domain.Known(-6.0)
	if got, known := WarmTombs(testShapes, domain.Known(true), domain.Known(plan), domain.Known(rooms), buried, built).Value(); !known || len(got) != 0 {
		t.Fatalf("a frozen tomb is done: %v %v", got, known)
	}
	rooms.Rooms[0].Temperature = domain.Unknown[float64]()
	if _, known := WarmTombs(testShapes, domain.Known(true), domain.Known(plan), domain.Known(rooms), buried, built).Value(); known {
		t.Fatal("an unmeasured tomb is unknown")
	}
}

// The meal closet (#936) is owed a shell while its dining room stands, and
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
	built := domain.Known(CurrentConstruction{Colony: true})
	none := domain.Known([]WasteItem{})
	if got, known := WarmTombs(testShapes, domain.Known(true), domain.Known(plan), domain.Known(rooms), none, built).Value(); !known || !reflect.DeepEqual(got, []string{"r2"}) {
		t.Fatalf("warm closet not cooled: %v %v", got, known)
	}
}

func TestRefrigerationReviewTakesWarmTombs(t *testing.T) {
	r := RefrigerationReview{Active: true, Rooms: []string{"r9"}, WarmNutrition: domain.Known(6.0)}
	got := r.WithTombs(domain.Known([]string{"r1"}))
	if !got.Active || !reflect.DeepEqual(got.Rooms, []string{"r1", "r9"}) || !reflect.DeepEqual(got.Tombs, []string{"r1"}) {
		t.Fatalf("%+v", got)
	}
	idle := RefrigerationReview{WarmNutrition: domain.Known(0.0)}
	if got := idle.WithTombs(domain.Unknown[[]string]()); got.Active {
		t.Fatal("unknown tombs change nothing")
	}
	if got := idle.WithTombs(domain.Known([]string{"r1"})); !got.Active {
		t.Fatal("a warm tomb alone activates refrigeration")
	}
}
