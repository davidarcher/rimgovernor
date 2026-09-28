package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// sculptureSource is the native half the sculpture lever (#830) needs
// beyond RoutineBuildingSource; a source without it skips the lever.
type sculptureSource interface {
	ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error)
	ReadPackedItems(context.Context, *c.Identity, string) ([]bridge.PackedItem, bridge.Result, error)
}

var _ sculptureSource = (*bridge.Client)(nil)

// sculptBedroom is the beauty lever after pots and floors (#830): one
// small sculpture bill at an art bench, then the finished packed sculpture
// installed (a RelocateIntent on the packed item's inner building) on free floor in the
// room; one change a step, each once per goal epoch. due is false when
// the lever has nothing to do.
func (r *RoutineSleepingUpkeepPlanner) sculptBedroom(call, epoch context.Context, state ControlState, goal store.GoalState, reading observation.RoutineReading) (RoutineBuildingResult, bool, error) {
	native, ok := r.native.(sculptureSource)
	facts := reading.Projection
	obs, sk := facts.Facts.Sleeping.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	traits := sleepingTraits(facts)
	if !ok || !sk || !rk || !ck || !census.Colony || traits == nil {
		return RoutineBuildingResult{}, false, nil
	}
	tier, _ := facts.BuildTier.Value()
	identity := boundary.Identity(state.Snapshot)
	benches, _, err := native.ReadGearBenches(call, identity)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	rows := make([]policy.GearBench, 0, len(benches))
	tokens := map[string]string{}
	for _, b := range benches {
		rows = append(rows, b.Bench)
		tokens[b.Bench.ID] = b.Token
	}
	items, _, err := native.ReadPackedItems(call, identity, policy.PackedSculptureDefinition)
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	packed := make([]string, 0, len(items))
	inner := map[string]bridge.PackedItem{}
	for _, item := range items {
		packed = append(packed, item.ID)
		inner[item.ID] = item
	}
	step, due := policy.NextSculpture(obs, policy.RoomQualityTargets(obs, traits, tier), policy.TidyFurnitureRooms(rooms, census, facts.Cells), rows, packed)
	if !due {
		return RoutineBuildingResult{}, false, nil
	}
	p := r.reviewer.player
	key := step.Room
	if step.Kind == policy.SculptureInstall {
		key = step.Packed
	}
	digest := sha256.Sum256([]byte(key))
	method := domain.MethodID(fmt.Sprintf("bedroom-sculpture-%s-%x", step.Kind, digest[:8]))
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, true, nil
	}
	id := domain.MintPlanID()
	var action domain.Action
	switch step.Kind {
	case policy.SculptureBill:
		bill, err := domain.NewProductionBill(step.Bench, policy.SculptureRecipe, domain.GearBatch, 1)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		if action, err = domain.NewProductionBillAction(domain.ActionID(fmt.Sprintf("%s-0", id)), bill); err != nil {
			return RoutineBuildingResult{}, false, err
		}
	case policy.SculptureInstall:
		item := inner[step.Packed]
		if item.InnerDef != policy.SculptureDefinition {
			clockSchedulerLog("%s: bedroom %s: packed sculpture %s not installable at %d,%d", goal.Goal.ID, step.Room, step.Packed, step.Anchor.X, step.Anchor.Z)
			return RoutineBuildingResult{Reason: BuildingMethodRefused}, true, nil
		}
		move, err := domain.NewMoveBuilding(item.Inner, item.InnerDef, step.Anchor, domain.North)
		if err != nil {
			return RoutineBuildingResult{}, false, err
		}
		if action, err = domain.NewMoveBuildingAction(domain.ActionID(fmt.Sprintf("%s-0", id)), move); err != nil {
			return RoutineBuildingResult{}, false, err
		}
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if err := p.current(call, epoch); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	if p.session.State() != state {
		return RoutineBuildingResult{}, false, fmt.Errorf("%w: sculptBedroom: p.session.State() != state", ErrControl)
	}
	if _, err := p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineBuildingResult{}, false, err
	}
	clockSchedulerLog("%s: bedroom %s: sculpture %s (weakest beauty)", goal.Goal.ID, step.Room, step.Kind)
	return RoutineBuildingResult{Reason: BuildingMethodAdmitted}, true, nil
}
