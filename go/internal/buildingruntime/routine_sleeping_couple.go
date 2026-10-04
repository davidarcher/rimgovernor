package buildingruntime

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// packedSource is the native half that reinstalls stored packed furniture
// (#830, #843); a source without it builds new beds instead.
type packedSource interface {
	ReadPackedItems(context.Context, *c.Identity, string) ([]bridge.PackedItem, bridge.Result, error)
}

var _ packedSource = (*bridge.Client)(nil)

const couplePackMethod = "sleeping-couple-pack-"

// storedPiece is a stored packed item of def installable at anchor/rot,
// as the move that installs it; false when none (or no packed source).
func (r *RoutineSleepingUpkeepPlanner) storedPiece(call context.Context, state ControlState, def string, anchor domain.Cell, rot domain.Rotation) (domain.MoveBuilding, bool, error) {
	native, ok := r.native.(packedSource)
	if !ok {
		return domain.MoveBuilding{}, false, nil
	}
	identity := boundary.Identity(state.Snapshot)
	packed, _, err := native.ReadPackedItems(call, identity, policy.PackedFurnitureDefinition)
	if err != nil {
		return domain.MoveBuilding{}, false, err
	}
	for _, item := range packed {
		if item.InnerDef != def {
			continue
		}
		move, err := domain.NewMoveBuilding(item.Inner, def, anchor, rot)
		return move, err == nil, err
	}
	return domain.MoveBuilding{}, false, nil
}

// couplePacked is the cells of the beds this epoch's completed couple pack
// steps uninstalled, each plan's first (the couple's room's) first.
func (r *RoutineSleepingUpkeepPlanner) couplePacked(call context.Context, goal store.WorkOwner) ([]domain.Cell, error) {
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

// coupleBed is the couple bed lever (#843): pack the couple's single beds,
// then install a DoubleBed (a stored one first) in the couple's room's
// bedroom slot; each step once per goal epoch. due is false when nothing
// is to do, so the ordinary sleeping choice goes on.
func (r *RoutineSleepingUpkeepPlanner) coupleBed(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.WorkOwner, reading observation.RoutineReading) (RoutineBuildingResult, bool, error) {
	facts := reading.Projection
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !sk || !rk || !ck || !census.Colony {
		return RoutineBuildingResult{}, false, nil
	}
	packed, err := r.couplePacked(call, goal)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	buildable, _ := facts.DefinitionAvailable(policy.SleepingCoupleBedDefinition).Value()
	step, due := policy.NextCoupleBed(obs, policy.CoupleBedRooms(rooms, census, facts.Cells), packed, buildable)
	if !due {
		return RoutineBuildingResult{}, false, nil
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
			return RoutineBuildingResult{}, false, err
		}
		id := domain.MintPlanID()
		actions := make([]domain.Action, 0, len(step.Pack))
		for i, piece := range step.Pack {
			value, err := domain.NewMoveBuilding(piece.Thing, piece.Def, policy.AnchorForRect(piece.Rect, piece.Size, piece.Rot), piece.Rot)
			if err != nil {
				return RoutineBuildingResult{}, false, err
			}
			action, err := domain.NewUninstallBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", id, i)), value)
			if err != nil {
				return RoutineBuildingResult{}, false, err
			}
			actions = append(actions, action)
		}
		clockSchedulerLog("%s: couple %s+%s: pack %d single bed(s) for room %s", goal.OwnerID(), step.Pawn, step.Partner, len(actions), step.Room)
		return r.commitCouple(call, epoch, state, goal, method, id, actions)
	case policy.CoupleInstall:
		room := sha256.Sum256([]byte(fmt.Sprintf("%d,%d", step.Anchor.X, step.Anchor.Z)))
		method := domain.MethodID(fmt.Sprintf("sleeping-couple-install-%x", room[:8]))
		if done, err := used(method); err != nil || done {
			return RoutineBuildingResult{}, false, err
		}
		move, stored, err := r.storedPiece(call, state, policy.SleepingCoupleBedDefinition, step.Anchor, step.Rot)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		if stored {
			id := domain.MintPlanID()
			action, err := domain.NewMoveBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), move)
			if err != nil {
				return RoutineBuildingResult{}, false, err
			}
			clockSchedulerLog("%s: couple %s+%s: reinstall stored %s in room %s", goal.OwnerID(), step.Pawn, step.Partner, move.Thing(), step.Room)
			return r.commitCouple(call, epoch, state, goal, method, id, []domain.Action{action})
		}
		clockSchedulerLog("%s: couple %s+%s: build DoubleBed in room %s", goal.OwnerID(), step.Pawn, step.Partner, step.Room)
		result, err := r.upgradeBedroom(call, epoch, state, review, goal, reading, policy.RoomUpgrade{Room: step.Room, Slot: "couple-bed", Def: policy.SleepingCoupleBedDefinition, Anchor: step.Anchor, Rot: step.Rot})
		// A build already tried this epoch, or refused, leaves the
		// ordinary choice to go on.
		return result, err != nil || result.Verdict == BuildingReasonAdmitted, err
	}
	return RoutineBuildingResult{}, false, nil
}

