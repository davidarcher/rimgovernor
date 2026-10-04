package policy

import "fmt"

// FactFamily names the observation facts native reports changed together
// (ObservationInvalidated): the unit an inspection declares as an input.
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

// Inspection checks one Concern: its type, its department, the fact
// families its check reads (#1907) and the check itself (#1908).
// The registry is the only place a Concern is classified.
type Inspection struct {
	Concern    ConcernID
	Type       ConcernType
	Department Department
	Inputs     []FactFamily
	// Inspect appends the Concern's assessment, and its row when owed, to the
	// run. DetectRoutine runs the inspections in registry order, so the order
	// below is the order of RoutineNeeds.Assessments (the stored bindings
	// are index-aligned); an inspection may read what the shared reviews and
	// every earlier inspection left on the run, and RecoverDisasterServices
	// must stay last because it promotes everything filed before it.
	Inspect func(*routineRun) error
}

// inspections lists every inspection in evaluation order. Inputs name the
// fact families the inspection's body reads (traced in #1908); a Concern that
// reads only configuration declares an explicit empty list.
var inspections = []Inspection{
	{ConfirmColonyNames, IncidentConcern, DepartmentSystem, []FactFamily{FactIdentity}, detectColonyNames},
	{AnswerDialog, IncidentConcern, DepartmentSystem, []FactFamily{FactEmergency}, detectAnswerDialog},
	{ActiveCombat, IncidentConcern, DepartmentMilitary, []FactFamily{FactEmergency, FactColony}, detectActiveCombat},
	{CriticalMedicine, IncidentConcern, DepartmentMedical, []FactFamily{FactPawns, FactEmergency}, detectCriticalMedicine},
	{RestoreWorkers, IncidentConcern, DepartmentPeople, []FactFamily{FactEmergency, FactPawns}, detectRestoreWorkers},
	{AllowStartingSupplies, ProjectConcern, DepartmentUpkeep, []FactFamily{FactColony}, detectAllowStartingSupplies},
	{ManageSupplySafety, StandardConcern, DepartmentUpkeep, []FactFamily{FactEmergency, FactColony}, detectManageSupplySafety},
	{EnsureWorkAssignments, ProjectConcern, DepartmentPeople, []FactFamily{FactPawns, FactIdentity, FactEmergency}, detectWorkAssignments},
	{EnsureFoodSupply, StandardConcern, DepartmentFood, []FactFamily{FactColony, FactPawns}, detectFoodSupply},
	{MaintainHousing, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactRooms, FactPawns}, detectHousing},
	{EnsureTemperatureSafety, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactRooms, FactWorld}, detectTemperatureSafety},
	{EnsureCooking, ProjectConcern, DepartmentFood, []FactFamily{FactColony}, detectCooking},
	{MaintainButcherSpot, ProjectConcern, DepartmentFood, []FactFamily{FactColony, FactRooms}, detectButcherSpot},
	{EnsureBasicPower, ProjectConcern, DepartmentIndustry, []FactFamily{FactColony, FactWorld}, detectBasicPower},
	{EnsureBasicDefense, StandardConcern, DepartmentMilitary, []FactFamily{FactPawns, FactEmergency}, detectBasicDefense},
	{EnsureComfort, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactRooms}, detectComfort},
	{ClearPests, StandardConcern, DepartmentMilitary, []FactFamily{FactColony, FactWorld}, detectPests},
	{MaintainEquipment, StandardConcern, DepartmentMilitary, []FactFamily{FactPawns, FactColony}, detectEquipment},
	{EnsureResearch, ProjectConcern, DepartmentIndustry, []FactFamily{FactResearch, FactPawns, FactColony}, detectResearch},
	{MaintainResource, StandardConcern, DepartmentIndustry, []FactFamily{FactColony, FactDefinitions, FactResearch, FactPawns}, detectResource},
	{TradeWithCaravan, IncidentConcern, DepartmentUpkeep, []FactFamily{FactWorld, FactColony, FactDefinitions, FactPawns}, detectTrade},
	{EnsureDefensiveLayout, ProjectConcern, DepartmentMilitary, []FactFamily{}, detectDefensiveLayout},
	{MaintainFireSafety, StandardConcern, DepartmentUpkeep, []FactFamily{FactColony, FactEmergency}, upkeepDetector(MaintainFireSafety, holdFireSafety)},
	{MaintainEssentialRepairs, StandardConcern, DepartmentUpkeep, []FactFamily{FactColony}, upkeepDetector(MaintainEssentialRepairs, nil)},
	{MaintainCleanFacilities, StandardConcern, DepartmentUpkeep, []FactFamily{FactRooms, FactColony}, upkeepDetector(MaintainCleanFacilities, holdCleaning)},
	{ClearHomeObstructions, StandardConcern, DepartmentUpkeep, []FactFamily{FactColony}, upkeepDetector(ClearHomeObstructions, holdClearance)},
	{ClearAncientShrine, ProjectConcern, DepartmentMilitary, []FactFamily{FactColony, FactWorld}, upkeepDetector(ClearAncientShrine, holdShrine)},
	{MaintainHomeCoverage, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactWorld}, detectHomeCoverage},
	{MaintainStoneShell, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactResearch}, detectStoneShell},
	{MaintainMedicalReserves, StandardConcern, DepartmentMedical, []FactFamily{FactPawns, FactColony}, detectMedicalReserves},
	{MaintainSurgery, StandardConcern, DepartmentMedical, []FactFamily{FactPawns, FactColony, FactDefinitions}, detectSurgery},
	{MaintainBabyFeeding, StandardConcern, DepartmentFood, []FactFamily{FactPawns, FactColony}, detectBabyFeeding},
	{MaintainFoodStorage, StandardConcern, DepartmentFood, []FactFamily{FactColony}, detectFoodStorage},
	{MaintainRefrigeration, StandardConcern, DepartmentFood, []FactFamily{FactRooms, FactColony}, detectRefrigeration},
	{MaintainLighting, StandardConcern, DepartmentShelter, []FactFamily{FactRooms, FactColony, FactWorld}, detectLighting},
	{MaintainFlooring, StandardConcern, DepartmentShelter, []FactFamily{FactRooms, FactColony}, detectFlooring},
	{MaintainRoutes, StandardConcern, DepartmentUpkeep, []FactFamily{FactWorld, FactColony}, detectRoutes},
	{MaintainArt, StandardConcern, DepartmentShelter, []FactFamily{FactRooms, FactColony, FactPawns}, detectArt},
	{MaintainShelter, StandardConcern, DepartmentShelter, []FactFamily{FactRooms, FactWorld, FactEmergency}, detectShelter},
	{MaintainFirebreak, StandardConcern, DepartmentUpkeep, []FactFamily{FactColony, FactWorld}, detectFirebreak},
	{MaintainMechs, StandardConcern, DepartmentIndustry, []FactFamily{FactColony, FactPawns}, detectMechs},
	{MaintainPsylink, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactColony}, detectPsylink},
	{MaintainIdeoRoles, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactColony}, detectIdeoRoles},
	{MaintainRituals, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactColony}, detectRituals},
	{MaintainPermits, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactDefinitions, FactColony}, detectPermits},
	{ManageCreepJoiners, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactEmergency}, detectCreepJoiners},
	{MaintainHerd, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactColony, FactRooms}, detectHerd},
	{MaintainPopulation, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactColony, FactWorld}, detectPopulation},
	{MaintainAnimalContainment, StandardConcern, DepartmentPeople, []FactFamily{FactColony, FactPawns, FactRooms}, detectAnimalContainment},
	{MaintainAnimalFeed, StandardConcern, DepartmentPeople, []FactFamily{FactColony, FactPawns, FactWorld}, detectAnimalFeed},
	{MaintainWaste, StandardConcern, DepartmentUpkeep, []FactFamily{FactColony}, detectWaste},
	{RemoveBlight, StandardConcern, DepartmentFood, []FactFamily{FactColony}, detectBlight},
	{ManagePollution, StandardConcern, DepartmentUpkeep, []FactFamily{FactColony, FactWorld}, detectPollution},
	{EnsureMechCharger, StandardConcern, DepartmentUpkeep, []FactFamily{FactColony, FactPawns}, detectMechCharger},
	{MaintainGeneBank, StandardConcern, DepartmentUpkeep, []FactFamily{FactColony}, detectGeneBank},
	{TidyLayout, StandardConcern, DepartmentUpkeep, []FactFamily{FactRooms, FactColony}, detectTidyLayout},
	{MaintainStockpiles, StandardConcern, DepartmentUpkeep, []FactFamily{FactColony}, detectStockpiles},
	{EnsureMood, IncidentConcern, DepartmentPeople, []FactFamily{FactPawns}, detectMood},
	{RecoverDisasterServices, IncidentConcern, DepartmentUpkeep, []FactFamily{FactEmergency, FactColony, FactWorld}, detectDisaster},
}

