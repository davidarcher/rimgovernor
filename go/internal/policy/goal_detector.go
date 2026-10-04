package policy

import "fmt"

// FactFamily names the observation facts native reports changed together
// (ObservationInvalidated): the unit a detector declares as an input.
type FactFamily string

const (
	FactDefinitions FactFamily = "definitions"
	FactWorld       FactFamily = "world"
	FactIdentity    FactFamily = "identity"
	FactColony      FactFamily = "colony"
	FactPawns       FactFamily = "pawns"
	FactEmergency   FactFamily = "emergency"
	FactRooms       FactFamily = "rooms"
	FactResearch    FactFamily = "research"
)

// FactFamilies lists every family, in wire order.
func FactFamilies() []FactFamily {
	return []FactFamily{FactDefinitions, FactWorld, FactIdentity, FactColony, FactPawns, FactEmergency, FactRooms, FactResearch}
}

// GoalDetector declares one GoalID: its concept, its colony area and the fact
// families its detection reads (#1907). The registry is the only place a
// GoalID is classified.
type GoalDetector struct {
	Goal    GoalID
	Concept Concept
	Domain  Domain
	Inputs  []FactFamily
}

var goalDetectors = []GoalDetector{
	{ActiveCombat, ConceptResponse, DomainMilitary, []FactFamily{FactEmergency, FactPawns}},
	{CriticalMedicine, ConceptResponse, DomainMedical, []FactFamily{FactPawns, FactEmergency}},
	{RestoreWorkers, ConceptResponse, DomainPeople, []FactFamily{FactPawns, FactColony}},
	{AnswerDialog, ConceptResponse, DomainSystem, []FactFamily{FactEmergency}},
	{ConfirmColonyNames, ConceptResponse, DomainSystem, []FactFamily{FactIdentity}},
	{RecoverDisasterServices, ConceptResponse, DomainUpkeep, []FactFamily{FactEmergency, FactColony}},
	{TradeWithCaravan, ConceptResponse, DomainUpkeep, []FactFamily{FactWorld, FactColony}},
	{EnsureMood, ConceptResponse, DomainPeople, []FactFamily{FactPawns}},
	{AllowStartingSupplies, ConceptProject, DomainUpkeep, []FactFamily{FactColony}},
	{EnsureCooking, ConceptProject, DomainFood, []FactFamily{FactColony, FactDefinitions}},
	{MaintainButcherSpot, ConceptProject, DomainFood, []FactFamily{FactColony, FactRooms}},
	{EnsureBasicPower, ConceptProject, DomainIndustry, []FactFamily{FactColony, FactDefinitions}},
	{EnsureWorkAssignments, ConceptProject, DomainPeople, []FactFamily{FactPawns}},
	{EnsureResearch, ConceptProject, DomainIndustry, []FactFamily{FactResearch, FactPawns}},
	{EnsureDefensiveLayout, ConceptProject, DomainMilitary, []FactFamily{FactColony, FactWorld}},
	{ClearAncientShrine, ConceptProject, DomainMilitary, []FactFamily{FactColony, FactWorld}},
	{MaintainWaste, ConceptStandard, DomainUpkeep, []FactFamily{FactColony}},
	{RemoveBlight, ConceptStandard, DomainFood, []FactFamily{FactColony}},
	{ManagePollution, ConceptStandard, DomainUpkeep, []FactFamily{FactColony, FactWorld}},
	{EnsureMechCharger, ConceptStandard, DomainUpkeep, []FactFamily{FactColony, FactPawns}},
	{MaintainStockpiles, ConceptStandard, DomainUpkeep, []FactFamily{FactColony}},
	{TidyLayout, ConceptStandard, DomainUpkeep, []FactFamily{FactRooms, FactColony}},
	{ClearHomeObstructions, ConceptStandard, DomainUpkeep, []FactFamily{FactColony}},
	{EnsureFoodSupply, ConceptStandard, DomainFood, []FactFamily{FactColony, FactPawns}},
	{EnsureBasicDefense, ConceptStandard, DomainMilitary, []FactFamily{FactColony, FactEmergency}},
	{EnsureTemperatureSafety, ConceptStandard, DomainShelter, []FactFamily{FactColony, FactPawns, FactWorld}},
	{EnsureComfort, ConceptStandard, DomainShelter, []FactFamily{FactColony, FactRooms}},
	{MaintainHousing, ConceptStandard, DomainShelter, []FactFamily{FactColony, FactRooms}},
	{ManageSupplySafety, ConceptStandard, DomainUpkeep, []FactFamily{FactEmergency, FactColony}},
	{ClearPests, ConceptStandard, DomainMilitary, []FactFamily{FactColony, FactWorld}},
	{MaintainAnimalContainment, ConceptStandard, DomainPeople, []FactFamily{FactColony, FactPawns}},
	{MaintainAnimalFeed, ConceptStandard, DomainPeople, []FactFamily{FactColony, FactPawns}},
	{MaintainCleanFacilities, ConceptStandard, DomainUpkeep, []FactFamily{FactRooms, FactColony}},
	{MaintainEquipment, ConceptStandard, DomainMilitary, []FactFamily{FactPawns, FactColony}},
	{MaintainEssentialRepairs, ConceptStandard, DomainUpkeep, []FactFamily{FactColony}},
	{MaintainFireSafety, ConceptStandard, DomainUpkeep, []FactFamily{FactColony, FactEmergency}},
	{MaintainFirebreak, ConceptStandard, DomainUpkeep, []FactFamily{FactColony, FactWorld}},
	{MaintainMechs, ConceptStandard, DomainIndustry, []FactFamily{FactColony, FactPawns}},
	{MaintainPsylink, ConceptStandard, DomainPeople, []FactFamily{FactPawns}},
	{ManageCreepJoiners, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactEmergency}},
	{MaintainPermits, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactIdentity}},
	{MaintainIdeoRoles, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactIdentity}},
	{MaintainRituals, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactIdentity}},
	{MaintainFlooring, ConceptStandard, DomainShelter, []FactFamily{FactRooms, FactColony}},
	{MaintainFoodStorage, ConceptStandard, DomainFood, []FactFamily{FactColony, FactRooms}},
	{MaintainBabyFeeding, ConceptStandard, DomainFood, []FactFamily{FactPawns, FactColony}},
	{MaintainHerd, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactColony}},
	{MaintainHomeCoverage, ConceptStandard, DomainShelter, []FactFamily{FactRooms, FactColony}},
	{MaintainShelter, ConceptStandard, DomainShelter, []FactFamily{FactRooms, FactColony}},
	{MaintainLighting, ConceptStandard, DomainShelter, []FactFamily{FactRooms, FactColony}},
	{MaintainArt, ConceptStandard, DomainShelter, []FactFamily{FactRooms, FactColony}},
	{MaintainMedicalReserves, ConceptStandard, DomainMedical, []FactFamily{FactPawns, FactColony}},
	{MaintainSurgery, ConceptStandard, DomainMedical, []FactFamily{FactPawns, FactColony}},
	{MaintainPopulation, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactColony}},
	{MaintainRefrigeration, ConceptStandard, DomainFood, []FactFamily{FactRooms, FactColony}},
	{MaintainResource, ConceptStandard, DomainIndustry, []FactFamily{FactColony, FactDefinitions}},
	{MaintainRoutes, ConceptStandard, DomainUpkeep, []FactFamily{FactWorld, FactColony}},
	{MaintainStoneShell, ConceptStandard, DomainShelter, []FactFamily{FactRooms, FactColony, FactWorld}},
}

