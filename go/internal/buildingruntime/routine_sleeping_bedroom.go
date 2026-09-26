package buildingruntime

import (
	"context"
	"crypto/sha256"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Individual bedrooms (#786). The review keeps MaintainSleeping open while a
// bedroom step is due; the sleeping planner answers it once every colonist
// owns a bed: move, furnish, then shell (policy.NextBedroomStep).

// bedroomStep is the projection's next bedroom step: none below Masonry or
// without the layout plan, the room census or the sleeping census.
func bedroomStep(facts observation.ColonyProjection) policy.BedroomStep {
	if tier, known := facts.BuildTier.Value(); !known || tier < policy.BuildTierMasonry {
		return policy.BedroomStep{}
	}
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	if !pk || !rk || !sk {
		return policy.BedroomStep{}
	}
	return policy.NextBedroomStep(plan, rooms, sleeping)
}

// bedroomsOwed is the review's BedroomsOwed fact for the projection.
// A due room quality swap (#813) owes a bedroom too.
func bedroomsOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	owed := domain.Known(false)
	if tier, known := facts.BuildTier.Value(); known && tier >= policy.BuildTierMasonry {
		owed = policy.BedroomsOwed(facts.LayoutPlan, facts.Rooms, facts.Facts.Sleeping)
	}
	if v, known := owed.Value(); known && !v {
		if _, swap := bedroomSwap(facts); swap {
			return domain.Known(true)
		}
		if _, upgrade := roomUpgrade(facts); upgrade {
			return domain.Known(true)
		}
	}
	return owed
}

// bedroomMethod names a bedroom step's method: one per planned room, so a
// room is shelled or furnished once per goal epoch.
func bedroomMethod(kind policy.BedroomStepKind, room policy.LayoutRoom) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("bedroom-%s-%d-%d", kind, room.Interior.X, room.Interior.Z))
}

// bedroomRing is the planned room's wall ring, door first, without the
// cells a wall or door already stands on (a neighbour's shared wall).
func bedroomRing(room policy.LayoutRoom, facts observation.ColonyProjection) []domain.Cell {
	standing := map[domain.Cell]bool{}
	if census, known := facts.Facts.CurrentConstruction.Value(); known {
		for _, b := range census.Buildings {
			if d := b.Building.Definition(); d == "Wall" || shellDoor(d) {
				for _, c := range b.Cells {
					standing[c] = true
				}
			}
		}
	}
	in := room.Interior
	ring := []domain.Cell{}
	if !standing[room.Door] {
		ring = append(ring, room.Door)
	}
	for x := in.X - 1; x <= in.X+in.Width; x++ {
		for z := in.Z - 1; z <= in.Z+in.Height; z++ {
			c := domain.Cell{X: x, Z: z}
			edge := x == in.X-1 || x == in.X+in.Width || z == in.Z-1 || z == in.Z+in.Height
			if edge && c != room.Door && !standing[c] {
				ring = append(ring, c)
			}
		}
	}
	return ring
}

// shellBedroom previews and admits the planned room's walls and door. A
// refused cell makes the slot no site this step.
func (r *RoutineSleepingUpkeepPlanner) shellBedroom(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, step policy.BedroomStep) (RoutineBuildingResult, error) {
	p := r.reviewer.player
	facts := reading.Projection
	method := bedroomMethod(step.Kind, step.Room)
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Reason: BuildingMethodUsed}, nil
	}
	wallDef, wok := animalContainmentDefinition(facts.Definitions, "Wall")
	doorDef, dok := animalContainmentDefinition(facts.Definitions, "Door")
	if !wok || !dok {
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	}
	wa, wak := wallDef.Available.Value()
	da, dak := doorDef.Available.Value()
	if !wak || !dak || !wa || !da {
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	}
	stuff, known := animalContainmentStuff(wallDef, doorDef)
	if !known {
		return RoutineBuildingResult{Reason: BuildingMethodUnknown}, nil
	}
	digest := sha256.Sum256([]byte(fmt.Sprintf("%s/%d/%s", goal.Goal.ID, goal.Goal.Epoch, method)))
	snapshot := state.Snapshot
	snapshot.Plan = domain.PlanID(fmt.Sprintf("routine-sleeping-bedroom-%x", digest[:16]))
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return ErrControl
		}
		return nil
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	var selected []policy.Preview
	for i, cell := range bedroomRing(step.Room, facts) {
		if err := check(); err != nil {
			return RoutineBuildingResult{}, err
		}
		definition := "Wall"
		if cell == step.Room.Door {
			definition = "Door"
		}
		building, err := domain.NewBuilding(definition, cell, domain.North, stuff)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		action, err := domain.NewBuildingAction(domain.ActionID(fmt.Sprintf("%s-%d", snapshot.Plan, i)), building)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		preview, _, err := r.native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		v := preview.Preview
		if v.Action != action || !v.Snapshot.Matches(snapshot) || !preview.Stock.Snapshot.Matches(snapshot) {
			return RoutineBuildingResult{}, ErrControl
		}
		footprint, fk := v.Footprint.Value()
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		if !fk || len(footprint) != 1 || footprint[0] != cell || !ck || !can || !sk || !safe {
			clockSchedulerLog("%s: bedroom %d,%d refused at %d,%d", goal.Goal.ID, step.Room.Interior.X, step.Room.Interior.Z, cell.X, cell.Z)
			return RoutineBuildingResult{Reason: BuildingMethodNoSpace}, nil
		}
		if err := mergeRoutineStock(&stock, preview.Stock, len(selected) == 0); err != nil {
			return RoutineBuildingResult{}, err
		}
		selected = append(selected, v)
	}
	if len(selected) == 0 {
		return RoutineBuildingResult{Reason: BuildingMethodNoSpace}, nil
	}
	return r.building.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, read: reading.ColonyReading, method: method, snapshot: snapshot, selected: selected, stock: stock, purpose: policy.Shelter, partial: true, check: check})
}
