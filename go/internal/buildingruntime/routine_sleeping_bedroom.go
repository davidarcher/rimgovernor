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
	return policy.NextBedroomStep(plan, rooms, sleeping, bedroomTargets(facts))
}

// bedroomTargets is the rooms' quality targets, so a bedroom move leaves an
// ascetic's NeverUpgrade room alone (#826); nil while the census is unknown.
func bedroomTargets(facts observation.ColonyProjection) map[string]policy.RoomTarget {
	obs, known := facts.Facts.Sleeping.Value()
	traits := sleepingTraits(facts)
	if !known || traits == nil {
		return nil
	}
	tier, _ := facts.BuildTier.Value()
	return policy.RoomQualityTargets(obs, traits, tier)
}

// bedroomsOwed is the review's BedroomsOwed fact for the projection.
// A due room quality swap (#813) owes a bedroom too.
func bedroomsOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	owed := domain.Known(false)
	if tier, known := facts.BuildTier.Value(); known && tier >= policy.BuildTierMasonry {
		owed = policy.BedroomsOwed(facts.LayoutPlan, facts.Rooms, facts.Facts.Sleeping, bedroomTargets(facts))
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

// bedroomRing is the planned room's wall ring, doors first, without the
// cells a wall or door already stands on (a neighbour's shared wall) or
// natural rock walls.
func bedroomRing(room policy.LayoutRoom, doors map[domain.Cell]bool, order []domain.Cell, facts observation.ColonyProjection) []domain.Cell {
	standing := map[domain.Cell]bool{}
	if census, known := facts.Facts.CurrentConstruction.Value(); known {
		for _, b := range census.Buildings {
			d := b.Building.Definition()
			for _, c := range b.Cells {
				// A planned door in a neighbour's standing wall replaces
				// the wall: the meal closet's door in the dining room's
				// back wall (#936).
				if shellDoor(d) || d == "Wall" && !doors[c] {
					standing[c] = true
				}
			}
		}
	}
	// Natural rock on the ring walls a dug room as it stands (#836).
	for _, c := range facts.Cells {
		if rock, known := c.NaturalRock.Value(); known && rock && !doors[c.Cell] {
			standing[c.Cell] = true
		}
	}
	in := room.Interior
	ring := []domain.Cell{}
	for _, d := range order {
		if !standing[d] {
			ring = append(ring, d)
		}
	}
	for x := in.X - 1; x <= in.X+in.Width; x++ {
		for z := in.Z - 1; z <= in.Z+in.Height; z++ {
			c := domain.Cell{X: x, Z: z}
			edge := x == in.X-1 || x == in.X+in.Width || z == in.Z-1 || z == in.Z+in.Height
			if edge && !doors[c] && !standing[c] {
				ring = append(ring, c)
			}
		}
	}
	return ring
}

// shellBedroom previews and admits the planned room's walls and door. A
// refused cell makes the slot no site this step.
func (r *RoutineSleepingUpkeepPlanner) shellBedroom(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.RoutineReading, step policy.BedroomStep) (RoutineBuildingResult, error) {
	return r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, step.Room, bedroomMethod(step.Kind, step.Room), "routine-sleeping-bedroom", bedroomShellReason(step))
}

// bedroomShellReason is a bedroom shell's short why: the colonists still
// outside a bedroom (#846).
func bedroomShellReason(step policy.BedroomStep) string {
	if step.Unhoused <= 0 {
		return ""
	}
	return fmt.Sprintf("room for %d unhoused", step.Unhoused)
}

// shellRoom previews and admits a planned room's walls and door once per
// method; prefix names the plan (the tomb shares it, #832). reason is the
// admission's short why for Operation.intent (#846).
func (b *RoutineBuildingPlanner) shellRoom(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.ColonyReading, room policy.LayoutRoom, method domain.MethodID, prefix, reason string) (RoutineBuildingResult, error) {
	p := b.reviewer.player
	facts := reading.Projection
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
	snapshot.Plan = domain.PlanID(fmt.Sprintf("%s-%x", prefix, digest[:16]))
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
	// A room planned into rock is mined out before its ring (#836).
	plan, _ := facts.LayoutPlan.Value()
	if result, handled, err := b.digPlannedRoom(call, epoch, excavationStep{state: state, review: review, goal: goal, facts: facts, read: reading}, plan, room, check); err != nil || handled {
		return result, err
	}
	stock := policy.StockObservation{Snapshot: snapshot, Tick: facts.Identity.Tick}
	// A door goes wherever the plan puts one in this ring: the room's own,
	// and a Link it or a neighbour shares (#835).
	order := plan.ShellDoors(room)
	doors := make(map[domain.Cell]bool, len(order))
	for _, d := range order {
		doors[d] = true
	}
	var selected []policy.Preview
	for i, cell := range bedroomRing(room, doors, order, facts) {
		if err := check(); err != nil {
			return RoutineBuildingResult{}, err
		}
		definition := "Wall"
		if doors[cell] {
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
		preview, _, err := b.native.PreviewBuilding(call, action, snapshot)
		if err != nil {
			return RoutineBuildingResult{}, err
		}
		v := preview.Preview
		footprint, fk := v.Footprint.Value()
		can, ck := v.CanPlace.Value()
		safe, sk := v.SafeToPlace.Value()
		if !fk || len(footprint) != 1 || footprint[0] != cell || !ck || !can || !sk || !safe {
			clockSchedulerLog("%s: %s %d,%d refused at %d,%d", goal.Goal.ID, prefix, room.Interior.X, room.Interior.Z, cell.X, cell.Z)
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
	return b.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, method: method, reason: reason, snapshot: snapshot, selected: selected, stock: stock, purpose: policy.Shelter})
}
