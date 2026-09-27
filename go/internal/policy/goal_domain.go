package policy

// Domain is the colony area a GoalID belongs to, like a Civ advisor (#1027).
// It is a tag for grouping goals in panels only; it never ranks goals or
// budgets labor. The table matches the domain table in
// docs/developers/architecture/control-loop.md.
type Domain string

const (
	DomainUnknown    Domain = ""
	DomainFood       Domain = "Food"
	DomainShelter    Domain = "Shelter"
	DomainProduction Domain = "Production"
	DomainResearch   Domain = "Research"
	DomainMilitary   Domain = "Military"
	DomainMedical    Domain = "Medical"
	DomainLabor      Domain = "Labor"
	DomainSupply     Domain = "Supply"
	DomainUpkeep     Domain = "Upkeep"
)

var goalDomains = map[GoalID]Domain{
	EnsureFoodSupply:          DomainFood,
	EnsureCooking:             DomainFood,
	MaintainFoodStorage:       DomainFood,
	MaintainRefrigeration:     DomainFood,
	MaintainHerd:              DomainFood,
	MaintainAnimalFeed:        DomainFood,
	MaintainAnimalContainment: DomainFood,
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

	EnsureBasicPower: DomainProduction,
	MaintainResource: DomainProduction,

	EnsureResearch: DomainResearch,

	ActiveCombat:          DomainMilitary,
	EnsureBasicDefense:    DomainMilitary,
	EnsureDefensiveLayout: DomainMilitary,
	ClearAncientShrine:    DomainMilitary,
	ClearPests:            DomainMilitary,
	MaintainEquipment:     DomainMilitary,

	CriticalMedicine:        DomainMedical,
	MaintainMedicalCare:     DomainMedical,
	MaintainMedicalReserves: DomainMedical,

	RestoreWorkers:        DomainLabor,
	EnsureWorkAssignments: DomainLabor,
	MaintainPopulation:    DomainLabor,

	AllowStartingSupplies: DomainSupply,
	SecureSupplies:        DomainSupply,
	ManageSupplySafety:    DomainSupply,
	MaintainStockpiles:    DomainSupply,
	MaintainStorage:       DomainSupply,
	TradeWithCaravan:      DomainSupply,

	MaintainWaste:            DomainUpkeep,
	TidyLayout:               DomainUpkeep,
	ClearHomeObstructions:    DomainUpkeep,
	MaintainCleanFacilities:  DomainUpkeep,
	MaintainEssentialRepairs: DomainUpkeep,
	MaintainFireSafety:       DomainUpkeep,
	MaintainRoutes:           DomainUpkeep,
	RecoverDisasterServices:  DomainUpkeep,
	AnswerDialog:             DomainUpkeep,
	ConfirmColonyNames:       DomainUpkeep,
}

// GoalDomain tags id with its colony area. Mood goals (one per pawn) are
// Labor; an unknown id is DomainUnknown.
func GoalDomain(id GoalID) Domain {
	if IsMoodGoal(id) {
		return DomainLabor
	}
	return goalDomains[id]
}