// inspectionIndex maps each registered ConcernID to its inspection.
var inspectionIndex = func() map[ConcernID]Inspection {
	m := make(map[ConcernID]Inspection, len(inspections))
	for _, d := range inspections {
		m[d.Concern] = d
	}
	return m
}()

// AllInspections lists every registered inspection.
func AllInspections() []Inspection { return append([]Inspection(nil), inspections...) }

// InspectionFor returns id's inspection; an unknown id reports false.
func InspectionFor(id ConcernID) (Inspection, bool) {
	d, ok := inspectionIndex[id]
	return d, ok
}

// ValidateInspections reports the first registry fault: a ConcernID in ids
// without exactly one inspection, an inspection for no listed ConcernID, or an
// inspection with no type, no department, no inspect function, a nil input list
// (a config-only Concern declares an explicit empty one) or an unknown input
// family.
func ValidateInspections(inspections []Inspection, ids []ConcernID) error {
	count := map[ConcernID]int{}
	known := map[FactFamily]bool{}
	for _, f := range FactFamilies() {
		known[f] = true
	}
	for _, d := range inspections {
		count[d.Concern]++
		switch {
		case d.Type == UnknownConcern:
			return fmt.Errorf("inspection %s: no type", d.Concern)
		case d.Department == DepartmentUnknown:
			return fmt.Errorf("inspection %s: no department", d.Concern)
		case d.Inspect == nil:
			return fmt.Errorf("inspection %s: no inspect function", d.Concern)
		case d.Inputs == nil:
			return fmt.Errorf("inspection %s: declares no input fact families (use an explicit empty list for a config-only Concern)", d.Concern)
		}
		for _, in := range d.Inputs {
			if !known[in] {
				return fmt.Errorf("inspection %s: unknown input family %q", d.Concern, in)
			}
		}
	}
	for _, id := range ids {
		if count[id] != 1 {
			return fmt.Errorf("ConcernID %s has %d inspections, want 1", id, count[id])
		}
		delete(count, id)
	}
	for id := range count {
		return fmt.Errorf("inspection %s matches no Concern", id)
	}
	return nil
}