// goalDetectorIndex maps each registered GoalID to its detector.
var goalDetectorIndex = func() map[GoalID]GoalDetector {
	m := make(map[GoalID]GoalDetector, len(goalDetectors))
	for _, d := range goalDetectors {
		m[d.Goal] = d
	}
	return m
}()

// GoalDetectors lists every registered detector.
func GoalDetectors() []GoalDetector { return append([]GoalDetector(nil), goalDetectors...) }

// DetectorFor returns id's detector; an unknown id reports false.
func DetectorFor(id GoalID) (GoalDetector, bool) {
	d, ok := goalDetectorIndex[id]
	return d, ok
}

// ValidateGoalDetectors reports the first registry fault: a GoalID in ids
// without exactly one detector, a detector for no listed GoalID, or a
// detector with no concept, no domain, no inputs or an unknown input family.
func ValidateGoalDetectors(detectors []GoalDetector, ids []GoalID) error {
	count := map[GoalID]int{}
	known := map[FactFamily]bool{}
	for _, f := range FactFamilies() {
		known[f] = true
	}
	for _, d := range detectors {
		count[d.Goal]++
		switch {
		case d.Concept == ConceptUnknown || d.Concept == ConceptRule:
			return fmt.Errorf("goal detector %s: no concept", d.Goal)
		case d.Domain == DomainUnknown:
			return fmt.Errorf("goal detector %s: no domain", d.Goal)
		case len(d.Inputs) == 0:
			return fmt.Errorf("goal detector %s: declares no input fact families", d.Goal)
		}
		for _, in := range d.Inputs {
			if !known[in] {
				return fmt.Errorf("goal detector %s: unknown input family %q", d.Goal, in)
			}
		}
	}
	for _, id := range ids {
		if count[id] != 1 {
			return fmt.Errorf("GoalID %s has %d detectors, want 1", id, count[id])
		}
		delete(count, id)
	}
	for id := range count {
		return fmt.Errorf("goal detector %s matches no GoalID", id)
	}
	return nil
}
