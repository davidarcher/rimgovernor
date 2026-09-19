package policy

import (
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"sort"
)

// AcquisitionSource is an observed native-approved source, not inventory.
// Definition is the source's own native definition name (the plant or the
// animal, not the harvested resource); a hunt row of a recognised pest
// definition (PestDefinition) is a pest hunt: food false, no nutrition.
type AcquisitionSource struct {
	ID, Resource, Token          string
	Definition                   string
	Cell                         domain.Cell
	Tree, Food, Designated, Hunt bool
	Yield, NutritionYield        float64
}

// SelectAcquisition retains native distance ordering. Pending yield and unresolved
// sources prevent duplicate work but never count as recovered stock.
func SelectAcquisition(sources domain.Fact[[]AcquisitionSource], deficit, pending domain.Fact[float64], food bool, held map[string]bool, huntSlots ...domain.Fact[int]) ([]AcquisitionSource, error) {
	accept := func(row AcquisitionSource) (float64, bool) {
		if food {
			return row.NutritionYield, row.Food && row.NutritionYield > 0
		}
		return row.Yield, row.Tree && row.Resource == "WoodLog"
	}
	return selectAcquisition(sources, deficit, pending, accept, held, huntSlots...)
}

// SelectResourceAcquisition is SelectAcquisition for one harvested
// definition (herbal medicine from wild healroot): every non-hunt source
// yielding exactly that resource counts by its unit yield.
func SelectResourceAcquisition(sources domain.Fact[[]AcquisitionSource], deficit, pending domain.Fact[float64], resource Resource, held map[string]bool) ([]AcquisitionSource, error) {
	if !validResource(resource) {
		return nil, errors.New("invalid acquisition resource")
	}
	accept := func(row AcquisitionSource) (float64, bool) {
		return row.Yield, !row.Hunt && Resource(row.Resource) == resource
	}
	return selectAcquisition(sources, deficit, pending, accept, held)
}

func selectAcquisition(sources domain.Fact[[]AcquisitionSource], deficit, pending domain.Fact[float64], accept func(AcquisitionSource) (float64, bool), held map[string]bool, huntSlots ...domain.Fact[int]) ([]AcquisitionSource, error) {
	rows, known := sources.Value()
	need, nk := deficit.Value()
	outstanding, pk := pending.Value()
	if !known || !nk || !pk || !foodNumber(need) || !foodNumber(outstanding) || len(rows) > 256 {
		return nil, errors.New("acquisition facts unavailable")
	}
	slots := 0
	if len(huntSlots) > 1 {
		return nil, errors.New("multiple hunting budgets")
	}
	if len(huntSlots) == 1 {
		if n, known := huntSlots[0].Value(); known {
			if n < 0 || n > 2 {
				return nil, errors.New("invalid hunting budget")
			}
			slots = n
		}
	}
	seen := map[string]bool{}
	for _, row := range rows {
		if !foodID(row.ID) || !foodID(row.Resource) || !foodID(row.Token) || seen[row.ID] || row.Cell.X < 0 || row.Cell.Z < 0 || !foodNumber(row.Yield) || row.Yield <= 0 || !foodNumber(row.NutritionYield) || !row.Food && row.NutritionYield != 0 || row.Hunt && (row.Tree || row.Yield != 1 || !row.Food && !PestDefinition(Resource(row.Definition))) {
			return nil, errors.New("invalid acquisition source")
		}
		seen[row.ID] = true
	}
	rows = append([]AcquisitionSource(nil), rows...)
	sort.SliceStable(rows, func(i, j int) bool { return !rows[i].Hunt && rows[j].Hunt })
	remaining := math.Max(0, need-outstanding)
	selected := []AcquisitionSource{}
	for _, row := range rows {
		if remaining <= 0 || len(selected) == 8 {
			break
		}
		if row.Designated || held[row.ID] {
			continue
		}
		if row.Hunt && slots == 0 {
			continue
		}
		amount, ok := accept(row)
		if !ok {
			continue
		}
		if row.Hunt {
			slots--
		}
		selected = append(selected, row)
		remaining -= amount
	}
	return selected, nil
}
