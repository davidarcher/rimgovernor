package buildingruntime

import (
	"context"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// digBreach cuts a route door through natural rock: when no breach cell
// takes a door on open wall (previewRoutes found no space), the first breach
// cell the frame lists as rock or does not list (fogged mountain) is mined
// and the door built on it, through the shared rock step (admitRockStep),
// reached from the open ground beside it. A door is a floor-role cell: the
// rock there is dug, then built on. Not handled when no breach is rock.
func (r *RoundsBuildingPlanner) digBreach(call, epoch context.Context, s excavationStep, protected []domain.Cell, check func() error) (RoundsBuildingResult, bool, error) {
	if r.routes == nil || r.routes.Method != policy.RoutesBuild {
		return RoundsBuildingResult{}, false, nil
	}
	for _, cell := range r.routes.Breaches {
		planned := []policy.RoleCell{{Cell: cell, Role: policy.RockNeedsFloor}}
		if slices.Contains(protected, cell) || len(policy.RockStep(planned, s.facts.Cells).Dig) == 0 {
			continue
		}
		access, ok := policy.RockAccess([]domain.Cell{cell}, s.facts.Cells)
		if !ok {
			continue
		}
		door, err := domain.NewBuilding(r.routes.Definition, cell, domain.North, r.routes.Stuff)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		method := domain.MethodID(fmt.Sprintf("plan-dig-route-%d-%d", cell.X, cell.Z))
		return r.admitRockStep(call, epoch, s, planned, access, method, []domain.Building{door}, check)
	}
	return RoundsBuildingResult{}, false, nil
}
