package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const couplePackMethod = "sleeping-couple-pack-"

// couplePacked is the cells of the beds this epoch's completed couple pack
// steps uninstalled, each plan's first (the couple's room's) first.
func (r *RoundsSleepingUpkeepPlanner) couplePacked(call context.Context, goal store.WorkOwner) ([]domain.Cell, error) {
	p := r.reviewer.player
	methods, err := p.journal.LoadOwnerMethods(call, goal)
	if err != nil {
		return nil, err
	}
	var out []domain.Cell
	for _, m := range methods {
		if !strings.HasPrefix(string(m.Method), couplePackMethod) {
			continue
		}
		plan, err := p.journal.LoadPlan(call, m.Plan)
		if err != nil {
			return nil, err
		}
		if len(plan.Progress) == 0 || plan.Progress[0].View().Stage != domain.Completed {
			continue
		}
		if u, ok := plan.Progress[0].Action().UninstallBuilding(); ok {
			out = append(out, u.Cell())
		}
	}
	return out, nil
}

// coupleBed is the couple bed lever: pack the couple's single beds,
// then install a DoubleBed (a stored one first) in the couple's room's
// bedroom slot; each step once per Episode. due is false when nothing
// is to do, so the ordinary sleeping choice goes on.
func (r *RoundsSleepingUpkeepPlanner) coupleBed(call, epoch context.Context, stock *packedStock, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading) (RoundsBuildingResult, bool, error) {
	facts := reading.Projection
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !sk || !rk || !ck || !census.Colony {
		return RoundsBuildingResult{}, false, nil
	}
	packed, err := r.couplePacked(call, goal)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	buildable, _ := facts.DefinitionAvailable(policy.SleepingCoupleBedDefinition).Value()
	plan, _ := facts.LayoutPlan.Value()
	step, due := policy.NextCoupleBed(obs, plan, policy.CoupleBedRooms(rooms, census, facts.Cells), packed, buildable)
	if !due {
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
	switch step.Kind {
	case policy.CouplePack:
		key := ""
		for _, piece := range step.Pack {
			key += piece.Thing + "/"
		}
		digest := sha256.Sum256([]byte(key))
		method := domain.MethodID(fmt.Sprintf("%s%x", couplePackMethod, digest[:8]))
		if done, err := used(method); err != nil || done {
			return RoundsBuildingResult{}, false, err
		}
		id := domain.MintPlanID()
		actions := make([]domain.Action, 0, len(step.Pack))
		for i, piece := range step.Pack {
			value, err := domain.NewMoveBuilding(piece.Thing, piece.Def, policy.AnchorForRect(piece.Rect, piece.Size, piece.Rot), piece.Rot)
			if err != nil {
				return RoundsBuildingResult{}, false, err
			}
			action, err := domain.NewUninstallBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), value)
			if err != nil {
				return RoundsBuildingResult{}, false, err
			}
			actions = append(actions, action)
		}
		return r.commitCouple(call, epoch, state, goal, method, id, actions)
	case policy.CoupleInstall:
		// The couple's room is reconciled to the DoubleBed in its bed slot: a
		// stored one is installed, else one is built.
		in := step.Planned.Interior
		result, err := r.building.reconcileRoom(call, epoch, state, review, goal, reading, stock, roomReconcile{
			room: step.Planned, template: step.Template,
			name: fmt.Sprintf("sleeping-couple-install-%d-%d", in.X, in.Z), reason: "couple's double bed",
		})
		// A wave already tried this epoch, or refused, leaves the ordinary
		// choice to go on.
		return result, err != nil || result.Verdict == BuildingReasonAdmitted, err
	}
	return RoundsBuildingResult{}, false, nil
}

// reinstallStoredBed answers a SleepingBuild with a stored packed bed of
// the chosen definition, installed at the first free bed spot of a hosting
// room; once per packed bed per Episode. due is false when none is
// stored or none fits, so the build goes on.
func (r *RoundsSleepingUpkeepPlanner) reinstallStoredBed(call, epoch context.Context, stock *packedStock, state ControlState, goal store.WorkOwner, reading observation.RoundsReading, choice policy.SleepingChoice) (RoundsBuildingResult, bool, error) {
	facts := reading.Projection
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !rk || !ck || !census.Colony {
		return RoundsBuildingResult{}, false, nil
	}
	hosting := map[domain.Cell]bool{}
	for _, cell := range choice.Cells {
		hosting[cell] = true
	}
	p := r.reviewer.player
	for _, room := range policy.FurnitureRooms(rooms, census, facts.Cells) {
		anchor, rot, ok := policy.BedSpot(room, policy.Resource(choice.Definition))
		if !ok || !hosting[anchor] {
			continue
		}
		move, stored, err := stock.Install(call, policy.PackedFurnitureDefinition, choice.Definition, anchor, rot)
		if err != nil || !stored {
			return RoundsBuildingResult{}, false, err
		}
		digest := sha256.Sum256([]byte(move.Thing()))
		method := domain.MethodID(fmt.Sprintf("sleeping-reinstall-%x", digest[:8]))
		if _, err := p.journal.LoadOwnerMethod(call, goal, method); err == nil {
			return RoundsBuildingResult{}, false, nil
		} else if !errors.Is(err, store.ErrNotFound) {
			return RoundsBuildingResult{}, false, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewMoveBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), move)
		if err != nil {
			return RoundsBuildingResult{}, false, err
		}
		return r.commitCouple(call, epoch, state, goal, method, id, []domain.Action{action})
	}
	return RoundsBuildingResult{}, false, nil
}

func (r *RoundsSleepingUpkeepPlanner) commitCouple(call, epoch context.Context, state ControlState, goal store.WorkOwner, method domain.MethodID, id domain.PlanID, actions []domain.Action) (RoundsBuildingResult, bool, error) {
	p := r.reviewer.player
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoundsBuildingResult{}, false, err
	}
	if err := p.current(call, epoch); err != nil {
		return RoundsBuildingResult{}, false, err
	}
	if p.session.State() != state {
		return RoundsBuildingResult{}, false, fmt.Errorf("%w: commitCouple: p.session.State() != state", ErrControl)
	}
	if err := p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoundsBuildingResult{}, false, err
	}
	return RoundsBuildingResult{Verdict: BuildingReasonAdmitted}, true, nil
}
