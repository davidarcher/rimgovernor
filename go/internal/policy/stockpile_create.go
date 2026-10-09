package policy

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StockpileCreate places the zone of a declared store no zone serves.
const StockpileCreate StockpileEditKind = "create"

// isWarehouseRole reports a warehouse zone's role: every warehouse site is a
// general store, the first planned room's "general" or a further one's
// "general:<room id>".
func isWarehouseRole(role string) bool { return stockpileRolePrefix(role) == domain.GeneralRole }

func stockpileRolePrefix(role string) string {
	prefix, _, _ := strings.Cut(role, ":")
	return prefix
}

func cellSet(cells []domain.Cell) map[domain.Cell]bool {
	out := make(map[domain.Cell]bool, len(cells))
	for _, c := range cells {
		out[c] = true
	}
	return out
}

func stockpileTouches(cells []domain.Cell, room map[domain.Cell]bool) bool {
	for _, c := range cells {
		if room[c] {
			return true
		}
	}
	return false
}
