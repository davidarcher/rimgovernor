package policy

import (
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
)

// AcquisitionSource is an observed native-approved plant, not inventory.
type AcquisitionSource struct {
	ID, Resource, Token    string
	Cell                   domain.Cell
	Tree, Food, Designated bool
	Yield, NutritionYield  float64
}

// SelectAcquisition retains native distance ordering. Pending yield and unresolved
// sources prevent duplicate work but never count as recovered stock.
func SelectAcquisition(sources domain.Fact[[]AcquisitionSource], deficit, pending domain.Fact[float64], food bool, held map[string]bool) ([]AcquisitionSource, error) {
	rows, known := sources.Value()
	need, nk := deficit.Value()
	outstanding, pk := pending.Value()
	if !known || !nk || !pk || !foodNumber(need) || !foodNumber(outstanding) || len(rows) > 256 {
		return nil, errors.New("acquisition facts unavailable")
	}
	seen := map[string]bool{}
	for _, row := range rows {
		if !foodID(row.ID) || !foodID(row.Resource) || !foodID(row.Token) || seen[row.ID] || row.Cell.X < 0 || row.Cell.Z < 0 || !foodNumber(row.Yield) || row.Yield <= 0 || !foodNumber(row.NutritionYield) || !row.Food && row.NutritionYield != 0 {
			return nil, errors.New("invalid acquisition source")
		}
		seen[row.ID] = true
	}
	remaining := math.Max(0, need-outstanding)
	selected := []AcquisitionSource{}
	for _, row := range rows {
		if remaining <= 0 || len(selected) == 8 {
			break
		}
		if row.Designated || held[row.ID] {
			continue
		}
		amount := row.Yield
		if food {
			if !row.Food || row.NutritionYield <= 0 {
				continue
			}
			amount = row.NutritionYield
		} else if !row.Tree || row.Resource != "WoodLog" {
			continue
		}
		selected = append(selected, row)
		remaining -= amount
	}
	return selected, nil
}
