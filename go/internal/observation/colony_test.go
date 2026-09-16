package observation

import (
	"context"
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"math"
	"os"
	"path/filepath"
	"testing"
)

func TestColonyProjectionKeepsRawFoodAndUnknownGeometryOutOfPolicy(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	expected := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	p, err := DecodeColony(r, expected)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := p.Facts.FoodDays.Value(); known {
		t.Fatal("raw runway became forecast")
	}
	if wood, known := p.Facts.Wood.Value(); !known || wood != 40 {
		t.Fatal(p)
	}
	if roof, known := p.Cells[0].Roofed.Value(); !known || roof {
		t.Fatal("native no-roof not preserved")
	}
	if occupied, known := p.Cells[0].Occupied.Value(); !known || occupied {
		t.Fatal("known free cell lost")
	}
	for _, inside := range []*bool{proto.Bool(true), proto.Bool(false), nil} {
		r.GetObserved().Planning.GetObserved().Cells.Cells[0].Indoors = inside
		projected, err := DecodeColony(r, expected)
		if err != nil {
			t.Fatal(err)
		}
		value, known := projected.Cells[0].Indoors.Value()
		if known != (inside != nil) || inside != nil && value != *inside {
			t.Fatal("indoor fact inferred from roof", value, known)
		}
	}
	r.GetObserved().Planning.GetObserved().Cells.Cells[0].Occupied = nil
	p, err = DecodeColony(r, expected)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := p.Cells[0].Occupied.Value(); known {
		t.Fatal("missing occupancy became empty")
	}
	r.GetObserved().Resources[0].Units = nil
	p, err = DecodeColony(r, expected)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := p.Facts.Wood.Value(); known {
		t.Fatal("missing stock count became zero")
	}
	r.GetObserved().Context.Tick = proto.Int64(8)
	r.GetObserved().Planning.GetObserved().Cells.Context.Tick = proto.Int64(8)
	if _, err = DecodeColony(r, expected); err == nil {
		t.Fatal("mixed review tick accepted")
	}
}

