package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// wasteDefinitions are the definitions the waste steps read availability
// and stuff for.
var wasteDefinitions = []string{"Wall", "Door", policy.GraveDefinition}

// tombStep is the projection's next tomb step (#832, #857); none while a
// fact is unknown.
func tombStep(facts observation.ColonyProjection) policy.TombStep {
	if owed, known := tombOwed(facts).Value(); !known || !owed {
		return policy.TombStep{}
	}
	available, _ := sarcophagusAvailable(facts).Value()
	plan, _ := facts.LayoutPlan.Value()
	waste, _ := facts.Facts.Waste.Value()
	built, _ := facts.Facts.CurrentConstruction.Value()
	return policy.NextTombStep(plan, waste, built.Buildings, facts.Shapes, available)
}

// tombOwed is the review's TombOwed fact for the projection.
func tombOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	return policy.TombOwed(facts.Shapes, sarcophagusAvailable(facts), facts.LayoutPlan, facts.Rooms, facts.Facts.Waste, facts.Facts.CurrentConstruction)
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
	return policy.NextTombStep(plan, waste, built.Buildings, facts.Shapes, true).Kind == policy.TombFull
}

// strangerButchery is whether the human butchery would take a fresh stranger
// corpse now (#1811); unread benches mean no.
func strangerButchery(facts observation.ColonyProjection) bool {
	return policy.HumanButcheryOpen(facts.ProductionBenches, facts.Facts.IdeologyRead())
}

// plannedMorgue is the planned morgue a waiting fresh stranger corpse owes
// a shell (#1820); false while the plan, rooms or waste census is unread.
func plannedMorgue(facts observation.ColonyProjection) (policy.PlannedRoom, bool) {
	plan, pk := facts.LayoutPlan.Value()
	waste, wk := facts.Facts.Waste.Value()
	ground, gk := colonyGround(facts)
	if !pk || !wk || !gk {
		return policy.PlannedRoom{}, false
	}
	return policy.MorgueRoomOwed(plan, plan.GroundWithRock(ground, naturalRock(facts)), waste, strangerButchery(facts))
}

// corpsesOwed is the review's CorpsesOwed fact: the tomb, the morgue or the
// incinerator is owed.
func corpsesOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	tomb, tk := tombOwed(facts).Value()
	burn, known := incineratorOwed(facts).Value()
	if _, morgue := plannedMorgue(facts); morgue || known && burn || tk && tomb {
		return domain.Known(true)
	}
	if tk {
		return domain.Known(false)
	}
	return domain.Unknown[bool]()
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

// warmTombs is the review's TombsWarm fact (#840): warm tombs holding a
// colonist, once the colony can build coolers.
func warmTombs(facts observation.ColonyProjection) domain.Fact[[]string] {
	return policy.WarmTombs(facts.Shapes, facts.DefinitionAvailable("Cooler"), facts.LayoutPlan, facts.Rooms, facts.Facts.Waste, facts.Facts.CurrentConstruction)
}
