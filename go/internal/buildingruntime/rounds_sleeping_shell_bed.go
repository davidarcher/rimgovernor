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

// packShellBed feeds a new bedroom's reconcile from the starter shell's own
// beds instead of a newly built one: when no packed Bed is stored, a vacant Bed
// left in the shell is packed (uninstalled) so the reconcile's install from
// stock takes it on the next pass (#2115; the install itself is reconcileRoom's).
// Each bed is packed once per Episode; due is false when bed is not a Bed, one
// is stored already, or there is none to carry over, so the reconcile goes on.
func (r *RoundsSleepingUpkeepPlanner) packShellBed(call, epoch context.Context, stock *packedStock, state ControlState, goal store.WorkOwner, reading observation.RoundsReading, bed string) (RoundsBuildingResult, bool, error) {
	const shellBed = "Bed"
	facts := reading.Projection
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	obs, sk := facts.Facts.Sleeping.Value()
	if bed != shellBed || !rk || !ck || !sk || !census.Colony {
		return RoundsBuildingResult{}, false, nil
	}
	items, _, err := stock.Items(call, policy.PackedFurnitureDefinition)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	for _, item := range items {
		if item.InnerDef == shellBed {
			return RoundsBuildingResult{}, false, nil
		}
	}
	p := r.reviewer.player
	plan, _ := facts.LayoutPlan.Value()
	shell := policy.ShellBedIDs(plan, rooms)
	var vacant []policy.SleepingBed // empty beds first, then an owner's
	for _, b := range obs.Beds {
		if shell[b.ID] && b.Definition == shellBed {
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
		value, err := domain.NewMoveBuilding(b.ID, shellBed, b.Cell, domain.South)
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
