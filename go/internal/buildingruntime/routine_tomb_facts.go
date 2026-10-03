package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// wasteDefinitions are the definitions the waste steps read availability
// and stuff for.
var wasteDefinitions = []string{"Wall", "Door", policy.SarcophagusDefinition, policy.GraveDefinition, policy.CrematoriumDefinition}

// tombStep is the projection's next tomb step (#832, #857); none while a
// fact is unknown.
func tombStep(facts observation.ColonyProjection) policy.TombStep {
	if owed, known := tombOwed(facts).Value(); !known || !owed {
		return policy.TombStep{}
	}
	available, _ := sarcophagusAvailable(facts).Value()
	plan, _ := facts.LayoutPlan.Value()
	rooms, _ := facts.Rooms.Value()
	waste, _ := facts.Facts.Waste.Value()
	built, _ := facts.Facts.CurrentConstruction.Value()
	return policy.NextTombStep(plan, rooms, waste, built.Buildings, available)
}

// tombOwed is the review's TombOwed fact for the projection.
func tombOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	return policy.TombOwed(sarcophagusAvailable(facts), facts.LayoutPlan, facts.Rooms, facts.Facts.Waste, facts.Facts.CurrentConstruction)
}

// sarcophagusAvailable is the sarcophagus's availability, false when
// native reports no stuff to make one from (#857).
func sarcophagusAvailable(facts observation.ColonyProjection) domain.Fact[bool] {
	for _, d := range facts.Definitions {
		if d.Name != policy.SarcophagusDefinition {
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
	return policy.NextTombStep(plan, policy.RoomObservation{}, waste, built.Buildings, true).Kind == policy.TombFull
}

// cremationStep is the projection's next cremation step (#833); none
// while the crematorium is unavailable or a fact is unknown.
func cremationStep(facts observation.ColonyProjection) policy.CremationStep {
	if owed, known := cremationOwed(facts).Value(); !known || !owed {
		return policy.CremationStep{}
	}
	plan, _ := facts.LayoutPlan.Value()
	rooms, _ := facts.Rooms.Value()
	waste, _ := facts.Facts.Waste.Value()
	built, _ := facts.Facts.CurrentConstruction.Value()
	return policy.NextCremationStep(plan, rooms, waste, built.Buildings, strangerButchery(facts))
}

// strangerButchery is whether the human butchery would take a fresh stranger
// corpse now (#1811); unread benches mean no.
func strangerButchery(facts observation.ColonyProjection) bool {
	return policy.HumanButcheryOpen(facts.ProductionBenches, facts.Facts.Ideology)
}

func cremationOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	return policy.CremationOwed(facts.DefinitionAvailable(policy.CrematoriumDefinition), facts.LayoutPlan, facts.Rooms, facts.Facts.Waste, facts.Facts.CurrentConstruction, strangerButchery(facts))
}

// corpsesOwed is the review's CorpsesOwed fact: the tomb or the
// crematorium is owed.
func corpsesOwed(facts observation.ColonyProjection) domain.Fact[bool] {
	tomb, tk := tombOwed(facts).Value()
	cremation, ck := cremationOwed(facts).Value()
	if tk && tomb || ck && cremation {
		return domain.Known(true)
	}
	if tk && ck {
		return domain.Known(false)
	}
	return domain.Unknown[bool]()
}

// plannedMealCloset is the planned meal closet MaintainRefrigeration owes a
// shell (#936), once the colony can build coolers; known false without
// coolers, unknown while the plan or room census is.
func plannedMealCloset(facts observation.ColonyProjection) (policy.LayoutRoom, domain.Fact[bool]) {
	available, ak := facts.DefinitionAvailable("Cooler").Value()
	if ak && !available {
		return policy.LayoutRoom{}, domain.Known(false)
	}
	plan, pk := facts.LayoutPlan.Value()
	rooms, rk := facts.Rooms.Value()
	if !ak || !pk || !rk {
		return policy.LayoutRoom{}, domain.Unknown[bool]()
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
	return policy.WarmTombs(facts.DefinitionAvailable("Cooler"), facts.LayoutPlan, facts.Rooms, facts.Facts.Waste, facts.Facts.CurrentConstruction)
}
