package buildingruntime

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// benchInputs are the benches consuming stored inputs (#1775), read from
// the bench census and the built-building census for each bench's cell. The
// kitchen's and butcher's benches (the projection's production benches) are
// left to the food stores; with that census or the building census
// unobserved no bench is planned.
func benchInputs(census []bridge.GearBenchRead, projection *observation.ColonyProjection) []policy.BenchInput {
	built, builtKnown := projection.Facts.CurrentConstruction.Value()
	food, foodKnown := projection.ProductionBenches.Value()
	if !builtKnown || !built.Colony || !foodKnown {
		return nil
	}
	at := make(map[string]domain.Cell, len(built.Buildings))
	for _, b := range built.Buildings {
		at[b.ID] = b.Building.Cell()
	}
	excluded := make(map[string]bool, len(food))
	for _, b := range food {
		excluded[b.ID] = true
	}
	benches := make([]policy.GearBench, 0, len(census))
	for _, row := range census {
		benches = append(benches, row.Bench)
	}
	return policy.DeriveBenchInputs(benches, at, excluded)
}

// An ingredients:<benchID> stockpile retires once the bench census no
// longer lists its bench; its allow-list stays the creating planner's, so
// the role otherwise publishes nothing.
func init() {
	RegisterStockpileRole("ingredients", func(in StockpileRoleInput, role string) (policy.StockpileRoleState, bool) {
		_, bench, _ := strings.Cut(role, ":")
		standing, known := in.Benches.Value()
		if !known || bench == "" || standing[bench] {
			return policy.StockpileRoleState{}, false
		}
		return policy.StockpileRoleState{Retired: true}, true
	})
}
