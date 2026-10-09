package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// burialDefinitions are the definitions the burial step reads availability
// and stuff for.
var burialDefinitions = []string{"Wall", "Door", policy.GraveDefinition}

// reviewStrangers sets the projection's stranger staging (#2336): the live
// KnowBuriedInSarcophagus stacks from the mood census and whether the next
// sarcophagus is funded from stock net of the MaintainResource floors. Unread
// thoughts leave the zero value, which stages no stranger.
func (r *Rounder) reviewStrangers(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) error {
	projection.Strangers = policy.StrangerTomb{}
	live, known := policy.KnowBuriedStacks(projection.Facts.MoodPawns)
	if !known {
		return nil
	}
	// At the cap nothing is staged, but the live count still proves the memory
	// fired for the disposal (#2337).
	if live >= policy.StrangerTombStackCap {
		projection.Strangers.Live = live
		return nil
	}
	targets := r.resourceTargets(snapshot)
	projection.Strangers = policy.StrangerTomb{Live: live, Funded: projection.StuffFunded(projection.Shapes.Furniture.Sarcophagus, targets)}
	return nil
}

// tombStep is the projection's next tomb or grave step (#832, #857, #2196); none while a
// fact is unknown.
func tombStep(facts observation.ColonyProjection) policy.TombStep {
	if owed, known := tombOwed(facts).Value(); !known || !owed {
		return policy.TombStep{}
	}
	available, _ := sarcophagusAvailable(facts).Value()
	plan, _ := facts.LayoutPlan.Value()
	waste, _ := facts.Facts.Waste.Value()
	built, _ := facts.Facts.CurrentConstruction.Value()
	return policy.NextTombStep(plan, waste, built.Buildings, facts.Shapes, available, facts.Strangers)
}

// tombOwed is the review's TombOwed fact for the projection.
func tombOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	return policy.TombOwed(facts.Shapes, sarcophagusAvailable(facts), facts.LayoutPlan, facts.Rooms, facts.Facts.Waste, facts.Facts.CurrentConstruction, facts.Strangers)
}

// sarcophagusAvailable is the sarcophagus's availability, false when
// native reports no stuff to make one from (#857).
func sarcophagusAvailable(facts observation.ColonyProjection) domain.Fact[bool] {
	for _, d := range facts.Definitions {
		if d.Name != facts.Shapes.Furniture.Sarcophagus {
			continue
		}
		if d.Stuffed && len(d.StuffOptions) == 0 {
			return domain.Known(false)
		}
		return d.Available
	}
	return domain.Unknown[bool]()
}

// tombsFull reports every planned tomb's sarcophagus slots taken while a
// dead colonist waits (#857): the layout owes another tomb room.
func tombsFull(plan policy.LayoutPlan, facts observation.ColonyProjection) bool {
	available, ak := sarcophagusAvailable(facts).Value()
	waste, wk := facts.Facts.Waste.Value()
	built, bk := facts.Facts.CurrentConstruction.Value()
	if !ak || !available || !wk || !bk || !built.Colony {
		return false
	}
	return policy.NextTombStep(plan, waste, built.Buildings, facts.Shapes, true, facts.Strangers).Kind == policy.TombFull
}

// plannedMorgue is the planned morgue a waiting human corpse owes a shell
// (#1820); false while the plan, rooms or waste census is unread.
func plannedMorgue(facts observation.ColonyProjection) (policy.PlannedRoom, bool) {
	plan, pk := facts.LayoutPlan.Value()
	waste, wk := facts.Facts.Waste.Value()
	ground, gk := colonyGround(facts)
	if !pk || !wk || !gk {
		return policy.PlannedRoom{}, false
	}
	return policy.MorgueRoomOwed(plan, plan.GroundWithRock(ground, naturalRock(facts)), waste)
}

// burialOwed is the review's BurialOwed fact: the tomb, a grave or the morgue
// is owed.
func burialOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	tomb, tk := tombOwed(facts).Value()
	if _, morgue := plannedMorgue(facts); morgue || tk && tomb {
		return domain.Known(true)
	}
	if tk {
		return domain.Known(false)
	}
	return domain.Unknown[bool]()
}

// burialCensus is the stockpile planner's burial reading: nil while the waste
// or construction census is unread.
func burialCensus(facts observation.ColonyProjection) *policy.BurialCensus {
	waste, wk := facts.Facts.Waste.Value()
	built, bk := facts.Facts.CurrentConstruction.Value()
	available, ak := sarcophagusAvailable(facts).Value()
	if !wk || !bk || !built.Colony || !ak {
		return nil
	}
	return &policy.BurialCensus{Waste: waste, Built: built.Buildings, Shapes: facts.Shapes, Sarcophagus: available}
}

// plannedMealCloset is the planned meal closet MaintainRefrigeration owes a
// shell (#936), once the colony can build coolers; known false without
// coolers, unknown while the plan or room census is.
func plannedMealCloset(facts observation.ColonyProjection) (policy.PlannedRoom, domain.Fact[bool]) {
	available, ak := facts.DefinitionAvailable("Cooler").Value()
	if ak && !available {
		return policy.PlannedRoom{}, domain.Known(false)
	}
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	if !ak || !pk || !rk {
		return policy.PlannedRoom{}, domain.Unknown[bool]()
	}
	room, owed := plan.MealClosetOwed(rooms)
	return room, domain.Known(owed)
}

// mealClosetOwed is the review's MealClosetOwed fact.
func mealClosetOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	_, owed := plannedMealCloset(facts)
	return owed
}

// warmCoolingRooms is the review's WarmRooms fact: the standing warm tombs,
// morgues and meal closets, once the colony can build coolers.
func warmCoolingRooms(facts observation.ColonyProjection) domain.Fact[[]string] {
	return policy.WarmCoolingRooms(facts.DefinitionAvailable("Cooler"), facts.LayoutPlan, facts.Rooms)
}
