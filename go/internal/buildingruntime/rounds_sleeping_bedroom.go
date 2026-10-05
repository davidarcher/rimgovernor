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
func bedroomStep(facts observation.ColonyProjection, stage policy.ColonyStage) policy.BedroomStep {
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	if !pk || !rk || !sk {
		return policy.BedroomStep{}
	}
	return policy.NextBedroomStep(plan, rooms, sleeping, bedroomTargets(facts), sleepingTraits(facts), suitePressure(facts), bedroomGate(facts, stage))
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

// suiteTargets is the suites the generator keeps and adds for plan (#1216) and the
// claims behind them; nil while the room, sleeping or work census is
// unknown.
func suiteTargets(facts observation.ColonyProjection, plan policy.LayoutPlan, stage policy.ColonyStage) ([]float64, []policy.SuiteClaim) {
	rooms, rk := facts.Rooms.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	traits := sleepingTraits(facts)
	if !rk || !sk || traits == nil {
		return nil, nil
	}
	targets := bedroomTargets(facts)
	claims := policy.SuiteClaims(plan, rooms, sleeping, targets, traits, suitePressure(facts), bedroomGate(facts, stage))
	return policy.SuiteTargets(plan, rooms, sleeping, claims), claims
}

// upgradeTargets is targets less the rooms of pawns owed a suite (#1257,
// suite first): those rooms get no in-place quality upgrade.
func upgradeTargets(facts observation.ColonyProjection, targets map[string]policy.RoomTarget, stage policy.ColonyStage) map[string]policy.RoomTarget {
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	sleeping, sk := facts.Facts.Sleeping.Value()
	traits := sleepingTraits(facts)
	if !pk || !rk || !sk || traits == nil {
		return targets
	}
	claims := policy.SuiteClaims(plan, rooms, sleeping, bedroomTargets(facts), traits, suitePressure(facts), bedroomGate(facts, stage))
	return policy.UpgradeTargets(targets, sleeping, claims)
}

// bedroomsOwed is the review's BedroomsOwed fact for the projection.
// A due room quality swap (#813) or wing migration (#1244) owes a bedroom
// too, so MaintainHousing stays open until it is done.
func bedroomsOwed(facts observation.ColonyProjection, stage policy.ColonyStage) domain.Fact[bool] {
	owed := policy.BedroomsOwed(facts.LayoutPlan, facts.Rooms, facts.Facts.Sleeping, bedroomTargets(facts), sleepingTraits(facts), suitePressure(facts), bedroomGate(facts, stage))
	if v, known := owed.Value(); known && !v {
		if _, swap := bedroomSwap(facts); swap {
			return domain.Known(true)
		}
		if migrateStep(facts).Kind != policy.BedroomNone {
			return domain.Known(true)
		}
		if _, upgrade := roomUpgrade(facts, stage); upgrade {
			return domain.Known(true)
		}
		if _, bed := companionBed(facts); bed {
			return domain.Known(true)
		}
		if throneStep(facts).Owed() || childRoomStep(facts).Owed() {
			return domain.Known(true)
		}
	}
	return owed
}

// bedroomReconcileName prefixes a bedroom room's reconcile methods. The
// "bedroom-shell-<x>-<z>" stem is the one the sleeping acceptance cases read
// the journal by; the build side appends the wave (removal, install, build).
func bedroomReconcileName(room policy.PlannedRoom) string {
	return fmt.Sprintf("bedroom-shell-%d-%d", room.Interior.X, room.Interior.Z)
}

// reconcileBedroom answers a BedroomReconcile through the shared build side
// (#2115): the room's ring, doors and floor and its bed are reconciled to the
// plan and the bedroom template, the bed installed from packed stock first and
// built on site only when none is stored. A vacant bed left in the starter
// shell is packed to become that stock; a placement native refuses means wait.
func (r *RoundsSleepingUpkeepPlanner) reconcileBedroom(call, epoch context.Context, stock *packedStock, state ControlState, review store.Rounds, goal store.WorkOwner, reading observation.RoundsReading, step policy.BedroomStep) (RoundsBuildingResult, error) {
	facts := reading.Projection
	// A suite is only started with its whole ring in stock (#1216).
	if step.Room.Role == policy.PlannedSuite {
		in := step.Room.Interior
		_, walls, _ := facts.StockedStuff("Wall")
		if walls < int64(2*(in.Width+in.Height)+4) {
			return RoundsBuildingResult{Verdict: BuildingSuiteStock}, nil
		}
	}
	request, err := sleepingRequest(facts, review)
	if err != nil {
		return RoundsBuildingResult{}, err
	}
	bed, method := policy.SleepingDefinition(facts.Shapes.Furniture, request.Definitions, request.Stocked, false, true)
	if method == policy.SleepingUnknown {
		return RoundsBuildingResult{Verdict: fieldUnavailable("bed_definitions")}, nil
	}
	if method != policy.SleepingBuild {
		return RoundsBuildingResult{Verdict: BuildingSleepingUnavailable}, nil
	}
	rooms, known := facts.Rooms.Value()
	if !known {
		return RoundsBuildingResult{Verdict: fieldUnavailable("room_census")}, nil
	}
	// A wing's bedrooms are reconciled together (#2133); a suite or a migration
	// step names its one room.
	batch := step.Rooms
	if len(batch) == 0 {
		batch = []policy.PlannedRoom{step.Room}
	}
	var rrs []roomReconcile
	var anyStanding bool
	for _, room := range batch {
		template, ok := policy.BedroomTemplate(room, rooms.Shapes, bed)
		if !ok {
			// A room the template does not fit is left out of the batch.
			continue
		}
		// Census: the shelter bed packs only once a room is roofed.
		if _, standing := policy.CensusRoomIn(room, rooms); standing {
			anyStanding = true
		}
		rrs = append(rrs, roomReconcile{room: room, template: template, name: bedroomReconcileName(room), reason: bedroomShellReason(step)})
	}
	if len(rrs) == 0 {
		return RoundsBuildingResult{Verdict: noSpace("bedroom_template")}, nil
	}
	// Only once a room stands: until then the shelter's bed keeps its sleeper.
	// One bed is packed for the whole batch, never one per room.
	if anyStanding {
		if result, due, err := r.packShellBed(call, epoch, stock, state, goal, reading, bed); due || err != nil {
			return result, err
		}
	}
	return r.building.reconcileRooms(call, epoch, state, review, goal, reading, stock, rrs)
}

// bedroomShellReason is a bedroom shell's short why: the colonists still
// outside a bedroom (#846).
func bedroomShellReason(step policy.BedroomStep) string {
	if step.Unhoused <= 0 {
		return ""
	}
	return fmt.Sprintf("room for %d unhoused", step.Unhoused)
}

// shellStuff chooses the wall's and the door's stuff; refusal names what is
// unavailable when it cannot.
type shellStuff func(wall, door observation.PlanningDefinition) (wallStuff, doorStuff string, refusal Verdict, ok bool)

// sharedShellStuff builds both from the one stuff the colony can raise a
// shell from (shellSharedStuff).
func sharedShellStuff(facts observation.ColonyProjection, wall, door observation.PlanningDefinition) (string, string, Verdict, bool) {
	stuff, known := shellSharedStuff(facts, wall, door)
	if !known {
		return "", "", fieldUnavailable("wall_door_stuff"), false
	}
	return stuff, stuff, Verdict{}, true
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
