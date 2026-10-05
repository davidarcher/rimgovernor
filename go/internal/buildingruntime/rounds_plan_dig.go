package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// admitRockStep is the one dig path every planner shares: it runs the rock
// step over the planner's role cells against the frame's terrain facts and,
// when rock must go, admits the excavations as one method with every
// building in buildings waiting on all of them (dig, then build). handled is
// false when no cell needs digging, no excavation read exists or the native
// side can dig nothing now, so the caller builds as before; while
// designations stand or the method already ran this epoch it holds the
// build. access is the walkable cell a miner reaches the rock from. A dig
// plan that settled with rock still standing refuses at once, naming the
// rock (rock_not_dug); the layout never repositions, so it stays stopped.
//
// Each building is previewed over rock, since the rock on its footprint is
// mined first. A native refusal of a building whose cell the frame lists as
// open and that names no blocker is an error, not a refusal to retry.
func (b *RoundsBuildingPlanner) admitRockStep(call, epoch context.Context, s excavationStep, planned []policy.RoleCell, access domain.Cell, method domain.MethodID, buildings []domain.Building, check func() error) (RoundsBuildingResult, bool, error) {
	roofs, err := b.roofRulesFor(call, s.state.Snapshot, planned)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	step, err := policy.RockStepRoofs(planned, s.facts.Cells, roofs)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	if len(step.Unfit) > 0 {
		return RoundsBuildingResult{Verdict: rockNotDug(string(method), fmt.Sprintf("%d_cells_thick_or_unseen_roof", len(step.Unfit)))}, true, nil
	}
	return b.digPlannedSky(call, epoch, s, step.Dig, step.Unroof, access, method, buildings, check)
}

// roofRulesFor is the load's roof rules (#1870) when planned has a needs-sky
// cell, the only role that asks what a roof is; nil otherwise. A source that
// serves no definitions is an error: the roof is not guessed.
func (b *RoundsBuildingPlanner) roofRulesFor(ctx context.Context, snapshot domain.GenerationSnapshot, planned []policy.RoleCell) (policy.RoofRules, error) {
	if !slices.ContainsFunc(planned, func(c policy.RoleCell) bool { return c.Role == policy.RockNeedsSky }) {
		return nil, nil
	}
	source, ok := b.native.(observation.DefinitionSource)
	if !ok {
		return nil, errors.New("roof rules: the native source serves no definitions")
	}
	catalog, err := source.DefinitionCatalog(ctx, boundary.Identity(snapshot))
	if err != nil {
		return nil, err
	}
	return catalog.RoofRules()
}

// digPlanned is admitRockStep's executor over an already classified dig
// list; a planner calls admitRockStep, not this.
func (b *RoundsBuildingPlanner) digPlanned(call, epoch context.Context, s excavationStep, rock []domain.Cell, access domain.Cell, method domain.MethodID, buildings []domain.Building, check func() error) (RoundsBuildingResult, bool, error) {
	return b.digPlannedSky(call, epoch, s, rock, nil, access, method, buildings, check)
}

