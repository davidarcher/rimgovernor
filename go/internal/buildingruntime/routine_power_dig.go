package buildingruntime

import (
	"context"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// digGeothermal mines the rock on a geothermal generator's footprint ahead
// of the generator. The layout reserves the enclosure over rock
// (utilityGrid.free with rockOK), so rock under the generator is reachable;
// every other power site is chosen over open ground and needs no dig. The
// footprint is the native definition's, so it is read from a preview of the
// generator on its geyser, then handed to the shared rock step
// (admitRockStep), which admits the digs and the generator as one method
// previewed over rock. Not handled when the footprint is open ground or the
// preview names none (the ordinary site preview reports that).
func (r *RoutineBuildingPlanner) digGeothermal(call, epoch context.Context, s excavationStep, protected []domain.Cell, check func() error) (RoutineBuildingResult, bool, error) {
	if r.power == nil || !r.power.FixedSite() {
		return RoutineBuildingResult{}, false, nil
	}
	if err := check(); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	building, err := domain.NewBuilding(r.definition, r.power.Center, domain.North, "")
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	probe := s.state.Snapshot
	probe.Plan, probe.Revision = domain.MintPlanID(), 1
	action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", probe.Plan)), building)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	preview, _, err := r.native.PreviewBuilding(call, action, probe)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	footprint, known := preview.Preview.Footprint.Value()
	if !known || len(footprint) == 0 {
		return RoutineBuildingResult{}, false, nil
	}
	planned := make([]policy.RoleCell, len(footprint))
	for i, c := range footprint {
		if slices.Contains(protected, c) {
			return RoutineBuildingResult{Verdict: BuildingReasonExistingWork}, true, nil
		}
		planned[i] = policy.RoleCell{Cell: c, Role: policy.RockNeedsFloor}
	}
	if len(policy.RockStep(planned, s.facts.Cells).Dig) == 0 {
		return RoutineBuildingResult{}, false, nil
	}
	access, ok := policy.RockAccess(footprint, s.facts.Cells)
	if !ok {
		return RoutineBuildingResult{Verdict: rockNotDug(r.definition, "no_open_cell_beside_footprint")}, true, nil
	}
	method := domain.MethodID(fmt.Sprintf("plan-dig-geothermal-%d-%d", r.power.Center.X, r.power.Center.Z))
	return r.admitRockStep(call, epoch, s, planned, access, method, []domain.Building{building}, check)
}
