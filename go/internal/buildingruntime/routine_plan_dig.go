package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
func (b *RoutineBuildingPlanner) admitRockStep(call, epoch context.Context, s excavationStep, planned []policy.RoleCell, access domain.Cell, method domain.MethodID, buildings []domain.Building, check func() error) (RoutineBuildingResult, bool, error) {
	return b.digPlanned(call, epoch, s, policy.RockStep(planned, s.facts.Cells).Dig, access, method, buildings, check)
}

// digPlanned is admitRockStep's executor over an already classified dig
// list; a planner calls admitRockStep, not this.
func (b *RoutineBuildingPlanner) digPlanned(call, epoch context.Context, s excavationStep, rock []domain.Cell, access domain.Cell, method domain.MethodID, buildings []domain.Building, check func() error) (RoutineBuildingResult, bool, error) {
	if len(rock) == 0 {
		return RoutineBuildingResult{}, false, nil
	}
	dig := *b
	if dig.excavation == nil {
		source, ok := b.native.(RoutineExcavationSource)
		if !ok {
			return RoutineBuildingResult{}, false, nil
		}
		dig.excavation = source
	}
	snapshot := s.state.Snapshot
	snapshot.Revision = 1
	site, err := dig.readExcavationSite(call, snapshot, s.facts.Identity.Tick, "plan-dig", policy.ExcavationTarget{}, rock, access, check)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	var excavations []domain.Excavation
	designated := false
	for _, cell := range site.Cells {
		designated = designated || cell.MineDesignated
		if !cell.Eligible || cell.MineDesignated || cell.Definition == "" {
			continue
		}
		excavation, err := domain.NewExcavation(cell.Cell, cell.Definition)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		excavations = append(excavations, excavation)
	}
	if len(excavations) == 0 {
		if designated {
			return RoutineBuildingResult{Verdict: BuildingReasonExistingWork}, true, nil
		}
		clockSchedulerLog("%s: %s: none of %d rock cells eligible", b.goal, method, len(rock))
		return RoutineBuildingResult{}, false, nil
	}
	if site.CollapsePending || site.Support == policy.ExcavationSupportUnsupported || !site.WorkerAvailable {
		clockSchedulerLog("%s: %s: not diggable now: support=%d (%s) collapse=%v worker=%v", b.goal, method, site.Support, site.SupportBlocker, site.CollapsePending, site.WorkerAvailable)
		return RoutineBuildingResult{Verdict: BuildingReasonNoSpace}, true, nil
	}
	if prior, err := b.reviewer.player.journal.LoadGoalMethod(call, s.goal.Goal.ID, s.goal.Goal.Epoch, method); err == nil {
		plan, err := b.reviewer.player.journal.LoadPlan(call, prior.Plan)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return RoutineBuildingResult{Verdict: BuildingReasonUsed}, true, nil
		}
		clockSchedulerLog("%s: %s: %d rock cells still standing after the dig plan settled", b.goal, method, len(excavations))
		return RoutineBuildingResult{Verdict: rockNotDug(string(method), fmt.Sprintf("%d_cells_standing", len(excavations)))}, true, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, false, err
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
			return RoutineBuildingResult{Verdict: fieldUnavailable("over_rock_preview")}, true, nil
		}
		preview, next, reason, err := b.previewPlannedBuilding(call, snapshot, s.facts, building, i, overRock, !digging[building.Cell()])
		if err != nil || !reason.IsZero() {
			return RoutineBuildingResult{Verdict: reason}, !reason.IsZero(), err
		}
		if err = mergeRoutineStock(&stock, next, i == 0); err != nil {
			return RoutineBuildingResult{}, false, err
		}
		previews = append(previews, preview...)
		actions = append(actions, preview[0].Action)
	}
	built := actions[:len(actions):len(actions)]
	var dependencies []domain.ActionDependency
	for _, excavation := range excavations {
		action, err := domain.NewExcavationAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, len(actions))), excavation)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		for _, building := range built {
			dependencies = append(dependencies, domain.ActionDependency{Action: building.ID(), Requires: action.ID()})
		}
		actions = append(actions, action)
	}
	plan, err := domain.NewPlan(snapshot.Plan, 1, actions, dependencies...)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	result, err := dig.admitExcavation(call, epoch, s, snapshot, method, plan, previews, stock, check)
	if err != nil {
		return result, false, err
	}
	clockSchedulerLog("%s: %s: %d rock cells reason=%s", b.goal, method, len(actions), result.Verdict)
	return result, true, nil
}

// overRockPreviewer is the native preview of a building as though natural
// rock on its footprint were mined (bridge.Client.PreviewBuildingOverRock,
// #874).
type overRockPreviewer interface {
	PreviewBuildingOverRock(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error)
}

// digMethod is the per-epoch method that mines what for room (#836).
func digMethod(what string, room policy.LayoutRoom) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("plan-dig-%s-%s-%d-%d", what, room.Role, room.Interior.X, room.Interior.Z))
}

// digPlannedRoom mines a planned room's interior and door ahead of its
// shell, reached from outside the door.
func (b *RoutineBuildingPlanner) digPlannedRoom(call, epoch context.Context, s excavationStep, plan policy.LayoutPlan, room policy.LayoutRoom, check func() error) (RoutineBuildingResult, bool, error) {
	shell, err := room.Footprint()
	if err != nil {
		return RoutineBuildingResult{}, false, nil
	}
	return b.digPlanned(call, epoch, s, plan.RoomRock(room, s.facts.Cells).Dig, shell.Threshold(), digMethod("room", room), nil, check)
}

// digExhaust mines the planned exhaust shaft of the room the refrigeration
// proposal cools (#836), reached from inside the room; until it is open
// the proposal falls back to any vented wall. A cooler wall cell still in
// rock is mined in the same plan that places the planned cooler (#874),
// when the native source can preview over rock.
func (b *RoutineBuildingPlanner) digExhaust(call, epoch context.Context, s excavationStep, check func() error) (RoutineBuildingResult, bool, error) {
	plan, pk := s.facts.LayoutPlan.Value()
	rooms, rk := s.facts.Rooms.Value()
	if tier, ok := s.facts.BuildTier.Value(); !pk || !rk || !ok || tier < policy.BuildTierMasonry {
		return RoutineBuildingResult{}, false, nil
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
				return RoutineBuildingResult{}, false, nil
			}
			building, err := domain.NewBuilding("Cooler", site.Cell, site.Rotation, "")
			if err != nil {
				return RoutineBuildingResult{}, false, err
			}
			place = []domain.Building{building}
		}
		return b.digPlanned(call, epoch, s, rock, cooler.Cold(), digMethod("exhaust", room), place, check)
	}
	return RoutineBuildingResult{}, false, nil
}