// digPlannedSky is digPlanned that also removes the roof (the existing
// remove_roof action) over unroof, the cells of a building that needs open
// sky. The roof comes off after the rock is mined and before the building;
// when the plan settles with the building still unplaced the roof is
// standing and the method refuses (rock_not_dug, roof_standing) instead of
// retrying (#1758).
func (b *RoundsBuildingPlanner) digPlannedSky(call, epoch context.Context, s excavationStep, rock, unroof []domain.Cell, access domain.Cell, method domain.MethodID, buildings []domain.Building, check func() error) (RoundsBuildingResult, bool, error) {
	if len(rock) == 0 && len(unroof) == 0 {
		return RoundsBuildingResult{}, false, nil
	}
	dig := *b
	// An unroof-only plan reads no rock, so it needs no excavation source.
	if dig.excavation == nil && len(rock) > 0 {
		source, ok := b.native.(RoundsExcavationSource)
		if !ok {
			return RoundsBuildingResult{}, false, nil
		}
		dig.excavation = source
	}
	snapshot := s.state.Snapshot
	snapshot.Revision = 1
	var excavations []domain.Excavation
	designated := false
	if len(rock) > 0 {
		site, err := dig.readExcavationSite(call, snapshot, s.facts.Identity.Tick, "plan-dig", policy.ExcavationTarget{}, rock, access, check)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		for _, cell := range site.Cells {
			designated = designated || cell.MineDesignated
			if !cell.Eligible || cell.MineDesignated || cell.Definition == "" {
				continue
			}
			excavation, err := domain.NewExcavation(cell.Cell, cell.Definition)
			if err != nil {
				return RoundsBuildingResult{}, false, err
			}
			excavations = append(excavations, excavation)
		}
		if len(buildings) == 0 {
			excavations = exposedFirst(excavations, s.facts.Cells)
		}
		if len(excavations) == 0 {
			if designated {
				return RoundsBuildingResult{Verdict: BuildingReasonExistingWork}, true, nil
			}
			return RoundsBuildingResult{}, false, nil
		}
		if site.CollapsePending || site.Support == policy.ExcavationSupportUnsupported || !site.WorkerAvailable {
			// Each cause is named, but stays no_space: callers that fall
			// back to another site test the kind.
			cause := "dig_no_worker"
			switch {
			case site.CollapsePending:
				cause = "dig_collapse_pending"
			case site.Support == policy.ExcavationSupportUnsupported:
				cause = "dig_roof_unsupported"
			}
			return RoundsBuildingResult{Verdict: noSpace(cause)}, true, nil
		}
	}
	admitted := method
	for wave := 0; ; wave++ {
		prior, err := b.reviewer.player.journal.LoadOwnerMethod(call, s.owner, admitted)
		if errors.Is(err, store.ErrNotFound) {
			break
		}
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		plan, err := b.reviewer.player.journal.LoadPlan(call, prior.Plan)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		if domain.StandardWorkOpen(plan.Progress) {
			return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "dig_plan")}, true, nil
		}
		if len(excavations) == 0 {
			return RoundsBuildingResult{Verdict: rockNotDug(string(method), fmt.Sprintf("roof_standing_%d_cells", len(unroof)))}, true, nil
		}
		if len(buildings) > 0 || wave+1 >= digWaves {
			return RoundsBuildingResult{Verdict: rockNotDug(string(method), fmt.Sprintf("%d_cells_standing", len(excavations)))}, true, nil
		}
		admitted = domain.MethodID(fmt.Sprintf("%s~%d", method, wave+1))
	}
	snapshot.Plan = domain.MintPlanID()
	stock := policy.StockObservation{Snapshot: snapshot, Tick: s.facts.Identity.Tick}
	var previews []policy.Preview
	actions := make([]domain.Action, 0, len(excavations)+len(buildings))
	digging := make(map[domain.Cell]bool, len(rock))
	for _, cell := range rock {
		digging[cell] = true
	}
	_, overRock := b.native.(overRockPreviewer)
	for i, building := range buildings {
		if !overRock && digging[building.Cell()] {
			return RoundsBuildingResult{Verdict: fieldUnavailable("over_rock_preview")}, true, nil
		}
		preview, next, reason, err := b.previewPlannedBuilding(call, snapshot, s.facts, building, i, overRock, !digging[building.Cell()])
		if err != nil || !reason.IsZero() {
			return RoundsBuildingResult{Verdict: reason}, !reason.IsZero(), err
		}
		if err = mergeRoundsStock(&stock, next, i == 0); err != nil {
			return RoundsBuildingResult{}, false, err
		}
		previews = append(previews, preview...)
		actions = append(actions, preview[0].Action)
	}
	built := actions[:len(actions):len(actions)]
	var dependencies []domain.ActionDependency
	var dug []domain.ActionID
	for _, excavation := range excavations {
		action, err := domain.NewExcavationAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, len(actions))), excavation)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		for _, building := range built {
			dependencies = append(dependencies, domain.ActionDependency{Action: building.ID(), Requires: action.ID()})
		}
		dug = append(dug, action.ID())
		actions = append(actions, action)
	}
	if len(unroof) > 0 {
		roof, err := domain.NewRemoveRoof(unroof)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		action, err := domain.NewRemoveRoofAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, len(actions))), roof)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		for _, id := range dug {
			dependencies = append(dependencies, domain.ActionDependency{Action: action.ID(), Requires: id})
		}
		for _, building := range built {
			dependencies = append(dependencies, domain.ActionDependency{Action: building.ID(), Requires: action.ID()})
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, actions, dependencies...)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	result, err := dig.admitExcavation(call, epoch, s, snapshot, admitted, plan, previews, stock, check)
	if err != nil {
		return result, false, err
	}
	return result, true, nil
}

