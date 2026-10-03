package store

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func allowListZone(priority domain.StockpilePriority, allow []string, cells []domain.Cell) (domain.ZoneCreate, error) {
	f, err := domain.AllowOnlyFilter(allow)
	if err != nil {
		return domain.ZoneCreate{}, err
	}
	return domain.NewFilteredStockpileZone(f, priority, cells)
}
