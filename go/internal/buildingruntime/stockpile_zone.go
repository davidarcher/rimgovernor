package buildingruntime

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// allowListZone is a stockpile that holds only the named thing definitions.
func allowListZone(priority domain.StockpilePriority, allow []string, cells []domain.Cell) (domain.ZoneCreate, error) {
	filter, err := domain.AllowOnlyFilter(allow)
	if err != nil {
		return domain.ZoneCreate{}, err
	}
	return domain.NewFilteredStockpileZone(filter, priority, cells)
}
