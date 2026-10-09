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

// The barn and vet room: once the pen stands, MaintainAnimalContainment
// reconciles each planned herd room (ring, door, animal beds; policy.NextHerdStep
// over reconcileRoom) and flags the vet room's beds medical. The review holds the goal open while a step is due
// (HerdRoomsOwed), so a herd that outgrows its beds is topped up.

// herdDefinitions are the definitions the herd steps read availability,
// stuff and size for.
var herdDefinitions = []string{"Wall", "Door"}

// herdFurniture reads the two animal bed shapes and the barn's heater from the live catalog. A
// definition the catalog lacks (a wrong defName, or a game without it) is an
// error, never a quiet skip; one that exists but is not buildable yet, or
// has no size read, reports false.
func herdFurniture(facts observation.ColonyProjection) (policy.HerdFurniture, bool, error) {
	var out policy.HerdFurniture
	usable := true
	for _, name := range []string{facts.Shapes.Furniture.AnimalSpot, facts.Shapes.Furniture.AnimalBed} {
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
		if name == facts.Shapes.Furniture.AnimalSpot {
			out.Spot = def
		} else {
			out.Bed = def
		}
	}
	// The barn's heater is optional like the containment lamp: until its
	// research is done or its size is read, the barn is furnished without it
	// (a definition the catalog lacks is still an error).
	if name := facts.Shapes.Furniture.Heater; name != "" {
		d, found := animalContainmentDefinition(facts.Definitions, name)
		if !found {
			return out, false, fmt.Errorf("%w: herdFurniture: %s was not read from the native catalog", ErrControl, name)
		}
		size, sizeKnown := d.Size.Value()
		if available, known := d.Available.Value(); known && available && sizeKnown && size.Width == 1 && size.Height == 1 {
			out.Heater = policy.InteriorPieceDef{Def: name, Size: domain.Cell{X: 1, Z: 1}}
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
	if len(animals) == 0 || len(plan.HerdRooms(policy.PlannedBarn))+len(plan.HerdRooms(policy.PlannedVetRoom)) == 0 {
		return policy.HerdStep{}, true, nil
	}
	furniture, usable, err := herdFurniture(facts)
	if err != nil || !usable {
		return policy.HerdStep{}, false, err
	}
	ground, gk := colonyGround(facts)
	if !gk {
		return policy.HerdStep{}, false, nil
	}
	return policy.NextHerdStep(plan, rooms, plan.GroundWithRock(ground, naturalRock(facts)), census.Buildings, sleeping.Beds, len(animals), furniture), true, nil
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
	return policy.VetRoomReady(plan, rooms, census.Buildings, sleeping.Beds, facts.Shapes.Furniture.AnimalBed)
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

// stageHerdRooms answers the due herd step: the room through the shared build
// side (reconcileRoom: the ring, door and beds the plan and the template still
// owe, installed from packed stock first; a lost wall is rebuilt like a first
// shell) or a vet bed's medical flag. It reads the room census itself, the pen
// steps before it did not need it.
func (r *RoundsAnimalContainmentPlanner) stageHerdRooms(call, epoch context.Context, state ControlState, review store.Rounds, goal store.StandardState, expected observation.Identity, claims domain.Fact[[]policy.ConstructionClaim]) (RoundsAnimalContainmentResult, error) {
	reading, err := r.reviewer.observeRooms(call, r.reviewer.native, expected, claims, herdDefinitions...)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	step, known, err := herdStep(reading.Projection)
	if err != nil {
		return RoundsAnimalContainmentResult{}, err
	}
	if !known {
		return RoundsAnimalContainmentResult{Verdict: fieldUnavailable("herd_rooms")}, nil
	}
	if !step.Owed() {
		return RoundsAnimalContainmentResult{Verdict: BuildingReasonNoDeficit}, nil
	}
	var result RoundsBuildingResult
	switch step.Kind {
	case policy.HerdMedical:
		result, err = r.markHerdBedMedical(call, epoch, state, goal, reading.Projection, step.Bed)
	default:
		in := step.Room.Interior
		stock := newPackedStock(r.reviewer.native, boundary.Identity(state.Snapshot))
		result, err = r.building.reconcileRoom(call, epoch, state, review, goal, reading, stock, roomReconcile{
			room: step.Room, template: step.Template,
			name: fmt.Sprintf("herd-%d-%d", in.X, in.Z), reason: string(step.Role),
		})
	}
	return RoundsAnimalContainmentResult{Verdict: result.Verdict}, err
}

// markHerdBedMedical commits one CAS-gated patch flagging a vet room bed
// medical, once per bed per Episode. A native that cannot read bed use
// is an error, never a skipped step.
func (r *RoundsAnimalContainmentPlanner) markHerdBedMedical(call, epoch context.Context, state ControlState, goal store.StandardState, facts observation.ColonyProjection, bed string) (RoundsBuildingResult, error) {
	p := r.reviewer.player
	native, ok := r.reviewer.native.(RoundsHospitalSource)
	if !ok {
		return RoundsBuildingResult{}, fmt.Errorf("%w: markHerdBedMedical: the native source cannot read bed use", ErrControl)
	}
	method := domain.MethodID("herd-medical-" + bed)
	if _, err := p.journal.LoadMethod(call, goal.Standard.ID, goal.Standard.Episode, method); err == nil {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "herd_bed_medical_method")}, nil
	} else if !errors.Is(err, store.ErrNotFound) {
		return RoundsBuildingResult{}, err
	}
	target, _, err := native.ReadBedUseTarget(call, boundary.Identity(state.Snapshot), bed)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if _, err = boundary.Context(target.Context, state.Snapshot); err != nil || target.Context.GetTick() < int64(facts.Identity.Tick) {
		return RoundsBuildingResult{}, fmt.Errorf("%w: markHerdBedMedical: stale bed read", ErrControl)
	}
	if target.Medical {
		return RoundsBuildingResult{Verdict: waitFor(WaitMethodUsed, "herd_bed_medical")}, nil
	}
	patch, err := domain.NewBedMedical(bed, true)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	if err = p.commitBedPatch(call, epoch, state, goal, method, patch, nil); err != nil {
		return RoundsBuildingResult{}, err
	}
	return RoundsBuildingResult{Verdict: BuildingReasonAdmitted}, nil
}
