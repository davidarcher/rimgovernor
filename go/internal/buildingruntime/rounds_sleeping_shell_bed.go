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
// Episode; due is false when there is no bed to carry over, the room has
// no spot, or the step was tried, so the ordinary build goes on.
func (r *RoundsSleepingUpkeepPlanner) furnishFromShell(call, epoch context.Context, stock *packedStock, state ControlState, goal store.WorkOwner, reading observation.RoundsReading, step policy.BedroomStep) (RoundsBuildingResult, bool, error) {
	facts := reading.Projection
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	obs, sk := facts.Facts.Sleeping.Value()
	if !rk || !ck || !sk || !census.Colony {
		return RoundsBuildingResult{}, false, nil
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
		return RoundsBuildingResult{}, false, nil
	}
	p := r.reviewer.player
	used := func(method domain.MethodID) (bool, error) {
		_, err := p.journal.LoadOwnerMethod(call, goal, method)
		if err == nil {
			return true, nil
		}
		if errors.Is(err, store.ErrNotFound) {
			return false, nil
		}
		return false, err
	}
	move, stored, err := stock.Install(call, policy.PackedFurnitureDefinition, string(bed), anchor, rot)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	if stored {
		digest := sha256.Sum256([]byte(move.Thing()))
		method := domain.MethodID(fmt.Sprintf("bedroom-reinstall-%x", digest[:8]))
		if done, err := used(method); err != nil || done {
			return RoundsBuildingResult{}, false, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewMoveBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), move)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		return r.commitCouple(call, epoch, state, goal, method, id, []domain.Action{action})
	}
	plan, _ := facts.LayoutPlan.Value()
	shell := policy.ShellBedIDs(plan, rooms)
	var vacant []policy.SleepingBed // empty beds first, then an owner's
	for _, b := range obs.Beds {
		if shell[b.ID] && b.Definition == bed {
			vacant = append(vacant, b)
		}
	}
	sort.Slice(vacant, func(i, j int) bool {
		if (len(vacant[i].Owners) == 0) != (len(vacant[j].Owners) == 0) {
			return len(vacant[i].Owners) == 0
		}
		return vacant[i].ID < vacant[j].ID
	})
	for _, b := range vacant {
		digest := sha256.Sum256([]byte(b.ID))
		method := domain.MethodID(fmt.Sprintf("bedroom-shell-pack-%x", digest[:8]))
		if m, err := p.journal.LoadOwnerMethod(call, goal, method); err == nil {
			// Packing is under way: wait for it rather than build a second bed.
			if prior, err := p.journal.LoadPlan(call, m.Plan); err == nil && domain.StandardWorkOpen(prior.Progress) {
				return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "shell_bed_pack")}, true, nil
			}
			continue
		} else if !errors.Is(err, store.ErrNotFound) {
			return RoundsBuildingResult{}, false, err
		}
		value, err := domain.NewMoveBuilding(b.ID, string(bed), b.Cell, domain.South)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewUninstallBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), value)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		return r.commitCouple(call, epoch, state, goal, method, id, []domain.Action{action})
	}
	return RoundsBuildingResult{}, false, nil
}
