package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func componentRequest() ResourceMethodRequest {
	history := ResourceHistory{End: 60000, Uses: []ResourceUse{
		{Tick: 60000, Resource: "Steel", Count: domain.Known(int64(10))},
		{Tick: 60000, Resource: ComponentResource, Count: domain.Known(int64(4))},
	}}
	recipe := GearRecipe{Definition: "MakeComponent", Products: []Resource{ComponentResource}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]Amount{{{Resource: "Steel", Count: 12}}}), RequiredWork: domain.Known([]WorkRequirement{})}
	return ResourceMethodRequest{
		Resource: ComponentResource, Target: 20,
		Benches:      domain.Known([]GearBench{{ID: "fabrication", Bills: domain.Known([]GearBill{}), Recipes: domain.Known([]GearRecipe{recipe})}}),
		Stock:        []Stock{{Resource: "Steel", Available: domain.Known(int64(157))}},
		CurrentStock: domain.Known([]Amount{{Resource: "Steel", Count: 157}, {Resource: ComponentResource, Count: 2}}),
		Runways: []ResourceRunway{
			ForecastResourceRunway("Steel", domain.Known(int64(157)), domain.Known(int64(0)), 70, history),
			ForecastResourceRunway(ComponentResource, domain.Known(int64(2)), domain.Known(int64(0)), 0, history),
		},
	}
}

func TestComponentFabricationProtectsSteelRunway(t *testing.T) {
	for _, tc := range []struct {
		name   string
		change func(*ResourceMethodRequest)
		kind   ResourceMethodKind
		target int64
	}{
		{"partial target", func(r *ResourceMethodRequest) {}, ResourceMethodProduce, 5},
		{"full target", func(r *ResourceMethodRequest) { r.Target = 4 }, ResourceMethodProduce, 4},
		{"one component boundary", func(r *ResourceMethodRequest) { r.Stock[0].Available = domain.Known(int64(132)) }, ResourceMethodProduce, 3},
		{"less than twelve spare", func(r *ResourceMethodRequest) { r.Stock[0].Available = domain.Known(int64(131)) }, ResourceMethodBlocked, 0},
		{"fresh stock fell", func(r *ResourceMethodRequest) {
			r.CurrentStock = domain.Known([]Amount{{Resource: "Steel", Count: 120}})
		}, ResourceMethodBlocked, 0},
		{"ore cannot fund bills", func(r *ResourceMethodRequest) {
			r.Stock[0].Available = domain.Known(int64(120))
			r.Runways[0].SurfaceOre = domain.Known(int64(10000))
		}, ResourceMethodBlocked, 0},
		{"unknown rate", func(r *ResourceMethodRequest) { r.Runways[0].ConsumptionPerDay = domain.Unknown[float64]() }, ResourceMethodUnknown, 0},
		{"unknown ingredient stock", func(r *ResourceMethodRequest) { r.Stock[0].Available = domain.Unknown[int64]() }, ResourceMethodUnknown, 0},
		{"unknown current stock", func(r *ResourceMethodRequest) { r.CurrentStock = domain.Unknown[[]Amount]() }, ResourceMethodUnknown, 0},
		{"unknown deficit", func(r *ResourceMethodRequest) { r.Runways[1].Deficit = domain.Unknown[bool]() }, ResourceMethodUnknown, 0},
		{"no deficit", func(r *ResourceMethodRequest) { r.Runways[1].Deficit = domain.Known(false) }, ResourceMethodBlocked, 0},
		{"missing steel forecast", func(r *ResourceMethodRequest) { r.Runways = r.Runways[1:] }, ResourceMethodUnknown, 0},
		{"zero rate retains reserve", func(r *ResourceMethodRequest) { r.Runways[0].ConsumptionPerDay = domain.Known(0.0) }, ResourceMethodProduce, 9},
		{"fractional demand rounds up", func(r *ResourceMethodRequest) {
			r.Runways[0].ConsumptionPerDay = domain.Known(10.3)
			r.Stock[0].Available = domain.Known(int64(133))
		}, ResourceMethodBlocked, 0},
		{"bench missing", func(r *ResourceMethodRequest) { r.Benches = domain.Known([]GearBench{}) }, ResourceMethodBlocked, 0},
		{"research gated", func(r *ResourceMethodRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].Available = domain.Known(false)
		}, ResourceMethodBlocked, 0},
		{"bench unavailable", func(r *ResourceMethodRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].AvailableOn = domain.Known(false)
		}, ResourceMethodBlocked, 0},
		{"active bill", func(r *ResourceMethodRequest) {
			b, _ := r.Benches.Value()
			b[0].Bills = domain.Known([]GearBill{{Products: []Resource{ComponentResource}, Active: domain.Known(true)}})
		}, ResourceMethodWait, 0},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := componentRequest()
			tc.change(&r)
			got, err := SelectResourceMethod(r)
			if err != nil || got.Kind != tc.kind || got.Target != tc.target {
				t.Fatalf("got %+v, %v; want %s target %d", got, err, tc.kind, tc.target)
			}
			if got.Kind == ResourceMethodProduce {
				if got.Recipe != "MakeComponent" || got.Bench != "fabrication" {
					t.Fatal(got)
				}
				r.Seen = []domain.MethodID{got.ID}
				again, err := SelectResourceMethod(r)
				if err != nil || again.Kind != ResourceMethodWait {
					t.Fatal(again, err)
				}
			}
		})
	}
}
