package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestUnsownFieldCapacityDoesNotBecomeGrowingFood(t *testing.T) {
	v := &o.ColonyFactsSnapshot{Farms: []*o.FarmFacts{{Crop: proto.String("Plant_Rice"), EdibleCrop: proto.Bool(true), GrowingCells: proto.Uint32(0), UsableCells: proto.Uint32(73)}}}
	definitions := []PlanningDefinition{{Name: "Plant_Rice", GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(1.0), NutritionDemandPerDay: domain.Known(5.0)}}
	growing := policy.FieldCoverage(domain.Known(int64(3)), colonyFieldCrops(v.Farms, definitions), 7)
	capacity := policy.FieldCoverage(domain.Known(int64(3)), colonyFieldCrops(v.Farms, definitions, true), 7)
	if growing != domain.Known(0.0) || capacity != domain.Known(1.0) {
		t.Fatal(growing, capacity)
	}
	v.Farms[0].UsableCells = nil
	if _, known := policy.FieldCoverage(domain.Known(int64(3)), colonyFieldCrops(v.Farms, definitions, true), 7).Value(); known {
		t.Fatal("unknown soil credited")
	}
}
