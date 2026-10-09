package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func productionFixture(t *testing.T) *o.ColonyFactsSnapshot {
	v := colonyFixture(t).GetObserved()
	issues := v.Issues[:0]
	for _, issue := range v.Issues {
		if issue.GetField() != "farms" && issue.GetField() != "cooking" {
			issues = append(issues, issue)
		}
	}
	v.Issues = issues
	v.Farms = []*o.FarmFacts{{Zone: &c.Ref{Id: proto.String("field")}, Crop: proto.String("Plant_Rice"), EdibleCrop: proto.Bool(true), PlantedCells: proto.Uint32(40), FertilePlantedCells: proto.Uint32(30)}}
	v.Cooking = []*o.CookingFacts{{Bench: &c.Ref{Id: proto.String("stove")}, Usable: proto.Bool(true), Recipes: []*o.RecipeState{{Recipe: &o.DefinitionRef{DefName: proto.String("CookMealSimple")}}}, Bills: []*o.BillState{{Recipe: &o.DefinitionRef{DefName: proto.String("CookMealSimple")}, Suspended: proto.Bool(false)}}}}
	return v
}

func TestColonyProductionBoundary(t *testing.T) {
	for _, change := range []string{"valid", "worked-bill", "duplicate-farm", "count", "nan", "duplicate-recipe", "unreviewed-bill", "conflict"} {
		t.Run(change, func(t *testing.T) {
			v := productionFixture(t)
			switch change {
			case "worked-bill":
				// A worker on the bill reports the ingredients it promised.
				v.Cooking[0].Bills[0].Reservations = []*o.IngredientReservation{{PawnId: proto.String("pawn"), Items: []*o.Quantity{{DefName: proto.String("RawRice"), Units: proto.Int64(10)}}}}
			case "duplicate-farm":
				v.Farms = append(v.Farms, proto.Clone(v.Farms[0]).(*o.FarmFacts))
			case "count":
				v.Farms[0].FertilePlantedCells = proto.Uint32(41)
			case "nan":
				v.Farms[0].GrowthMean = proto.Float64(math.NaN())
			case "duplicate-recipe":
				v.Cooking[0].Recipes = append(v.Cooking[0].Recipes, proto.Clone(v.Cooking[0].Recipes[0]).(*o.RecipeState))
			case "unreviewed-bill":
				v.Cooking[0].Bills[0].CanRunNow = proto.Bool(true)
			case "conflict":
				v.Issues = append(v.Issues, &o.ReadIssue{Field: proto.String("farms"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}})
			}
			err := ValidateColonyFacts(v, v.Context.Identity)
			if (err == nil) != (change == "valid" || change == "worked-bill") {
				t.Fatal(change, err)
			}
		})
	}
}
