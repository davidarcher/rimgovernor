package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// digGeothermal mines the rock on a geothermal generator's footprint ahead
// of the generator. The layout reserves the enclosure over rock
// (utilityGrid.free with rockOK), so rock under the generator is reachable;
// every other power site is chosen over open ground and needs no dig. The
// footprint is the native definition's, so it is read from a preview of the
// generator on its geyser, then handed to the shared rock step
// (admitRockStep), which admits the digs alone (#1896); the ordinary site
// preview places the generator once the footprint reads open. Not handled when the footprint is open ground or the
// preview names none (the ordinary site preview reports that).
func (r *RoundsBuildingPlanner) digGeothermal(call, epoch context.Context, s excavationStep, protected []domain.Cell, check func() error) (RoundsBuildingResult, bool, error) {
	if r.power == nil || !r.power.FixedSite() {
		return RoundsBuildingResult{}, false, nil
	}
	if err := check(); err != nil {
		return RoundsBuildingResult{}, false, err
	}
	building, err := domain.NewBuilding(r.definition, r.power.Center, domain.North, "")
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	probe := s.state.Snapshot
	probe.Plan, probe.Revision = domain.MintPlanID(), 1
	action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", probe.Plan)), building)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	preview, _, err := r.native.PreviewBuilding(call, action, probe)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	footprint, known := preview.Preview.Footprint.Value()
	if !known || len(footprint) == 0 {
		return RoundsBuildingResult{}, false, nil
	}
	planned := make([]policy.RoleCell, len(footprint))
	for i, c := range footprint {
		if slices.Contains(protected, c) {
			return RoundsBuildingResult{Verdict: BuildingReasonExistingWork}, true, nil
		}
		planned[i] = policy.RoleCell{Cell: c, Role: policy.RockNeedsFloor}
	}
	if len(policy.RockStep(planned, s.facts.Cells).Dig) == 0 {
		return RoundsBuildingResult{}, false, nil
	}
	access, ok := policy.RockAccess(footprint, s.facts.Cells)
	if !ok {
		return RoundsBuildingResult{Verdict: rockNotDug(r.definition, "no_open_cell_beside_footprint")}, true, nil
	}
	method := domain.MethodID(fmt.Sprintf("plan-dig-geothermal-%d-%d", r.power.Center.X, r.power.Center.Z))
	return r.admitRockStep(call, epoch, s, planned, access, method, nil, check)
}

// digSky mines and unroofs a wind turbine or solar site laid out over
// natural rock (#1758), ahead of the generator. The layout takes rock only
// where no cell is under thick roof; a turbine's lanes (its catch zone) are
// cleared with it. If any planned site is already clear the ordinary site
// preview places that one and nothing is dug. A turbine clears only its own
// wind path (policy.TurbineWindCells), and the over-rock preview may report
// no more blocked cells than the step digs or unroofs there; once the work
// is done the ordinary preview must report none. Otherwise the first untaken
// site is handed to the shared rock step as a dig plus remove_roof wave and
// nothing else (#1896): the plan's dependencies cannot wait on the rock
// being mined, so the generator is not in it. The routine replans from the
// live frame every cycle; once the site reads clear this returns unhandled
// and the ordinary site preview places the generator. Not handled when no
// planned site needs the work.
func (r *RoundsBuildingPlanner) digSky(call, epoch context.Context, s excavationStep, protected []domain.Cell, check func() error) (RoundsBuildingResult, bool, error) {
	if r.power == nil || r.power.Method != policy.PowerGenerate || r.definition != policy.WindTurbineDefinition && r.definition != policy.SolarDefinition {
		return RoundsBuildingResult{}, false, nil
	}
	plan, planned := s.facts.LayoutPlan.Value()
	if tier, ok := s.facts.TechTier.Value(); !planned || !ok || tier < policy.TechTierMasonry {
		return RoundsBuildingResult{}, false, nil
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
	roofs, err := r.roofRulesFor(call, s.state.Snapshot, []policy.RoleCell{{Role: policy.RockNeedsSky}})
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	var pick *policy.PlannedPowerSite
	var pickCells []policy.RoleCell
	for _, site := range policy.PlannedPowerSites(plan, r.definition) {
		footprint := policy.RectangleCells(site.Area)
		cells := make([]policy.RoleCell, 0, len(footprint))
		for _, c := range footprint {
			cells = append(cells, policy.RoleCell{Cell: c, Role: policy.RockNeedsSky})
		}
		zone := policy.TurbineCatchZone(plan, site.Area)
		for _, lane := range zone {
			cells = append(cells, policy.RoleCell{Cell: lane, Role: policy.RockNeedsSky})
		}
		step, err := policy.RockStepRoofs(cells, s.facts.Cells, roofs)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		if slices.ContainsFunc(footprint, func(c domain.Cell) bool { return taken[c] }) {
			if len(step.Unroof) > 0 {
				stalled, err := r.roofStalled(call, s, skyMethod(r.definition, site.Area))
				if err != nil || stalled {
					return RoundsBuildingResult{Verdict: rockNotDug(r.definition, fmt.Sprintf("roof_standing_%d_cells_after_stall", len(step.Unroof)))}, stalled, err
				}
			}
			continue
		}
		if len(step.Dig) == 0 && len(step.Unroof) == 0 && len(step.Unfit) == 0 {
			return RoundsBuildingResult{}, false, nil
		}
		if pick == nil && len(step.Unfit) == 0 {
			site := site
			pick, pickCells = &site, cells
		}
	}
	if pick == nil {
		return RoundsBuildingResult{}, false, nil
	}
	if err := check(); err != nil {
		return RoundsBuildingResult{}, false, err
	}
	cells := make([]domain.Cell, len(pickCells))
	for i, c := range pickCells {
		cells[i] = c.Cell
	}
	access, ok := policy.RockAccess(cells, s.facts.Cells)
	if !ok {
		return RoundsBuildingResult{Verdict: rockNotDug(r.definition, "no_open_cell_beside_site")}, true, nil
	}
	return r.admitRockStep(call, epoch, s, pickCells, access, skyMethod(r.definition, pick.Area), nil, check)
}

func skyMethod(definition string, area policy.Rectangle) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("plan-dig-sky-%s-%d-%d", definition, area.X, area.Z))
}

// roofStalled reports whether the sky method's remove_roof action was
// designated at least excavationStallTicks ago: a placed generator whose
// roof is still on is then a named refusal, not a site skipped as taken
// forever (#1872). A site without a journaled sky method was not ours.
func (r *RoundsBuildingPlanner) roofStalled(call context.Context, s excavationStep, method domain.MethodID) (bool, error) {
	journal := r.reviewer.player.journal
	prior, err := journal.LoadOwnerMethod(call, s.owner, method)
	if errors.Is(err, store.ErrNotFound) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	plan, err := journal.LoadPlan(call, prior.Plan)
	if err != nil {
		return false, err
	}
	for _, progress := range plan.Progress {
		if _, ok := progress.Action().RemoveRoof(); !ok {
			continue
		}
		if v := progress.View(); v.Stage == domain.Completed && int64(s.facts.Identity.Tick-v.Tick) >= excavationStallTicks {
			return true, nil
		}
	}
	return false, nil
}
