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

// GoalDetector declares one GoalID: its concept, its colony area, the fact
// families its detection reads (#1907) and the detection itself (#1908).
// The registry is the only place a GoalID is classified.
type GoalDetector struct {
	Goal    GoalID
	Concept Concept
	Domain  Domain
	Inputs  []FactFamily
	// Detect appends the goal's assessment, and its goal when owed, to the
	// run. DetectRoutine runs the detectors in registry order, so the order
	// below is the order of RoutineNeeds.Assessments (the stored bindings
	// are index-aligned); a detector may read what the shared reviews and
	// every earlier detector left on the run, and RecoverDisasterServices
	// must stay last because it promotes everything filed before it.
	Detect func(*routineRun) error
}

// goalDetectors lists every detector in evaluation order. Inputs name the
// fact families the detector's body reads (traced in #1908); a goal that
// reads only configuration declares an explicit empty list.
var goalDetectors = []GoalDetector{
	{ConfirmColonyNames, ConceptResponse, DomainSystem, []FactFamily{FactIdentity}, detectColonyNames},
	{AnswerDialog, ConceptResponse, DomainSystem, []FactFamily{FactEmergency}, detectAnswerDialog},
	{ActiveCombat, ConceptResponse, DomainMilitary, []FactFamily{FactEmergency, FactColony}, detectActiveCombat},
	{CriticalMedicine, ConceptResponse, DomainMedical, []FactFamily{FactPawns, FactEmergency}, detectCriticalMedicine},
	{RestoreWorkers, ConceptResponse, DomainPeople, []FactFamily{FactEmergency, FactPawns}, detectRestoreWorkers},
	{AllowStartingSupplies, ConceptProject, DomainUpkeep, []FactFamily{FactColony}, detectAllowStartingSupplies},
	{ManageSupplySafety, ConceptStandard, DomainUpkeep, []FactFamily{FactEmergency, FactColony}, detectManageSupplySafety},
	{EnsureWorkAssignments, ConceptProject, DomainPeople, []FactFamily{FactPawns, FactIdentity, FactEmergency}, detectWorkAssignments},
	{EnsureFoodSupply, ConceptStandard, DomainFood, []FactFamily{FactColony, FactPawns}, detectFoodSupply},
	{MaintainHousing, ConceptStandard, DomainShelter, []FactFamily{FactColony, FactRooms, FactPawns}, detectHousing},
	{EnsureTemperatureSafety, ConceptStandard, DomainShelter, []FactFamily{FactColony, FactRooms, FactWorld}, detectTemperatureSafety},
	{EnsureCooking, ConceptProject, DomainFood, []FactFamily{FactColony}, detectCooking},
	{MaintainButcherSpot, ConceptProject, DomainFood, []FactFamily{FactColony, FactRooms}, detectButcherSpot},
	{EnsureBasicPower, ConceptProject, DomainIndustry, []FactFamily{FactColony, FactWorld}, detectBasicPower},
	{EnsureBasicDefense, ConceptStandard, DomainMilitary, []FactFamily{FactPawns, FactEmergency}, detectBasicDefense},
	{EnsureComfort, ConceptStandard, DomainShelter, []FactFamily{FactColony, FactRooms}, detectComfort},
	{ClearPests, ConceptStandard, DomainMilitary, []FactFamily{FactColony, FactWorld}, detectPests},
	{MaintainEquipment, ConceptStandard, DomainMilitary, []FactFamily{FactPawns, FactColony}, detectEquipment},
	{EnsureResearch, ConceptProject, DomainIndustry, []FactFamily{FactResearch, FactPawns, FactColony}, detectResearch},
	{MaintainResource, ConceptStandard, DomainIndustry, []FactFamily{FactColony, FactDefinitions, FactResearch, FactPawns}, detectResource},
	{TradeWithCaravan, ConceptResponse, DomainUpkeep, []FactFamily{FactWorld, FactColony, FactDefinitions, FactPawns}, detectTrade},
	{EnsureDefensiveLayout, ConceptProject, DomainMilitary, []FactFamily{}, detectDefensiveLayout},
	{MaintainFireSafety, ConceptStandard, DomainUpkeep, []FactFamily{FactColony, FactEmergency}, upkeepDetector(MaintainFireSafety, holdFireSafety)},
	{MaintainEssentialRepairs, ConceptStandard, DomainUpkeep, []FactFamily{FactColony}, upkeepDetector(MaintainEssentialRepairs, nil)},
	{MaintainCleanFacilities, ConceptStandard, DomainUpkeep, []FactFamily{FactRooms, FactColony}, upkeepDetector(MaintainCleanFacilities, holdCleaning)},
	{ClearHomeObstructions, ConceptStandard, DomainUpkeep, []FactFamily{FactColony}, upkeepDetector(ClearHomeObstructions, holdClearance)},
	{ClearAncientShrine, ConceptProject, DomainMilitary, []FactFamily{FactColony, FactWorld}, upkeepDetector(ClearAncientShrine, holdShrine)},
	{MaintainHomeCoverage, ConceptStandard, DomainShelter, []FactFamily{FactColony, FactWorld}, detectHomeCoverage},
	{MaintainStoneShell, ConceptStandard, DomainShelter, []FactFamily{FactColony, FactResearch}, detectStoneShell},
	{MaintainMedicalReserves, ConceptStandard, DomainMedical, []FactFamily{FactPawns, FactColony}, detectMedicalReserves},
	{MaintainSurgery, ConceptStandard, DomainMedical, []FactFamily{FactPawns, FactColony, FactDefinitions}, detectSurgery},
	{MaintainBabyFeeding, ConceptStandard, DomainFood, []FactFamily{FactPawns, FactColony}, detectBabyFeeding},
	{MaintainFoodStorage, ConceptStandard, DomainFood, []FactFamily{FactColony}, detectFoodStorage},
	{MaintainRefrigeration, ConceptStandard, DomainFood, []FactFamily{FactRooms, FactColony}, detectRefrigeration},
	{MaintainLighting, ConceptStandard, DomainShelter, []FactFamily{FactRooms, FactColony, FactWorld}, detectLighting},
	{MaintainFlooring, ConceptStandard, DomainShelter, []FactFamily{FactRooms, FactColony}, detectFlooring},
	{MaintainRoutes, ConceptStandard, DomainUpkeep, []FactFamily{FactWorld, FactColony}, detectRoutes},
	{MaintainArt, ConceptStandard, DomainShelter, []FactFamily{FactRooms, FactColony, FactPawns}, detectArt},
	{MaintainShelter, ConceptStandard, DomainShelter, []FactFamily{FactRooms, FactWorld, FactEmergency}, detectShelter},
	{MaintainFirebreak, ConceptStandard, DomainUpkeep, []FactFamily{FactColony, FactWorld}, detectFirebreak},
	{MaintainMechs, ConceptStandard, DomainIndustry, []FactFamily{FactColony, FactPawns}, detectMechs},
	{MaintainPsylink, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactColony}, detectPsylink},
	{MaintainIdeoRoles, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactColony}, detectIdeoRoles},
	{MaintainRituals, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactColony}, detectRituals},
	{MaintainPermits, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactDefinitions, FactColony}, detectPermits},
	{ManageCreepJoiners, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactEmergency}, detectCreepJoiners},
	{MaintainHerd, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactColony, FactRooms}, detectHerd},
	{MaintainPopulation, ConceptStandard, DomainPeople, []FactFamily{FactPawns, FactColony, FactWorld}, detectPopulation},
	{MaintainAnimalContainment, ConceptStandard, DomainPeople, []FactFamily{FactColony, FactPawns, FactRooms}, detectAnimalContainment},
	{MaintainAnimalFeed, ConceptStandard, DomainPeople, []FactFamily{FactColony, FactPawns, FactWorld}, detectAnimalFeed},
	{MaintainWaste, ConceptStandard, DomainUpkeep, []FactFamily{FactColony}, detectWaste},
	{RemoveBlight, ConceptStandard, DomainFood, []FactFamily{FactColony}, detectBlight},
	{ManagePollution, ConceptStandard, DomainUpkeep, []FactFamily{FactColony, FactWorld}, detectPollution},
	{EnsureMechCharger, ConceptStandard, DomainUpkeep, []FactFamily{FactColony, FactPawns}, detectMechCharger},
	{MaintainGeneBank, ConceptStandard, DomainUpkeep, []FactFamily{FactColony}, detectGeneBank},
	{TidyLayout, ConceptStandard, DomainUpkeep, []FactFamily{FactRooms, FactColony}, detectTidyLayout},
	{MaintainStockpiles, ConceptStandard, DomainUpkeep, []FactFamily{FactColony}, detectStockpiles},
	{EnsureMood, ConceptResponse, DomainPeople, []FactFamily{FactPawns}, detectMood},
	{RecoverDisasterServices, ConceptResponse, DomainUpkeep, []FactFamily{FactEmergency, FactColony, FactWorld}, detectDisaster},
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
// detector with no concept, no domain, no detect function, a nil input list
// (a config-only goal declares an explicit empty one) or an unknown input
// family.
func ValidateGoalDetectors(detectors []GoalDetector, ids []GoalID) error {
	count := map[GoalID]int{}
	known := map[FactFamily]bool{}
	for _, f := range FactFamilies() {
		known[f] = true
	}
	for _, d := range detectors {
		count[d.Goal]++
		switch {
		case d.Concept == ConceptUnknown || d.Concept == ConceptSafeguard:
			return fmt.Errorf("goal detector %s: no concept", d.Goal)
		case d.Domain == DomainUnknown:
			return fmt.Errorf("goal detector %s: no domain", d.Goal)
		case d.Detect == nil:
			return fmt.Errorf("goal detector %s: no detect function", d.Goal)
		case d.Inputs == nil:
			return fmt.Errorf("goal detector %s: declares no input fact families (use an explicit empty list for a config-only goal)", d.Goal)
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
