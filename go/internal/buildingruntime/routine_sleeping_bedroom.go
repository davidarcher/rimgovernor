package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Individual bedrooms (#786). The review keeps MaintainHousing open while a
// bedroom step is due; the sleeping planner answers it once every colonist
// owns a bed: move, furnish, then shell (policy.NextBedroomStep).

// bedroomStep is the projection's next bedroom step: none without the
// layout plan, the room census or the sleeping census. A tribe builds its
// wood bedrooms at Camp tier too (#1182).
func bedroomStep(facts observation.ColonyProjection) policy.BedroomStep {
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	if !pk || !rk || !sk {
		return policy.BedroomStep{}
	}
	return policy.NextBedroomStep(plan, rooms, sleeping, bedroomTargets(facts), sleepingTraits(facts), suitePressure(facts))
}

// migrateStep is the projection's next wing migration step (#1219): none
// without the layout plan, the room census or the sleeping census.
func migrateStep(facts observation.ColonyProjection) policy.BedroomStep {
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	if !pk || !rk || !sk {
		return policy.BedroomStep{}
	}
	return policy.NextMigrateStep(plan, rooms, sleeping)
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
	return policy.RoomQualityTargets(obs, traits, tier, facts.Impressiveness)
}

// suiteTargets is the suites Grow keeps and adds for plan (#1216) and the
// claims behind them; nil while the room, sleeping or work census is
// unknown.
func suiteTargets(facts observation.ColonyProjection, plan policy.LayoutPlan) ([]float64, []policy.SuiteClaim) {
	rooms, rk := facts.Rooms.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	traits := sleepingTraits(facts)
	if !rk || !sk || traits == nil {
		return nil, nil
	}
	targets := bedroomTargets(facts)
	claims := policy.SuiteClaims(plan, rooms, sleeping, targets, traits, suitePressure(facts))
	return policy.SuiteTargets(plan, rooms, sleeping, targets, claims), claims
}

// upgradeTargets is targets less the rooms of pawns owed a suite (#1257,
// suite first): those rooms get no in-place quality upgrade.
func upgradeTargets(facts observation.ColonyProjection, targets map[string]policy.RoomTarget) map[string]policy.RoomTarget {
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	traits := sleepingTraits(facts)
	if !pk || !rk || !sk || traits == nil {
		return targets
	}
	claims := policy.SuiteClaims(plan, rooms, sleeping, bedroomTargets(facts), traits, suitePressure(facts))
	return policy.UpgradeTargets(targets, sleeping, claims)
}

// bedroomsOwed is the review's BedroomsOwed fact for the projection.
// A due room quality swap (#813) or wing migration (#1244) owes a bedroom
// too, so MaintainHousing stays open until it is done.
func bedroomsOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	owed := policy.BedroomsOwed(facts.LayoutPlan, facts.Rooms, facts.Facts.Sleeping, bedroomTargets(facts), sleepingTraits(facts), suitePressure(facts))
	if v, known := owed.Value(); known && !v {
		if _, swap := bedroomSwap(facts); swap {
			return domain.Known(true)
		}
		if migrateStep(facts).Kind != policy.BedroomNone {
			return domain.Known(true)
		}
		if _, upgrade := roomUpgrade(facts); upgrade {
			return domain.Known(true)
		}
		if _, bed := companionBed(facts); bed {
			return domain.Known(true)
		}
		if _, grow := suiteGrowth(facts); grow {
			return domain.Known(true)
		}
		if throneStep(facts).Owed() || childRoomStep(facts).Owed() {
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
	// A suite is only started with its whole ring in stock (#1216).
	if step.Room.Role == policy.ModuleSuite {
		in := step.Room.Interior
		_, walls, _ := reading.Projection.StockedStuff("Wall")
		if walls < int64(2*(in.Width+in.Height)+4) {
			return RoutineBuildingResult{Verdict: BuildingSuiteStock}, nil
		}
	}
	return r.building.shellRoom(call, epoch, state, review, goal, reading.ColonyReading, step.Room, bedroomMethod(step.Kind, step.Room), bedroomShellReason(step))
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
func (b *RoutineBuildingPlanner) shellRoom(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.ColonyReading, room policy.LayoutRoom, method domain.MethodID, reason string) (RoutineBuildingResult, error) {
	return b.shellRoomOf(call, epoch, state, review, goal, reading, room, method, reason, sharedShellStuff)
}

// shellStuff chooses the wall's and the door's stuff; refusal names what is
// unavailable when it cannot.
type shellStuff func(wall, door observation.PlanningDefinition) (wallStuff, doorStuff string, refusal Verdict, ok bool)

// sharedShellStuff builds both from the one cheapest stuff they share.
func sharedShellStuff(wall, door observation.PlanningDefinition) (string, string, Verdict, bool) {
	stuff, known := animalContainmentStuff(wall, door)
	if !known {
		return "", "", fieldUnavailable("wall_door_stuff"), false
	}
	return stuff, stuff, Verdict{}, true
}

// shellRoomOf is shellRoom with the walls' and door's stuff chosen by stuff.
func (b *RoutineBuildingPlanner) shellRoomOf(call, epoch context.Context, state ControlState, review store.RoutineReview, goal store.GoalState, reading observation.ColonyReading, room policy.LayoutRoom, method domain.MethodID, reason string, choose shellStuff) (RoutineBuildingResult, error) {
	p := b.reviewer.player
	facts := reading.Projection
	if _, err := p.journal.LoadGoalMethod(call, goal.Goal.ID, goal.Goal.Epoch, method); err == nil {
		return RoutineBuildingResult{Verdict: BuildingReasonUsed}, nil
	}
	wallDef, wok := animalContainmentDefinition(facts.Definitions, policy.ShellWallDefinition)
	doorDef, dok := animalContainmentDefinition(facts.Definitions, policy.ShellDoorDefinition)
	if !wok || !dok {
		return RoutineBuildingResult{Verdict: fieldUnavailable("wall_door_definitions")}, nil
	}
	wa, wak := wallDef.Available.Value()
	da, dak := doorDef.Available.Value()
	if !wak || !dak || !wa || !da {
		return RoutineBuildingResult{Verdict: fieldUnavailable("wall_door_availability")}, nil
	}
	wallStuff, doorStuff, refusal, known := choose(wallDef, doorDef)
	if !known {
		return RoutineBuildingResult{Verdict: refusal}, nil
	}
	snapshot := state.Snapshot
	snapshot.Plan = domain.MintPlanID()
	snapshot.Revision = 1
	check := func() error {
		if err := p.current(call, epoch); err != nil {
			return err
		}
		if p.session.State() != state {
			return fmt.Errorf("%w: shellRoom: p.session.State() != state", ErrControl)
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
		definition, stuff := policy.ShellWallDefinition, wallStuff
		if doors[cell] {
			definition, stuff = policy.ShellDoorDefinition, doorStuff
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
			clockSchedulerLog("%s: %s %d,%d refused at %d,%d", goal.Goal.ID, method, room.Interior.X, room.Interior.Z, cell.X, cell.Z)
			return RoutineBuildingResult{Verdict: BuildingReasonNoSpace}, nil
		}
		if err := mergeRoutineStock(&stock, preview.Stock, len(selected) == 0); err != nil {
			return RoutineBuildingResult{}, err
		}
		selected = append(selected, v)
	}
	if len(selected) == 0 {
		return RoutineBuildingResult{Verdict: BuildingReasonNoSpace}, nil
	}
	return b.admitPreviews(call, epoch, routineAdmission{state: state, review: review, goal: goal, facts: facts, method: method, reason: reason, snapshot: snapshot, selected: selected, stock: stock, purpose: policy.Shelter})
}

// suitePressure orders the suite queue by the pawns' current bedroom,
// space and jealousy thoughts (#1217); nil (pawn-id order) while the mood
// census is unknown.
func suitePressure(facts observation.ColonyProjection) map[policy.PawnID]float64 {
	pawns, known := facts.Facts.MoodPawns.Value()
	if !known {
		return nil
	}
	return policy.SuitePressure(pawns)
}
