package policy

// Department is the colony area a Concern belongs to, like a Civ advisor.
// It is a tag for grouping goals in panels only; it never ranks goals or
// budgets labor. The table matches the department table in
// docs/developers/architecture/control-loop.md.
type Department string

const (
	DepartmentUnknown    Department = ""
	DepartmentFood       Department = "Food"
	DepartmentShelter    Department = "Shelter"
	DepartmentIndustry   Department = "Industry"
	DepartmentMilitary   Department = "Military"
	DepartmentMedical    Department = "Medical"
	DepartmentPeople     Department = "People"
	DepartmentStorage    Department = "Storage"
	DepartmentSanitation Department = "Sanitation"
	// DepartmentSystem holds game-plumbing goals; panels do not show it.
	DepartmentSystem Department = "System"
)

// DepartmentOf tags id with its colony area; an unknown id is DepartmentUnknown.
func DepartmentOf(id ConcernID) Department { return inspectionIndex[id].Department }

// stockpileZoneLimits is the number of stockpile-zone creations one method of
// each Standard Concern that owns stores may carry.
// ClearHomeObstructions keeps its chunk dump until that zone goes, and
// MaintainStockpiles applies every declaration of the storage planner in one
// batch. A concern absent from the table owns no store.
var stockpileZoneLimits = map[ConcernID]int{
	MaintainStockpiles:    16,
	ClearHomeObstructions: 1,
	// Food
	EnsureFoodSupply:      1,
	EnsureCooking:         1,
	MaintainBabyFeeding:   1,
	MaintainFoodStorage:   1,
	MaintainRefrigeration: 1,
	RemoveBlight:          1,
	// Industry
	EnsureBasicPower:   1,
	EnsureResearch:     1,
	MaintainResource:   1,
	MaintainTrade:      1,
	MaintainWorkLedger: 1,
	MaintainMechs:      1,
	EnsureMechCharger:  1,
	// Military
	EnsureBasicDefense:    1,
	ClearPests:            1,
	MaintainEquipment:     1,
	MaintainTraining:      1,
	EnsureDefensiveLayout: 1,
	ClearAncientShrine:    1,
	// Medical
	MaintainMedicalReserves: 1,
	MaintainSurgery:         1,
	MaintainGeneBank:        1,
	// People
	EnsureWorkAssignments:     1,
	MaintainPsylink:           1,
	MaintainIdeoRoles:         1,
	ImproveIdeoligion:         1,
	MaintainRituals:           1,
	HoldGatherings:            1,
	MaintainPermits:           1,
	ManageCreepJoiners:        1,
	MaintainHerd:              1,
	MaintainPopulation:        1,
	MaintainAnimalContainment: 1,
	MaintainBurial:            1,
}

// StockpileZoneLimit is the number of stockpile-zone creations one method of
// concern may carry; zero means the concern owns no store.
func StockpileZoneLimit(concern ConcernID) int { return stockpileZoneLimits[concern] }
