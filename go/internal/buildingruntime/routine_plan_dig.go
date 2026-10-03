package buildingruntime

import (
	"context"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// digPlanned designates rock the layout plan wants gone before building:
// a dug room's interior and door, or a cooler's wall cell and exhaust shaft
// (#836). access is the walkable cell a miner reaches the rock from. It
// reports handled=false when there is no rock left to dig, no excavation
// read, or nothing the native side can dig now, so the caller builds as
// before; while designations stand or the method already ran this epoch it
// holds the build, since a shell or cooler cannot stand on rock. A dig plan
// that settles with rock still standing is followed by another round
// (nextDigRound), up to digRoundLimit, then the step refuses naming the rock.
//
// A non-nil cooler is a planned cooler whose wall cell is among rock (#874):
// the same plan places it, previewed as though the rock were mined and
// depending on every excavation, so the room is never left open.
func (b *RoutineBuildingPlanner) digPlanned(call, epoch context.Context, s excavationStep, rock []domain.Cell, access domain.Cell, method domain.MethodID, cooler *policy.PlannedCoolerSite, check func() error) (RoutineBuildingResult, bool, error) {
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
	method, wait, err := b.nextDigRound(call, s, method, len(excavations))
	if err != nil || !wait.IsZero() {
		return RoutineBuildingResult{Verdict: wait}, err == nil, err
	}
	snapshot.Plan = domain.MintPlanID()
	stock := policy.StockObservation{Snapshot: snapshot, Tick: s.facts.Identity.Tick}
	var previews []policy.Preview
	actions := make([]domain.Action, 0, len(excavations)+1)
	if cooler != nil {
		// The cooler is action -0, as previewCoolerWall names it.
		var reason Verdict
		previews, stock, reason, err = b.previewCoolerWall(call, snapshot, s.facts, nil, check, cooler.Cell, cooler.Rotation, true)
		if err != nil || !reason.IsZero() {
			return RoutineBuildingResult{Verdict: reason}, !reason.IsZero(), err
		}
		actions = append(actions, previews[0].Action)
	}
	var dependencies []domain.ActionDependency
	for _, excavation := range excavations {
		action, err := domain.NewExcavationAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, len(actions))), excavation)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		if cooler != nil {
			dependencies = append(dependencies, domain.ActionDependency{Action: actions[0].ID(), Requires: action.ID()})
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

// digRoundLimit bounds the dig plans one method may spend in a goal epoch: the
// first and two follow-ups for rock native took the designation off (a
// collapse or lost support cancels a mine order mid-dig).
const digRoundLimit = 3

// nextDigRound names the method of the next dig plan for base. Round 0 is
// base itself; a later round follows an earlier plan that settled with rock
// still standing, so a partial dig is dug on instead of waiting forever. A
// zero wait means method is free to admit; otherwise wait is the verdict: the
// earlier round's work still open (a wait), or rock still standing after
// digRoundLimit rounds (a refusal that names the rock; the layout never
// repositions, so it stays stopped until the rock is dealt with).
func (b *RoutineBuildingPlanner) nextDigRound(call context.Context, s excavationStep, base domain.MethodID, rock int) (method domain.MethodID, wait Verdict, err error) {
	journal := b.reviewer.player.journal
	for round := 0; round < digRoundLimit; round++ {
		method = base
		if round > 0 {
			method = domain.MethodID(fmt.Sprintf("%s-round-%d", base, round))
		}
		prior, err := journal.LoadGoalMethod(call, s.goal.Goal.ID, s.goal.Goal.Epoch, method)
		if errors.Is(err, store.ErrNotFound) {
			return method, Verdict{}, nil
		}
		if err != nil {
			return "", Verdict{}, err
		}
		plan, err := journal.LoadPlan(call, prior.Plan)
		if err != nil {
			return "", Verdict{}, err
		}
		if domain.GoalWorkOpen(plan.Progress) {
			return "", BuildingReasonUsed, nil
		}
	}
	clockSchedulerLog("%s: %s: %d rock cells still standing after %d dig plans settled", b.goal, base, rock, digRoundLimit)
	return "", rockNotDug(string(base), fmt.Sprintf("%d_cells_after_%d_dig_plans", rock, digRoundLimit)), nil
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
	return b.digPlanned(call, epoch, s, plan.RoomDig(room, s.facts.Cells), shell.Threshold(), digMethod("room", room), nil, check)
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
		rock := plan.ExhaustDig(room, s.facts.Cells)
		var place *policy.PlannedCoolerSite
		if plan.CoolerCellRock(room, s.facts.Cells) {
			if _, ok := b.native.(overRockPreviewer); !ok {
				return RoutineBuildingResult{}, false, nil
			}
			rock, place = append([]domain.Cell{site.Cell}, rock...), &site
		}
		return b.digPlanned(call, epoch, s, rock, cooler.Cold(), digMethod("exhaust", room), place, check)
	}
	return RoutineBuildingResult{}, false, nil
}
