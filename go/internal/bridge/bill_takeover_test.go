package bridge

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestProductionCensusAcceptsBillDriftFacts(t *testing.T) {
	v := productionFixture(t)
	bill := v.Cooking[0].Bills[0]
	bill.DefaultIngredients = proto.Bool(false)
	bill.UnrestrictedWorker = proto.Bool(false)
	bill.WorkerId = proto.String("pawn")
	bill.IngredientFilter = &o.StockpileFilter{AllowedDefNames: []string{"Rice"}}
	if err := ValidateColonyFacts(v, v.Context.Identity); err != nil {
		t.Fatal(err)
	}
	bill.IngredientFilter.AllowedDefNames = append(bill.IngredientFilter.AllowedDefNames, "Rice")
	if err := ValidateColonyFacts(v, v.Context.Identity); err == nil {
		t.Fatal("duplicate ingredient accepted")
	}
}
