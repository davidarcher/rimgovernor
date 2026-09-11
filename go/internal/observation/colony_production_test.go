package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestFieldBudgetUsesCropDefinitionsAndPreservesFoodRunway(t *testing.T) {
	v := &o.ColonyFactsSnapshot{NutritionPerDay: proto.Float64(99), Farms: []*o.FarmFacts{{Crop: proto.String("Rice"), EdibleCrop: proto.Bool(true), GrowingCells: proto.Uint32(73)}}}
	p := ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(3)), FoodDays: domain.Known(2.0)}}
	p.FieldCrops = colonyFieldCrops(v, []PlanningDefinition{{Name: "Rice", NutritionDemandPerDay: domain.Known(5.0), GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(1.0)}})
	p.ApplyFieldBudget(7)
	if n, known := p.Facts.FieldCoverage.Value(); !known || n != 1 {
		t.Fatal(p.Facts)
	}
	if n, _ := p.Facts.FoodDays.Value(); n != 2 {
		t.Fatal("crops credited as food")
	}
	p.ApplyFieldBudget(14)
	if n, known := p.Facts.FieldCoverage.Value(); !known || n >= 1 {
		t.Fatal(p.Facts)
	}
	p.FieldCrops = colonyFieldCrops(v, nil)
	p.ApplyFieldBudget(7)
	if _, known := p.Facts.FieldCoverage.Value(); known {
		t.Fatal("missing crop definition certified")
	}
	p.FieldCrops = colonyFieldCrops(v, []PlanningDefinition{{Name: "Rice", GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(1.0)}})
	p.ApplyFieldBudget(7)
	if _, known := p.Facts.FieldCoverage.Value(); known {
		t.Fatal("human demand substituted for unknown competing-animal demand")
	}
}

func TestProductionFactsRequireEdibleGrowingCellsAndActiveFoodBills(t *testing.T) {
	for _, change := range []string{"ready", "suspended", "no-bills", "not-food", "unusable", "unknown-bill", "unknown-bench", "unknown-farm"} {
		t.Run(change, func(t *testing.T) {
			v := &o.ColonyFactsSnapshot{Farms: []*o.FarmFacts{{EdibleCrop: proto.Bool(true), GrowingCells: proto.Uint32(30)}, {EdibleCrop: proto.Bool(false), GrowingCells: proto.Uint32(99)}}, Cooking: []*o.CookingFacts{{Usable: proto.Bool(true), Recipes: []*o.RecipeState{{Recipe: &o.DefinitionRef{DefName: proto.String("Meal")}}}, Bills: []*o.BillState{{Recipe: &o.DefinitionRef{DefName: proto.String("Meal")}, Suspended: proto.Bool(false)}}}}}
			switch change {
			case "suspended":
				v.Cooking[0].Bills[0].Suspended = proto.Bool(true)
			case "no-bills":
				v.Cooking[0].Bills = nil
			case "not-food":
				v.Cooking[0].Bills[0].Recipe.DefName = proto.String("Stone")
			case "unusable":
				v.Cooking[0].Usable = proto.Bool(false)
			case "unknown-bill":
				v.Cooking[0].Bills[0].Suspended = nil
			case "unknown-bench":
				v.Cooking[0].Usable = nil
			case "unknown-farm":
				v.Farms[0].GrowingCells = nil
			}
			f := policy.RoutineFacts{Colonists: domain.Known(int64(3))}
			colonyProduction(v, &f)
			cooking, known := f.Cooking.Value()
			if known != (change != "unknown-bill" && change != "unknown-bench") || cooking != (change == "ready" || change == "unknown-farm") {
				t.Fatal(f.Cooking)
			}
			growing, known := f.GrowingCells.Value()
			if known != (change != "unknown-farm") || known && growing != 30 {
				t.Fatal(f.GrowingCells)
			}
			needs, err := policy.DetectRoutine(f, policy.RoutineLatches{}, policy.DefaultRoutinePolicy())
			if err != nil {
				t.Fatal(err)
			}
			if got, known := needs.Gates.Production.Value(); change != "unknown-farm" && (!known || !got) {
				t.Fatal(needs.Gates)
			}
		})
	}
}
