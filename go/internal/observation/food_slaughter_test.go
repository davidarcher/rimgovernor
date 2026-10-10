package observation

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"google.golang.org/protobuf/proto"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// The slaughter food numbers derive from the race rows (native used to send
// the nutrition, feed and reproduction beside the live pawn): the meat's
// nutrition times the live MeatAmount, the adult feed scaled by the life
// stage's hunger factor for a plant eater, and the egg interval or gestation.
func TestSlaughterFoodDerivesFromTheRaceRows(t *testing.T) {
	catalog := catalogOf(t, "Cow", "Chicken", "Meat_Cow", "Milk", "EggChickenUnfertilized", "EggChickenFertilized")
	races, err := catalog.AnimalRaces()
	if err != nil {
		t.Fatal(err)
	}
	cow, _ := races.Race("Cow")
	adult := int32(len(cow.LifeStages) - 1)
	calf := int32(0)
	pawn := func(id, def string, stage *int32) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), DefName: proto.String(def), Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(1)}}, AnimalState: &o.AnimalState{LifeStageIndex: stage}}
	}
	pawns := bridge.NewPawns(pawn("cow", "Cow", &adult), pawn("calf", "Cow", &calf), pawn("hen", "Chicken", &adult))
	perUnit, _ := cow.MeatNutritionPerUnit.Value()
	adultFeed, _ := cow.AdultFeedPerDay.Value()

	row := slaughterFood(&o.FoodSlaughterAnimal{PawnId: proto.String("cow"), Race: proto.String("Cow"), MeatAmount: proto.Float64(200)}, pawns, races)
	if n, ok := row.MeatNutrition.Value(); !ok || math.Abs(n-200*perUnit) > 1e-9 {
		t.Errorf("meat nutrition %v", row.MeatNutrition)
	}
	if f, ok := row.FeedPerDay.Value(); !ok || math.Abs(f-adultFeed) > 1e-9 {
		t.Errorf("adult feed %v want %v", row.FeedPerDay, adultFeed)
	}
	if days, ok := row.ReproductionDays.Value(); !ok || days != cow.GestationDays || days <= 0 {
		t.Errorf("gestation %v want %v", row.ReproductionDays, cow.GestationDays)
	}
	young := slaughterFood(&o.FoodSlaughterAnimal{PawnId: proto.String("calf"), Race: proto.String("Cow"), MeatAmount: proto.Float64(50)}, pawns, races)
	if f, ok := young.FeedPerDay.Value(); !ok || !(f < adultFeed) {
		t.Errorf("calf feed %v must be under the adult's %v", young.FeedPerDay, adultFeed)
	}
	hen := slaughterFood(&o.FoodSlaughterAnimal{PawnId: proto.String("hen"), Race: proto.String("Chicken"), MeatAmount: proto.Float64(30)}, pawns, races)
	if days, ok := hen.ReproductionDays.Value(); !ok || days <= 0 || days == cow.GestationDays {
		t.Errorf("a hen's reproduction is its egg interval, got %v", hen.ReproductionDays)
	}
	// Meat the colony does not eat sends no amount: nothing is known.
	none := slaughterFood(&o.FoodSlaughterAnimal{PawnId: proto.String("cow"), Race: proto.String("Cow")}, pawns, races)
	if _, ok := none.MeatNutrition.Value(); ok {
		t.Error("an uneaten meat carries nutrition")
	}
	if _, ok := none.FeedPerDay.Value(); ok {
		t.Error("an uneaten meat carries a feed")
	}
}
