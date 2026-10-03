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

// digSky mines and unroofs a wind turbine or solar site laid out over
// natural rock (#1758), ahead of the generator. The layout takes rock only
// where no cell is under thick roof; a turbine's lanes (its catch zone) are
// cleared with it. If any planned site is already clear the ordinary site
// preview places that one and nothing is dug. Otherwise the first untaken
// site is handed to the shared rock step as one plan: dig, remove the
// roof, build, the generator previewed over rock on exactly the planned
// footprint. Not handled when no planned site needs the work.
func (r *RoutineBuildingPlanner) digSky(call, epoch context.Context, s excavationStep, protected []domain.Cell, check func() error) (RoutineBuildingResult, bool, error) {
	if r.power == nil || r.power.Method != policy.PowerGenerate || r.definition != policy.WindTurbineDefinition && r.definition != policy.SolarDefinition {
		return RoutineBuildingResult{}, false, nil
	}
	plan, planned := s.facts.LayoutPlan.Value()
	if tier, ok := s.facts.BuildTier.Value(); !planned || !ok || tier < policy.BuildTierMasonry {
		return RoutineBuildingResult{}, false, nil
	}
	taken := map[domain.Cell]bool{}
	for _, c := range protected {
		taken[c] = true
	}
	if census, ok := s.facts.Facts.CurrentConstruction.Value(); ok && census.Colony {
		for _, b := range census.Buildings {
			for _, c := range b.Cells {
				taken[c] = true
			}
		}
	}
	var pick *policy.PlannedPowerSite
	var pickCells []policy.RoleCell
	for _, site := range policy.PlannedPowerSites(plan, r.definition) {
		footprint := policy.RectangleCells(site.Area)
		if slices.ContainsFunc(footprint, func(c domain.Cell) bool { return taken[c] }) {
			continue
		}
		cells := make([]policy.RoleCell, 0, len(footprint))
		for _, c := range footprint {
			cells = append(cells, policy.RoleCell{Cell: c, Role: policy.RockNeedsSky})
		}
		for _, lane := range policy.TurbineCatchZone(plan, site.Area) {
			cells = append(cells, policy.RoleCell{Cell: lane, Role: policy.RockNeedsSky})
		}
		step := policy.RockStep(cells, s.facts.Cells)
		if len(step.Dig) == 0 && len(step.Unroof) == 0 && len(step.Unfit) == 0 {
			return RoutineBuildingResult{}, false, nil
		}
		if pick == nil && len(step.Unfit) == 0 {
			site := site
			pick, pickCells = &site, cells
		}
	}
	if pick == nil {
		return RoutineBuildingResult{}, false, nil
	}
	if err := check(); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	building, err := domain.NewBuilding(r.definition, pick.Cell, pick.Rotation, r.stuff)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	footprint := policy.RectangleCells(pick.Area)
	cells := make([]domain.Cell, len(pickCells))
	for i, c := range pickCells {
		cells[i] = c.Cell
	}
	access, ok := policy.RockAccess(cells, s.facts.Cells)
	if !ok {
		return RoutineBuildingResult{Verdict: rockNotDug(r.definition, "no_open_cell_beside_site")}, true, nil
	}
	sky := *r
	sky.exactFootprint, sky.windAllowance = footprint, int32(len(pickCells)-len(footprint))
	method := domain.MethodID(fmt.Sprintf("plan-dig-sky-%s-%d-%d", r.definition, pick.Area.X, pick.Area.Z))
	return sky.admitRockStep(call, epoch, s, pickCells, access, method, []domain.Building{building}, check)
}
