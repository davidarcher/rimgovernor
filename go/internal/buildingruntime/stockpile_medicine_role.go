package buildingruntime

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A medicine:<roomID> stockpile (#723, sited by the storage planner, #1776)
// keeps its settings while the room hosts a hospital and retires once the
// room census no longer shows it as one (the room gone, or its role
// changed); an unknown census or role holds it.
func init() {
	RegisterStockpileRole("medicine", func(in StockpileRoleInput, role string) (policy.StockpileRoleState, bool) {
		_, id, _ := strings.Cut(role, ":")
		rooms, known := in.Projection.Rooms.Value()
		facility, err := policy.Facility(policy.RoomRoleHospital)
		if !known || id == "" || err != nil {
			return policy.StockpileRoleState{}, false
		}
		state := policy.StockpileRoleState{Filter: domain.MedicineFilter(), Priority: domain.ImportantPriority}
		room, found := rooms.Room(id)
		if !found {
			state.Retired = true
			return state, true
		}
		kind, known := room.Role.Value()
		if !known {
			return policy.StockpileRoleState{}, false
		}
		state.Retired = !facility.Hosts(kind)
		return state, true
	})
}
