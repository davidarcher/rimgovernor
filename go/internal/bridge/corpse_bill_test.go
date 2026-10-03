package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func TestBillIntentCarriesTheCorpseClass(t *testing.T) {
	forever := func(class c.CorpseClass) *op.BillSettings {
		return &op.BillSettings{RepeatMode: op.RepeatMode_REPEAT_MODE_FOREVER.Enum(), Suspended: proto.Bool(false), IngredientSearchRadius: proto.Float32(40), Store: &op.BillStore{Destination: &op.BillStore_Mode{Mode: op.StoreMode_STORE_MODE_DROP_ON_FLOOR}}, CorpseClass: class.Enum()}
	}
	cremate, err := domain.NewCremationBill("crematorium", "CremateCorpse", domain.CorpseStranger, "")
	if err != nil {
		t.Fatal(err)
	}
	add := billIntent(cremate)
	if add.GetRecipeDef() != "CremateCorpse" || !proto.Equal(add.GetSettings(), forever(c.CorpseClass_CORPSE_CLASS_STRANGER)) {
		t.Fatalf("cremation settings: %v", add)
	}
	// An animal cremation bill excludes fresh corpses (#1810).
	spoiled, _ := domain.NewCremationBill("crematorium", "CremateCorpse", domain.CorpseAnimal, domain.RotRotting)
	want := forever(c.CorpseClass_CORPSE_CLASS_ANIMAL)
	want.MinRotStage = c.RotStage_ROT_STAGE_ROTTING.Enum()
	if s := billIntent(spoiled).GetSettings(); !proto.Equal(s, want) {
		t.Fatalf("animal cremation settings: %v", s)
	}
	if s := billIntent(cremate).GetSettings(); s.MinRotStage != nil {
		t.Fatalf("stranger cremation gained a minimum: %v", s)
	}
	// Every butcher bill names its class: animal, or stranger when pinned.
	butcher, _ := domain.NewProductionBill("table", "ButcherCorpseFlesh", domain.ButcherForever, 0)
	if s := billIntent(butcher).GetSettings(); !proto.Equal(s, forever(c.CorpseClass_CORPSE_CLASS_ANIMAL)) {
		t.Fatalf("butcher settings: %v", s)
	}
	human, _ := domain.NewHumanButcherBill("table", "ButcherCorpseFlesh", "cook")
	if s := billIntent(human).GetSettings(); s.GetCorpseClass() != c.CorpseClass_CORPSE_CLASS_STRANGER || s.GetWorker().GetEntityId() != "cook" {
		t.Fatalf("human butcher settings: %v", s)
	}
	// Ordinary bills carry none.
	meal, _ := domain.NewProductionBill("stove", "CookMealSimple", domain.FoodTarget, 5)
	if s := billIntent(meal).GetSettings(); s.CorpseClass != nil {
		t.Fatalf("meal gained a corpse class: %v", s)
	}
}
