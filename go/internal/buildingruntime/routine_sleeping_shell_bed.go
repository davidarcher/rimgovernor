package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// furnishFromShell answers a BedroomFurnish with the starter shell's own
// beds instead of a newly built one: a stored packed Bed is reinstalled at
// the room's first bed spot, else a vacant Bed left in the shell is packed
// (uninstalled) so the next round reinstalls it. Each step once per bed per
// goal epoch; due is false when there is no bed to carry over, the room has
// no spot, or the step was tried, so the ordinary build goes on.
func (r *RoutineSleepingUpkeepPlanner) furnishFromShell(call, epoch context.Context, state ControlState, goal store.GoalState, reading observation.RoutineReading, step policy.BedroomStep) (RoutineBuildingResult, bool, error) {
	facts := reading.Projection
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	obs, sk := facts.Facts.Sleeping.Value()
	if !rk || !ck || !sk || !census.Colony {
		return RoutineBuildingResult{}, false, nil
	}
	const bed = policy.Resource("Bed")
	var anchor domain.Cell
	var rot domain.Rotation
	found := false
	for _, room := range policy.CoupleBedRooms(rooms, census, facts.Cells) {
		if room.Room.Interior != step.Room.Interior {
			continue
		}
		anchor, rot, found = policy.BedSpot(room, bed)
	}
	if !found {
		return RoutineBuildingResult{}, false, nil
	}
	p := r.reviewer.player
	used := func(method domain.MethodID) (bool, error) {
		_, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method)
		if err == nil {
			return true, nil
		}
		if errors.Is(err, store.ErrNotFound) {
			return false, nil
		}
		return false, err
	}
	move, stored, err := r.storedPiece(call, state, string(bed), anchor, rot)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if stored {
		digest := sha256.Sum256([]byte(move.Thing()))
		method := domain.MethodID(fmt.Sprintf("bedroom-reinstall-%x", digest[:8]))
		if done, err := used(method); err != nil || done {
			return RoutineBuildingResult{}, false, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewMoveBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), move)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		clockSchedulerLog("%s: bedroom: reinstall stored %s in the new room", goal.Goal.ID, move.Thing())
		return r.commitCouple(call, epoch, state, goal, method, id, []domain.Action{action})
	}
	plan, _ := facts.LayoutPlan.Value()
	shell := policy.ShellBedIDs(plan, rooms)
	var vacant []policy.SleepingBed
	for _, b := range obs.Beds {
		if shell[b.ID] && b.Definition == bed && len(b.Owners) == 0 {
			vacant = append(vacant, b)
		}
	}
	sort.Slice(vacant, func(i, j int) bool { return vacant[i].ID < vacant[j].ID })
	for _, b := range vacant {
		digest := sha256.Sum256([]byte(b.ID))
		method := domain.MethodID(fmt.Sprintf("bedroom-shell-pack-%x", digest[:8]))
		if m, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
			// Packing is under way: wait for it rather than build a second bed.
			if prior, err := p.journal.LoadPlan(call, m.Plan); err == nil && domain.GoalWorkOpen(prior.Progress) {
				return RoutineBuildingResult{Reason: BuildingMethodUsed}, true, nil
			}
			continue
		} else if !errors.Is(err, store.ErrNotFound) {
			return RoutineBuildingResult{}, false, err
		}
		value, err := domain.NewMoveBuilding(b.ID, string(bed), b.Cell, domain.South)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewUninstallBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		clockSchedulerLog("%s: bedroom: pack vacant shell bed %s to carry it over", goal.Goal.ID, b.ID)
		return r.commitCouple(call, epoch, state, goal, method, id, []domain.Action{action})
	}
	return RoutineBuildingResult{}, false, nil
}
