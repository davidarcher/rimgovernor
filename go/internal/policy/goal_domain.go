package policy

// Domain is the colony area a GoalID belongs to, like a Civ advisor (#1027).
// It is a tag for grouping goals in panels only; it never ranks goals or
// budgets labor. The table matches the domain table in
// docs/developers/architecture/control-loop.md.
type Domain string

const (
	DomainUnknown  Domain = ""
	DomainFood     Domain = "Food"
	DomainShelter  Domain = "Shelter"
	DomainIndustry Domain = "Industry"
	DomainMilitary Domain = "Military"
	DomainMedical  Domain = "Medical"
	DomainPeople   Domain = "People"
	DomainUpkeep   Domain = "Upkeep"
	// DomainSystem holds game-plumbing goals; panels do not show it.
	DomainSystem Domain = "System"
)

var goalDomains = map[GoalID]Domain{
	EnsureFoodSupply:          DomainFood,
	EnsureCooking:             DomainFood,
	MaintainFoodStorage:       DomainFood,
	MaintainRefrigeration:     DomainFood,
	MaintainHerd:              DomainPeople,
	MaintainAnimalFeed:        DomainPeople,
	MaintainAnimalContainment: DomainPeople,
	RemoveBlight:              DomainFood,

	EnsureInitialShelter:    DomainShelter,
	EnsureBasicComfort:      DomainShelter,
	EnsureComfort:           DomainShelter,
	EnsureTemperatureSafety: DomainShelter,
	EnsureExpansion:         DomainShelter,
	MaintainSleeping:        DomainShelter,
	MaintainStoneShell:      DomainShelter,
	MaintainLighting:        DomainShelter,
	MaintainFlooring:        DomainShelter,
	MaintainHomeCoverage:    DomainShelter,

	EnsureBasicPower: DomainIndustry,
	MaintainResource: DomainIndustry,
	EnsureResearch:   DomainIndustry,

	ActiveCombat:          DomainMilitary,
	EnsureBasicDefense:    DomainMilitary,
	EnsureDefensiveLayout: DomainMilitary,
	ClearAncientShrine:    DomainMilitary,
	ClearPests:            DomainMilitary,
	MaintainEquipment:     DomainMilitary,

	CriticalMedicine:        DomainMedical,
	MaintainMedicalCare:     DomainMedical,
	MaintainMedicalReserves: DomainMedical,

	RestoreWorkers:        DomainPeople,
	EnsureWorkAssignments: DomainPeople,
	MaintainPopulation:    DomainPeople,

	AllowStartingSupplies:    DomainUpkeep,
	SecureSupplies:           DomainUpkeep,
	ManageSupplySafety:       DomainUpkeep,
	MaintainStockpiles:       DomainUpkeep,
	MaintainStorage:          DomainUpkeep,
	TradeWithCaravan:         DomainUpkeep,
	MaintainWaste:            DomainUpkeep,
	TidyLayout:               DomainUpkeep,
	ClearHomeObstructions:    DomainUpkeep,
	MaintainCleanFacilities:  DomainUpkeep,
	MaintainEssentialRepairs: DomainUpkeep,
	MaintainFireSafety:       DomainUpkeep,
	MaintainRoutes:           DomainUpkeep,
	RecoverDisasterServices:  DomainUpkeep,

	AnswerDialog:       DomainSystem,
	ConfirmColonyNames: DomainSystem,
}

// GoalDomain tags id with its colony area. Mood goals (one per pawn) are
// People; an unknown id is DomainUnknown.
func GoalDomain(id GoalID) Domain {
	if IsMoodGoal(id) {
		return DomainPeople
	}
	return goalDomains[id]
}
