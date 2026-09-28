package bridge

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

func TestFishingZoneExtensionNamesZoneAndFloor(t *testing.T) {
	z, err := domain.NewFishingZoneExtension("Zone_7", []domain.Cell{{X: 1, Z: 2}})
	if err != nil {
		t.Fatal(err)
	}
	op := ZoneConfiguration(z)
	if op.GetType() != 4 || op.GetExtendZoneId() != "Zone_7" || op.GetFishing().GetPopulationFloor() != .6 || op.Growing != nil || op.Stockpile != nil {
		t.Fatal(op)
	}
}

func TestCorpseLarderZoneExcludesRottenAndNonAnimalStock(t *testing.T) {
	zone, err := domain.NewFilteredStockpileZone(domain.CorpseLarderFilter(), domain.ImportantPriority, []domain.Cell{{X: 1, Z: 2}})
	if err != nil {
		t.Fatal(err)
	}
	settings := ZoneConfiguration(zone).Stockpile
	if settings.GetPreset() != op.FilterPreset_FILTER_PRESET_NOTHING || len(settings.Filter.Allow) != 2 || settings.Filter.Allow[0].GetCategoryDef() != "CorpsesAnimal" || settings.Filter.Allow[1].GetSpecialFilterDef() != "AllowFresh" || len(settings.Filter.Disallow) != 1 || settings.Filter.Disallow[0].GetSpecialFilterDef() != "AllowRotten" {
		t.Fatal(settings)
	}
	if restored, err := domain.ReconstructZone(zone); err != nil || restored != zone {
		t.Fatal(restored, err)
	}
}

func TestRawFoodZoneAllowsRawMeatAndPlantFoodOnly(t *testing.T) {
	settings := StockpileSettings(domain.RawFoodFilter(), domain.CriticalPriority)
	f := settings.GetFilter()
	if settings.GetPriority() != op.StoragePriority_STORAGE_PRIORITY_CRITICAL || settings.GetPreset() != op.FilterPreset_FILTER_PRESET_NOTHING || len(f.GetAllow()) != 2 || f.Allow[0].GetCategoryDef() != "MeatRaw" || f.Allow[1].GetCategoryDef() != "PlantFoodRaw" || len(f.GetDisallow()) != 1 || f.Disallow[0].GetSpecialFilterDef() != "AllowRotten" || f.HitPointsMin != nil || f.QualityMin != nil {
		t.Fatal(settings)
	}
}

func TestZoneConfigurationBranchesOnKind(t *testing.T) {
	cells := []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}}
	growing, _ := domain.NewZoneCreate(domain.GrowingZone, "Plant_Rice", cells)
	stockpile, _ := domain.NewFilteredStockpileZone(domain.FoodFilter(), domain.ImportantPriority, cells)

	g := ZoneConfiguration(growing)
	if g.GetType() != op.ZoneType_ZONE_TYPE_GROWING || g.Stockpile != nil || g.GetGrowing().GetPlantDef() != "Plant_Rice" || !g.GetGrowing().GetAllowSow() || !g.GetGrowing().GetAllowCut() {
		t.Fatal("unexpected growing zone configuration", g)
	}
	s := ZoneConfiguration(stockpile)
	if s.GetType() != op.ZoneType_ZONE_TYPE_STOCKPILE || s.Growing != nil || s.GetStockpile().GetPriority() != op.StoragePriority_STORAGE_PRIORITY_IMPORTANT || s.GetStockpile().GetPreset() != op.FilterPreset_FILTER_PRESET_FOOD {
		t.Fatal("unexpected stockpile zone configuration", s)
	}
	if s.GetLabel() != "RimGovernor food storage" || g.GetLabel() != "RimGovernor crops" {
		t.Fatal("unexpected zone labels", g.GetLabel(), s.GetLabel())
	}
	allowList, _ := allowListZone(domain.ImportantPriority, []string{"MealSimple", "MealFine"}, cells)
	a := ZoneConfiguration(allowList)
	if a.GetType() != op.ZoneType_ZONE_TYPE_STOCKPILE || a.GetStockpile().GetPreset() != op.FilterPreset_FILTER_PRESET_NOTHING || a.GetStockpile().GetPriority() != op.StoragePriority_STORAGE_PRIORITY_IMPORTANT {
		t.Fatal("unexpected allow-list stockpile configuration", a)
	}
	allow := a.GetStockpile().GetFilter().GetAllow()
	if len(allow) != 2 || allow[0].GetThingDef() != "MealFine" || allow[1].GetThingDef() != "MealSimple" {
		t.Fatal("unexpected allow-list filter selectors", allow)
	}
	if a.GetLabel() != "RimGovernor supplies storage" {
		t.Fatal("unexpected allow-list zone label", a.GetLabel())
	}
	if s.GetStockpile().GetFilter() != nil {
		t.Fatal("food preset must not carry an allow-list filter")
	}
}

// A zone preview native evaluated but refused (littered or occupied ground)
// is a reply the planner moves past to its next candidate, not a contract
// violation; a failure outcome or a missing verdict still rejects (#223).
func TestPreviewZoneReturnsARefusedSiteAsAnEvaluation(t *testing.T) {
	zone, _ := domain.NewFilteredStockpileZone(domain.FoodFilter(), domain.ImportantPriority, []domain.Cell{{X: 1, Z: 1}})
	valid := &op.ZonePreviewReply{Outcome: &op.ZonePreviewReply_Evaluated{Evaluated: &op.ZonePreview{Context: pbContext(), Accepted: proto.Bool(true)}}}
	for _, test := range []struct {
		name     string
		change   func(*op.ZonePreviewReply)
		accepted bool
		ok       bool
	}{
		{"accepted", func(*op.ZonePreviewReply) {}, true, true},
		{"refused site", func(v *op.ZonePreviewReply) { v.GetEvaluated().Accepted = proto.Bool(false) }, false, true},
		{"missing verdict", func(v *op.ZonePreviewReply) { v.GetEvaluated().Accepted = nil }, false, false},
		{"failure outcome", func(v *op.ZonePreviewReply) {
			v.Outcome = &op.ZonePreviewReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum()}}
		}, false, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			reply := proto.Clone(valid).(*op.ZonePreviewReply)
			test.change(reply)
			s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
				if arg.Tool != "rimgovernor/zones_preview" {
					t.Fatal(arg.Tool)
				}
				return pbResult(reply), nil
			}}
			client := testClient(t, s, testBudget)
			got, _, err := client.PreviewZone(context.Background(), pbIdentity(), zone)
			if !test.ok {
				if !errors.Is(err, ErrContract) && !errors.Is(err, ErrRefused) {
					t.Fatal("expected rejection", err)
				}
				return
			}
			if err != nil || got.GetEvaluated().GetAccepted() != test.accepted {
				t.Fatal(got, err)
			}
		})
	}
}