// overRockPreviewer is the native preview of a building as though natural
// rock on its footprint were mined (bridge.Client.PreviewBuildingOverRock,
// #874).
type overRockPreviewer interface {
	PreviewBuildingOverRock(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
}

// digWaves bounds the waves one room dig may take; each wave mines the rock
// the previous one exposed.
const digWaves = 64

// exposedFirst keeps the excavations with an open walkable cardinal
// neighbour. Designating a whole room at once let miners clear cells that
// touch only diagonally, and a pawn cannot step between two diagonal gaps, so
// the rock behind stood unreachable. Each settled wave exposes the next
// layer. With no cell exposed the list stays whole.
func exposedFirst(excavations []domain.Excavation, cells []policy.SiteCell) []domain.Excavation {
	open := make(map[domain.Cell]bool, len(cells))
	for _, c := range cells {
		if walkable, known := c.Walkable.Value(); known && walkable {
			open[c.Cell] = true
		}
	}
	var out []domain.Excavation
	for _, e := range excavations {
		c := e.Cell()
		if open[domain.Cell{X: c.X + 1, Z: c.Z}] || open[domain.Cell{X: c.X - 1, Z: c.Z}] || open[domain.Cell{X: c.X, Z: c.Z + 1}] || open[domain.Cell{X: c.X, Z: c.Z - 1}] {
			out = append(out, e)
		}
	}
	if len(out) == 0 {
		return excavations
	}
	return out
}

// digMethod is the per-epoch method that mines what for room (#836).
func digMethod(what string, room policy.PlannedRoom) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("plan-dig-%s-%s-%d-%d", what, room.Role, room.Interior.X, room.Interior.Z))
}

// roomDigMethodPrefix starts every digMethod("room", ...) id.
const roomDigMethodPrefix = "plan-dig-room-"

// isRoomDigMethod reports whether method is a planned room's dig. The ring
// is raised beside it, so an open room dig never holds the shell planner.
func isRoomDigMethod(method domain.MethodID) bool {
	return strings.HasPrefix(string(method), roomDigMethodPrefix)
}

// digPlannedRoom mines a planned room's interior and door ahead of its
// shell, reached from outside the door.
func (b *RoundsBuildingPlanner) digPlannedRoom(call, epoch context.Context, s excavationStep, plan policy.LayoutPlan, room policy.PlannedRoom, check func() error) (RoundsBuildingResult, bool, error) {
	shell, err := room.Footprint()
	if err != nil {
		return RoundsBuildingResult{}, false, nil
	}
	return b.digPlanned(call, epoch, s, plan.RoomRock(room, s.facts.Cells).Dig, shell.Threshold(), digMethod("room", room), nil, check)
}

// digExhaust mines the planned exhaust shaft of the room the refrigeration
// proposal cools (#836), reached from inside the room; until it is open
// the proposal falls back to any vented wall. A cooler wall cell still in
// rock is mined in the same plan that places the planned cooler (#874),
// when the native source can preview over rock.
func (b *RoundsBuildingPlanner) digExhaust(call, epoch context.Context, s excavationStep, check func() error) (RoundsBuildingResult, bool, error) {
	plan, pk := s.facts.LayoutPlan.Value()
	rooms, rk := s.facts.Rooms.Value()
	if tier, ok := s.facts.BuildTier.Value(); !pk || !rk || !ok || tier < policy.BuildTierMasonry {
		return RoundsBuildingResult{}, false, nil
	}
	for _, room := range plan.AllRooms() {
		site, _, ok := plan.CoolerExhaust(room)
		if !ok {
			continue
		}
		if standing, ok := policy.PlannedRoomStanding(room, rooms); !ok || standing.ID != b.refrigeration.Room {
			continue
		}
		cooler := policy.RefrigerationCooler{Position: site.Cell, Rotation: site.Rotation}
		step, _ := plan.ExhaustRock(room, s.facts.Cells)
		rock := step.Dig
		var place []domain.Building
		if slices.Contains(rock, site.Cell) {
			if _, ok := b.native.(overRockPreviewer); !ok {
				return RoundsBuildingResult{}, false, nil
			}
			building, err := domain.NewBuilding("Cooler", site.Cell, site.Rotation, "")
			if err != nil {
				return RoundsBuildingResult{}, false, err
			}
			place = []domain.Building{building}
		}
		return b.digPlanned(call, epoch, s, rock, cooler.Cold(), digMethod("exhaust", room), place, check)
	}
	return RoundsBuildingResult{}, false, nil
}
