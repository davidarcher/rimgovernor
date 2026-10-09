package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestResourceLaborWorkerSources(t *testing.T) {
	for _, tc := range []struct {
		name    string
		workers domain.Fact[int]
		pawns   domain.Fact[[]policy.WorkPawn]
		want    float64
		known   bool
	}{
		{"both unknown", domain.Unknown[int](), domain.Unknown[[]policy.WorkPawn](), 0, false},
		{"aggregate fallback", domain.Known(2), domain.Unknown[[]policy.WorkPawn](), 40000, true},
		{"known empty pawns", domain.Known(2), domain.Known([]policy.WorkPawn{}), 0, true},
		{"authoritative pawns", domain.Known(2), domain.Known([]policy.WorkPawn{{Available: domain.Known(true), Applies: domain.Known(true)}}), 20000, true},
		{"incomplete pawn facts", domain.Known(2), domain.Known([]policy.WorkPawn{{}}), 0, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			projection := observation.ColonyProjection{Workers: tc.workers, WorkPawns: tc.pawns}
			projection.Facts.FoodPlan = domain.Known(policy.FoodPlan{})
			got, err := resourceLabor(projection)
			value, known := got.Value()
			if err != nil || known != tc.known || known && value != tc.want {
				t.Fatalf("got %v/%v %v", value, known, err)
			}
			c := policy.MineCandidates("Steel", []policy.ResourceSource{{ThingID: "ore", Yield: 100, Distance: 10, Method: policy.ResourceSourceMine, Safety: "open_surface"}}, domain.Known(int64(500)))
			plan, err := policy.PlanResourceSupply([]policy.ResourceSupplyInput{{Resource: "Steel", Deficit: 100, Candidates: c}}, got)
			if err != nil {
				t.Fatal(err)
			}
			if !known && (len(plan.Opened("Steel")) != 0 || plan.Plan.Portfolio[0].Reason != "unknown_capacity") {
				t.Fatal(plan.Plan.Explain())
			}
		})
	}
}