// Set this to a retained official ProtoJSON payload from native acceptance.
// This proves the actual C# projection reaches durable Go review unchanged.
func TestColonyNativeCaptureReachesRoutineReview(t *testing.T) {
	path := os.Getenv("RIMGOVERNOR_NATIVE_COLONY_CAPTURE")
	if path == "" {
		t.Skip("requires retained native colony acceptance payload")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	identity, err := contextIdentity(r.GetObserved().Context)
	if err != nil {
		t.Fatal(err)
	}
	p, err := DecodeColony(r, identity)
	if err != nil {
		t.Fatal(err)
	}
	if len(p.Cells) == 0 || len(p.Definitions) == 0 {
		t.Fatal("missing native planning data")
	}
	if reference := os.Getenv("RIMGOVERNOR_NATIVE_PRODUCTION_REFERENCE"); reference != "" {
		p.ApplyFieldBudget(policy.DefaultRoutinePolicy().FoodTargetDays)
		data, err := os.ReadFile(reference)
		if err != nil {
			t.Fatal(err)
		}
		var want struct {
			GrowingCells  int64    `json:"growing_cells"`
			CookingReady  bool     `json:"cooking_ready"`
			FieldCoverage *float64 `json:"field_coverage"`
		}
		if err = json.Unmarshal(data, &want); err != nil {
			t.Fatal(err)
		}
		growing, known := p.Facts.GrowingCells.Value()
		if want.FieldCoverage != nil {
			coverage, known := p.Facts.FieldCoverage.Value()
			if !known || math.Abs(coverage-*want.FieldCoverage) > 1e-9 {
				t.Fatal("native field coverage parity", p.Facts.FieldCoverage, *want.FieldCoverage)
			}
			t.Logf("Native field coverage matches Python: %.9g", coverage)
		}
		if !known || growing != want.GrowingCells {
			t.Fatal("native growing-cell parity", p.Facts.GrowingCells, want)
		}
		cooking, known := p.Facts.Cooking.Value()
		if !known || cooking != want.CookingReady {
			t.Fatal("native cooking parity", p.Facts.Cooking, want)
		}
		t.Logf("Native production reaches routine facts: %d growing cells, cooking ready=%v", growing, cooking)
	}
	if reference := os.Getenv("RIMGOVERNOR_NATIVE_FOOD_FORECAST"); reference != "" {
		food, known := p.FoodSupply.Value()
		if !known {
			t.Fatal("native food supply missing")
		}
		forecast, err := policy.ForecastFood(food, nil)
		if err != nil {
			t.Fatal(err)
		}
		data, err := os.ReadFile(reference)
		if err != nil {
			t.Fatal(err)
		}
		var want struct {
			Readable                                                         bool
			RunwayDays, UsableNutrition, AtRiskNutrition, InventoryNutrition float64
		}
		if err = json.Unmarshal(data, &want); err != nil || !want.Readable {
			t.Fatal("invalid native reference forecast", err)
		}
		days, known := forecast.RunwayDays.Value()
		if !known {
			t.Fatal("missing native food runway")
		}
		for _, pair := range [][2]float64{{days, want.RunwayDays}, {forecast.UsableNutrition, want.UsableNutrition}, {forecast.AtRiskNutrition, want.AtRiskNutrition}, {forecast.InventoryNutrition, want.InventoryNutrition}} {
			if math.Abs(pair[0]-pair[1]) > 1e-6*math.Max(1, math.Abs(pair[1])) {
				t.Fatal("Go/Python native food forecast mismatch", pair)
			}
		}
		t.Logf("Native food forecast matches Python: runway %.9g days", days)
	}
	if reference := os.Getenv("RIMGOVERNOR_NATIVE_COMBINED_FOOD_FORECAST"); reference != "" {
		data, err := os.ReadFile(reference)
		if err != nil {
			t.Fatal(err)
		}
		var want struct {
			Readable   bool
			RunwayDays float64
		}
		if err = json.Unmarshal(data, &want); err != nil || !want.Readable {
			t.Fatal("invalid combined reference", err)
		}
		days, known := p.Facts.FoodDays.Value()
		if !known || math.Abs(days-want.RunwayDays) > 1e-6*math.Max(1, math.Abs(want.RunwayDays)) {
			t.Fatal("combined forecast did not reach routine facts", p.Facts.FoodDays, want)
		}
		t.Logf("Native combined forecast reaches routine FoodDays: %.9g", days)
	}
	s, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "native-review.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	current := domain.GenerationSnapshot{Colony: identity.Colony, Load: identity.Load, Map: identity.Map, Plan: "native-review", Revision: 1, Native: 1}
	out, err := s.ReviewRoutine(context.Background(), store.RoutineReviewRequest{Current: current, Tick: identity.Tick, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: p.Facts})
	if err != nil {
		t.Fatal(err)
	}
	if len(out.Goals) != 16 {
		t.Fatal("native facts did not reach maintained goals")
	}
	for _, assessment := range out.Needs.Assessments {
		if assessment.ID == policy.ConfirmColonyNames {
			if expected := os.Getenv("RIMGOVERNOR_NATIVE_NAMING"); expected != "" {
				naming, known := p.Facts.ColonyNaming.Value()
				want := domain.NeedRecovered
				if expected == "present" {
					want = domain.NeedDeficit
				}
				if !known || naming != (expected == "present") || assessment.Need != want {
					t.Fatal("native naming did not reach durable review", p.Facts.ColonyNaming, assessment)
				}
				t.Logf("Native naming %s reaches durable need %s", expected, want)
			}
		}
		if assessment.ID == policy.EnsureCooking {
			if ready, known := p.Facts.Cooking.Value(); known {
				want := domain.NeedDeficit
				if ready {
					want = domain.NeedRecovered
				}
				if assessment.Need != want {
					t.Fatal("native cooking did not reach durable need", assessment)
				}
			}
		}
		if assessment.ID == policy.EnsureFoodSupply {
			days, known := p.Facts.FoodDays.Value()
			if !known && assessment.Need != domain.NeedUnknown {
				t.Fatal("raw native runway certified food need", assessment)
			}
			if known && days < policy.DefaultRoutinePolicy().FoodMinDays && assessment.Need != domain.NeedDeficit {
				t.Fatal("forecast shortage did not create deficit", assessment)
			}
		}
	}
	t.Logf("Native core and %d cells/%d definitions reached durable routine review", len(p.Cells), len(p.Definitions))
}
