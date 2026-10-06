package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// penHayProjection is one pen with pasture that stops in the cold season and
// the haygrass definition, under the given calendar.
func penHayProjection(cal domain.Fact[policy.Calendar], demand domain.Fact[float64]) observation.ColonyProjection {
	var p observation.ColonyProjection
	p.Facts.Calendar = cal
	p.Facts.PenGrazing = domain.Known([]policy.PenGrazing{{ID: "pen", DemandPerDay: demand, PasturePerDay: domain.Known(0.0), StoredNutrition: domain.Known(0.0)}})
	p.Definitions = []observation.PlanningDefinition{{Name: "Plant_Haygrass", Available: domain.Known(true), GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(0.5), HarvestedThingDef: domain.Known("Hay")}}
	p.CropClimate = policy.CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(30.0), OutdoorsDark: domain.Known(false)}
	return p
}

func TestHayStockFollowsGrowthStopSeason(t *testing.T) {
	cold := domain.Known(policy.Calendar{Season: "Summer", DayOfYear: 20, GrowingDays: 40, GrowingDaysRemaining: 10, NonGrowingDays: 30, Sowing: true})
	temperate := domain.Known(policy.Calendar{Season: "Summer", DayOfYear: 20, GrowingDays: 60, GrowingDaysRemaining: 10, Sowing: true})
	for _, tc := range []struct {
		name   string
		cal    domain.Fact[policy.Calendar]
		demand domain.Fact[float64]
		want   bool
	}{
		{"cold biome plans hay", cold, domain.Known(4.0), true},
		{"year-round growth plans none", temperate, domain.Known(4.0), false},
		{"unknown calendar adds nothing", domain.Unknown[policy.Calendar](), domain.Known(4.0), false},
		{"unknown pen demand adds nothing", cold, domain.Unknown[float64](), false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			p := penHayProjection(tc.cal, tc.demand)
			opt, ok := hayShortfall(p)
			if ok != tc.want || ok && opt.Needed <= 0 {
				t.Fatalf("shortfall %+v ok=%v", opt, ok)
			}
			if _, n, ok := hayTarget(p); ok != tc.want || ok && n <= 0 {
				t.Fatalf("target %d ok=%v", n, ok)
			}
		})
	}
}