// reinstallStoredBed answers a SleepingBuild with a stored packed bed of
// the chosen definition, installed at the first free bed spot of a hosting
// room; once per packed bed per goal epoch. due is false when none is
// stored or none fits, so the build goes on.
func (r *RoutineSleepingUpkeepPlanner) reinstallStoredBed(call, epoch context.Context, state ControlState, goal store.WorkOwner, reading observation.RoutineReading, choice policy.SleepingChoice) (RoutineBuildingResult, bool, error) {
	facts := reading.Projection
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	if !rk || !ck || !census.Colony {
		return RoutineBuildingResult{}, false, nil
	}
	hosting := map[domain.Cell]bool{}
	for _, cell := range choice.Cells {
		hosting[cell] = true
	}
	p := r.reviewer.player
	for _, room := range policy.TidyFurnitureRooms(rooms, census, facts.Cells) {
		anchor, rot, ok := policy.BedSpot(room, policy.Resource(choice.Definition))
		if !ok || !hosting[anchor] {
			continue
		}
		move, stored, err := r.storedPiece(call, state, choice.Definition, anchor, rot)
		if err != nil || !stored {
			return RoutineBuildingResult{}, false, err
		}
		digest := sha256.Sum256([]byte(move.Thing()))
		method := domain.MethodID(fmt.Sprintf("sleeping-reinstall-%x", digest[:8]))
		if _, err := p.journal.LoadOwnerMethod(call, goal, method); err == nil {
			return RoutineBuildingResult{}, false, nil
		} else if !errors.Is(err, store.ErrNotFound) {
			return RoutineBuildingResult{}, false, err
		}
		id := domain.MintPlanID()
		action, err := domain.NewMoveBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), move)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		clockSchedulerLog("%s: reinstall stored %s %s in room %s", goal.OwnerID(), choice.Definition, move.Thing(), room.ID)
		return r.commitCouple(call, epoch, state, goal, method, id, []domain.Action{action})
	}
	return RoutineBuildingResult{}, false, nil
}

func (r *RoutineSleepingUpkeepPlanner) commitCouple(call, epoch context.Context, state ControlState, goal store.WorkOwner, method domain.MethodID, id domain.PlanID, actions []domain.Action) (RoutineBuildingResult, bool, error) {
	p := r.reviewer.player
	plan, err := domain.NewPlan(id, 1, actions)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if err := p.current(call, epoch); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if p.session.State() != state {
		return RoutineBuildingResult{}, false, fmt.Errorf("%w: commitCouple: p.session.State() != state", ErrControl)
	}
	if err := p.journal.CommitOwnerMethod(call, goal, method, "", plan); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	return RoutineBuildingResult{Verdict: BuildingReasonAdmitted}, true, nil
}
