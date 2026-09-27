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
	in, _ := InteriorRoomFromLayout(room)
	interior, _ := PlanInterior(in, InteriorPieceDefFor(SarcophagusDefinition))
	built := domain.Known(CurrentConstruction{Colony: true, Buildings: []CurrentBuilding{sarcophagus(t, "Sarcophagus_1", interior.Pieces[0])}})
	buried := domain.Known([]WasteItem{{ID: "Corpse_1", State: WasteBuried, CorpseOf: domain.CorpseColonist, Grave: "Sarcophagus_1"}})
	none := domain.Known([]WasteItem{})

	if got, known := WarmTombs(domain.Known(true), domain.Known(plan), domain.Known(rooms), none, built).Value(); !known || len(got) != 0 {
		t.Fatalf("an empty tomb is left warm: %v %v", got, known)
	}
	if got, known := WarmTombs(domain.Known(true), domain.Known(plan), domain.Known(rooms), buried, built).Value(); !known || !reflect.DeepEqual(got, []string{"r1"}) {
		t.Fatalf("a colonist's tomb owes cooling: %v %v", got, known)
	}
	if got, known := WarmTombs(domain.Known(false), domain.Known(plan), domain.Known(rooms), buried, built).Value(); !known || len(got) != 0 {
		t.Fatalf("no cooler research, no cooling: %v %v", got, known)
	}
	rooms.Rooms[0].Temperature = domain.Known(-6.0)
	if got, known := WarmTombs(domain.Known(true), domain.Known(plan), domain.Known(rooms), buried, built).Value(); !known || len(got) != 0 {
		t.Fatalf("a frozen tomb is done: %v %v", got, known)
	}
	rooms.Rooms[0].Temperature = domain.Unknown[float64]()
	if _, known := WarmTombs(domain.Known(true), domain.Known(plan), domain.Known(rooms), buried, built).Value(); known {
		t.Fatal("an unmeasured tomb is unknown")
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
