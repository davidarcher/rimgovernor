package buildingruntime

import (
	"context"
	"errors"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The barn and vet room (#1633): once the pen stands, MaintainAnimalContainment
// raises each planned herd room, places its animal beds one at a time and
// flags the vet room's beds medical (policy.NextHerdStep). The review holds the goal open while a step is due
// (HerdRoomsOwed), so a herd that outgrows its beds is topped up.

// herdDefinitions are the definitions the herd steps read availability,
// stuff and size for.
var herdDefinitions = []string{"Wall", "Door", policy.AnimalSleepingSpotDefinition, policy.AnimalBedDefinition}

// herdFurniture reads the two animal bed shapes from the live catalog. A
// definition the catalog lacks (a wrong defName, or a game without it) is an
// error, never a quiet skip; one that exists but is not buildable yet, or
// has no size read, reports false.
func herdFurniture(facts observation.ColonyProjection) (policy.HerdFurniture, bool, error) {
	var out policy.HerdFurniture
	usable := true
	for _, name := range []string{policy.AnimalSleepingSpotDefinition, policy.AnimalBedDefinition} {
		d, found := animalContainmentDefinition(facts.Definitions, name)
		if !found {
			return out, false, fmt.Errorf("%w: herdFurniture: %s was not read from the native catalog", ErrControl, name)
		}
		size, sizeKnown := d.Size.Value()
		available, availableKnown := d.Available.Value()
		if availableKnown && !available && !sizeKnown {
			return out, false, fmt.Errorf("%w: herdFurniture: the native catalog has no definition %s", ErrControl, name)
		}
		if !availableKnown || !available || !sizeKnown || size.Width < 1 || size.Height < 1 {
			usable = false
			continue
		}
		def := policy.InteriorPieceDef{Def: name, Size: domain.Cell{X: size.Width, Z: size.Height}}
		if name == policy.AnimalSleepingSpotDefinition {
			out.Spot = def
		} else {
			out.Bed = def
		}
	}
	return out, usable, nil
}

// herdStep is the projection's next barn or vet room step. known is false
// while a fact it reads (the animal census, the plan, the room or
// construction census, an animal bed's size or availability) is.
func herdStep(facts observation.ColonyProjection) (step policy.HerdStep, known bool, err error) {
	animals, ak := facts.Facts.AnimalUpkeep.Animals.Value()
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	if !ak || !pk || !rk || !ck || !sk || !census.Colony {
		return policy.HerdStep{}, false, nil
	}
	if len(animals) == 0 || len(plan.HerdRooms(policy.ModuleBarn))+len(plan.HerdRooms(policy.ModuleVetRoom)) == 0 {
		return policy.HerdStep{}, true, nil
	}
	furniture, usable, err := herdFurniture(facts)
	if err != nil || !usable {
		return policy.HerdStep{}, false, err
	}
	return policy.NextHerdStep(plan, rooms, census.Buildings, sleeping.Beds, len(animals), furniture), true, nil
}

// vetRoomReady is VetRoom.Ready: known once the plan, room census,
// construction census and sleeping census are; see policy.VetRoomReady.
func vetRoomReady(facts observation.ColonyProjection) domain.Fact[bool] {
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	census, ck := facts.Facts.CurrentConstruction.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	if !pk || !rk || !ck || !sk || !census.Colony {
		return domain.Unknown[bool]()
	}
	return policy.VetRoomReady(plan, rooms, census.Buildings, sleeping.Beds)
}

// herdRoomsOwed is the review's HerdRoomsOwed fact: known true while a barn
// or vet room step is due.
func herdRoomsOwed(facts observation.ColonyProjection) (domain.Fact[bool], error) {
	step, known, err := herdStep(facts)
	if err != nil || !known {
		return domain.Unknown[bool](), err
	}
	return domain.Known(step.Owed()), nil
}

// herdMethod names a herd step's method: the shell once per room, each bed
// slot once, per goal epoch.
func herdMethod(step policy.HerdStep) domain.MethodID {
	in := step.Room.Interior
	if step.Kind == policy.HerdPlace {
		return domain.MethodID(fmt.Sprintf("herd-place-%d-%d-%s", in.X, in.Z, step.Piece.Slot))
	}
	return domain.MethodID(fmt.Sprintf("herd-shell-%d-%d", in.X, in.Z))
}

// stageHerdRooms answers the due herd step: the room's shell through
// shellRoom, a bed through placePiece. It reads the room census itself, the
// pen steps before it did not need it.
func (r *RoutineAnimalContainmentPlanner) stageHerdRooms(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, expected observation.Identity, claims domain.Fact[[]policy.ConstructionClaim]) (RoutineAnimalContainmentResult, error) {
	reading, err := r.reviewer.observeRooms(call, r.reviewer.native, expected, claims, herdDefinitions...)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	step, known, err := herdStep(reading.Projection)
	if err != nil {
		return RoutineAnimalContainmentResult{}, err
	}
	if !known {
		return RoutineAnimalContainmentResult{Verdict: fieldUnavailable("herd_rooms")}, nil
	}
	if !step.Owed() {
		return RoutineAnimalContainmentResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	clockSchedulerLog("%s: %s %s", goal.Goal.ID, step.Role, step.Kind)
	var result RoutineBuildingResult
	switch step.Kind {
	case policy.HerdShell:
		result, err = r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, step.Room, herdMethod(step), string(step.Role))
	case policy.HerdMedical:
		result, err = r.markHerdBedMedical(call, epoch, state, goal, reading.Projection, step.Bed)
	default:
		result, err = r.building.placePiece(call, epoch, state, review, goal, reading, step.Piece, herdMethod(step))
	}
	return RoutineAnimalContainmentResult{Verdict: result.Verdict}, err
}

// markHerdBedMedical commits one CAS-gated patch flagging a vet room bed
// medical, once per bed per goal epoch. A native that cannot read bed use
// is an error, never a skipped step.
func (r *RoutineAnimalContainmentPlanner) markHerdBedMedical(call, epoch context.Context, state ControlState, goal store.GoalState, facts observation.ColonyProjection, bed string) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	native, ok := r.reviewer.native.(RoutineHospitalSource)
	if !ok {
		return RoutineBuildingResult{}, fmt.Errorf("%w: markHerdBedMedical: the native source cannot read bed use", ErrControl)
	}
	method := domain.MethodID("herd-medical-" + bed)
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Verdict: BuildingReasonUsed}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoutineBuildingResult{}, err
	}
	target, _, err := native.ReadBedUseTarget(call, boundary.Identity(state.Snapshot), bed)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if _, err = boundary.Context(target.Context, state.Snapshot); err != nil || target.Context.GetTick() < int64(facts.Identity.Tick) {
		return RoutineBuildingResult{}, fmt.Errorf("%w: markHerdBedMedical: stale bed read", ErrControl)
	}
	if target.Medical {
		return RoutineBuildingResult{Verdict: BuildingReasonUsed}, nil
	}
	patch, err := domain.NewBedMedical(bed, true)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	id := domain.MintPlanID()
	action, err := domain.NewBedUseAction(domain.ActionID(fmt.Sprintf("%s-0", id)), patch)
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		return RoutineBuildingResult{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return RoutineBuildingResult{}, err
	}
	if p.session.State() != state {
		return RoutineBuildingResult{}, fmt.Errorf("%w: markHerdBedMedical: p.session.State() != state", ErrControl)
	}
	if _, err = p.journal.CommitGoalMethod(call, goal.Goal.ID, goal.Revision, method, plan); err != nil {
		return RoutineBuildingResult{}, err
	}
	return RoutineBuildingResult{Verdict: BuildingReasonAdmitted}, nil
}
