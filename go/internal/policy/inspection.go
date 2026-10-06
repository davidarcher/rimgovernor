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
	// run. DetectRounds runs the inspections in registry order, so the order
	// below is the order of RoundsFindings.Assessments (the stored bindings
	// are index-aligned); an inspection may read what the shared reviews and
	// every earlier inspection left on the run, and RecoverDisasterServices
	// must stay last because it promotes everything filed before it.
	Inspect func(*roundsRun) error
}

// inspections lists every inspection in evaluation order. Inputs name the
// fact families the inspection's body reads (traced in #1908); a Concern that
// reads only configuration declares an explicit empty list.
var inspections = []Inspection{
	{ConfirmColonyNames, IncidentConcern, DepartmentSystem, []FactFamily{FactIdentity}, inspectColonyNames},
	{AnswerDialog, IncidentConcern, DepartmentSystem, []FactFamily{FactEmergency}, inspectAnswerDialog},
	{ActiveCombat, IncidentConcern, DepartmentMilitary, []FactFamily{FactEmergency, FactColony}, inspectActiveCombat},
	{CriticalMedicine, IncidentConcern, DepartmentMedical, []FactFamily{FactPawns, FactEmergency}, inspectCriticalMedicine},
	{RestoreWorkers, IncidentConcern, DepartmentPeople, []FactFamily{FactEmergency, FactPawns}, inspectRestoreWorkers},
	{AllowStartingSupplies, ProjectConcern, DepartmentStorage, []FactFamily{FactColony}, inspectAllowStartingSupplies},
	{ManageSupplySafety, StandardConcern, DepartmentStorage, []FactFamily{FactEmergency, FactColony}, inspectManageSupplySafety},
	{EnsureWorkAssignments, ProjectConcern, DepartmentPeople, []FactFamily{FactPawns, FactIdentity, FactEmergency}, inspectWorkAssignments},
	{EnsureFoodSupply, StandardConcern, DepartmentFood, []FactFamily{FactColony, FactPawns}, inspectFoodSupply},
	{MaintainHousing, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactRooms, FactPawns}, inspectHousing},
	{EnsureTemperatureSafety, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactRooms, FactWorld}, inspectTemperatureSafety},
	{EnsureCooking, ProjectConcern, DepartmentFood, []FactFamily{FactColony}, inspectCooking},
	{MaintainButcherSpot, ProjectConcern, DepartmentFood, []FactFamily{FactColony, FactRooms}, inspectButcherSpot},
	{EnsureBasicPower, ProjectConcern, DepartmentIndustry, []FactFamily{FactColony, FactWorld}, inspectBasicPower},
	{EnsureBasicDefense, StandardConcern, DepartmentMilitary, []FactFamily{FactPawns, FactEmergency}, inspectBasicDefense},
	{EnsureComfort, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactRooms}, inspectComfort},
	{ClearPests, StandardConcern, DepartmentMilitary, []FactFamily{FactColony, FactWorld}, inspectPests},
	{MaintainEquipment, StandardConcern, DepartmentMilitary, []FactFamily{FactPawns, FactColony}, inspectEquipment},
	{EnsureResearch, ProjectConcern, DepartmentIndustry, []FactFamily{FactResearch, FactPawns, FactColony}, inspectResearch},
	{MaintainResource, StandardConcern, DepartmentIndustry, []FactFamily{FactColony, FactDefinitions, FactResearch, FactPawns}, inspectResource},
	{TradeWithCaravan, IncidentConcern, DepartmentIndustry, []FactFamily{FactWorld, FactColony, FactDefinitions, FactPawns}, inspectTrade},
	{EnsureDefensiveLayout, ProjectConcern, DepartmentMilitary, []FactFamily{}, inspectDefensiveLayout},
	{MaintainFireSafety, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactEmergency}, upkeepInspection(MaintainFireSafety, holdFireSafety)},
	{MaintainEssentialRepairs, StandardConcern, DepartmentShelter, []FactFamily{FactColony}, upkeepInspection(MaintainEssentialRepairs, nil)},
	{MaintainCleanFacilities, StandardConcern, DepartmentSanitation, []FactFamily{FactRooms, FactColony}, upkeepInspection(MaintainCleanFacilities, holdCleaning)},
	{ClearHomeObstructions, StandardConcern, DepartmentShelter, []FactFamily{FactColony}, upkeepInspection(ClearHomeObstructions, holdClearance)},
	{ClearAncientShrine, ProjectConcern, DepartmentMilitary, []FactFamily{FactColony, FactWorld}, upkeepInspection(ClearAncientShrine, holdShrine)},
	{MaintainHomeCoverage, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactWorld}, inspectHomeCoverage},
	{MaintainStoneShell, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactResearch}, inspectStoneShell},
	{MaintainMedicalReserves, StandardConcern, DepartmentMedical, []FactFamily{FactPawns, FactColony}, inspectMedicalReserves},
	{MaintainSurgery, StandardConcern, DepartmentMedical, []FactFamily{FactPawns, FactColony, FactDefinitions}, inspectSurgery},
	{MaintainBabyFeeding, StandardConcern, DepartmentFood, []FactFamily{FactPawns, FactColony}, inspectBabyFeeding},
	{MaintainFoodStorage, StandardConcern, DepartmentFood, []FactFamily{FactColony}, inspectFoodStorage},
	{MaintainRefrigeration, StandardConcern, DepartmentFood, []FactFamily{FactRooms, FactColony}, inspectRefrigeration},
	{MaintainLighting, StandardConcern, DepartmentShelter, []FactFamily{FactRooms, FactColony, FactWorld}, inspectLighting},
	{MaintainFlooring, StandardConcern, DepartmentShelter, []FactFamily{FactRooms, FactColony}, inspectFlooring},
	{MaintainRoutes, StandardConcern, DepartmentShelter, []FactFamily{FactWorld, FactColony}, inspectRoutes},
	{MaintainArt, StandardConcern, DepartmentShelter, []FactFamily{FactRooms, FactColony, FactPawns}, inspectArt},
	{MaintainShelter, StandardConcern, DepartmentShelter, []FactFamily{FactRooms, FactWorld, FactEmergency}, inspectShelter},
	{MaintainFirebreak, StandardConcern, DepartmentShelter, []FactFamily{FactColony, FactWorld}, inspectFirebreak},
	{MaintainMechs, StandardConcern, DepartmentIndustry, []FactFamily{FactColony, FactPawns}, inspectMechs},
	{MaintainPsylink, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactColony}, inspectPsylink},
	{MaintainIdeoRoles, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactColony}, inspectIdeoRoles},
	{MaintainRituals, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactColony}, inspectRituals},
	{MaintainPermits, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactDefinitions, FactColony}, inspectPermits},
	{ManageCreepJoiners, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactEmergency}, inspectCreepJoiners},
	{MaintainHerd, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactColony, FactRooms}, inspectHerd},
	{MaintainPopulation, StandardConcern, DepartmentPeople, []FactFamily{FactPawns, FactColony, FactWorld}, inspectPopulation},
	{MaintainAnimalContainment, StandardConcern, DepartmentPeople, []FactFamily{FactColony, FactPawns, FactRooms}, inspectAnimalContainment},
	{MaintainAnimalFeed, StandardConcern, DepartmentPeople, []FactFamily{FactColony, FactPawns, FactWorld}, inspectAnimalFeed},
	{MaintainWaste, StandardConcern, DepartmentSanitation, []FactFamily{FactColony}, inspectWaste},
	{RemoveBlight, StandardConcern, DepartmentFood, []FactFamily{FactColony}, inspectBlight},
	{ManagePollution, StandardConcern, DepartmentSanitation, []FactFamily{FactColony, FactWorld}, inspectPollution},
	{EnsureMechCharger, StandardConcern, DepartmentIndustry, []FactFamily{FactColony, FactPawns}, inspectMechCharger},
	{MaintainGeneBank, StandardConcern, DepartmentMedical, []FactFamily{FactColony}, inspectGeneBank},
	{MaintainStockpiles, StandardConcern, DepartmentStorage, []FactFamily{FactColony}, inspectStockpiles},
	{EnsureMood, IncidentConcern, DepartmentPeople, []FactFamily{FactPawns}, inspectMood},
	{RecoverDisasterServices, IncidentConcern, DepartmentShelter, []FactFamily{FactEmergency, FactColony, FactWorld}, inspectDisaster},
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
