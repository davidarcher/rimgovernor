package policy

// Concept is the kind of thing a GoalID actually is (#1012). The table
// matches the concept table in docs/developers/architecture/control-loop.md.
type Concept string

const (
	ConceptUnknown  Concept = ""
	ConceptStandard Concept = "Standard"
	ConceptProject  Concept = "Project"
	ConceptResponse Concept = "Response"
	ConceptRule     Concept = "Rule"
)

var goalConcepts = map[GoalID]Concept{
	ActiveCombat:            ConceptResponse,
	CriticalMedicine:        ConceptResponse,
	RestoreWorkers:          ConceptResponse,
	AnswerDialog:            ConceptResponse,
	ConfirmColonyNames:      ConceptResponse,
	RecoverDisasterServices: ConceptResponse,
	TradeWithCaravan:        ConceptResponse,

	AllowStartingSupplies: ConceptProject,
	EnsureCooking:         ConceptProject,
	EnsureBasicPower:      ConceptProject,
	EnsureWorkAssignments: ConceptProject,
	EnsureResearch:        ConceptProject,
	EnsureDefensiveLayout: ConceptProject,
	ClearAncientShrine:    ConceptProject,

	// Chores: Standards whose target is no outstanding work.
	MaintainWaste:         ConceptStandard,
	RemoveBlight:          ConceptStandard,
	MaintainStockpiles:    ConceptStandard,
	TidyLayout:            ConceptStandard,
	ClearHomeObstructions: ConceptStandard,

	EnsureFoodSupply:          ConceptStandard,
	EnsureBasicDefense:        ConceptStandard,
	EnsureTemperatureSafety:   ConceptStandard,
	EnsureComfort:             ConceptStandard,
	MaintainHousing:           ConceptStandard,
	ManageSupplySafety:        ConceptStandard,
	SecureSupplies:            ConceptStandard,
	ClearPests:                ConceptStandard,
	MaintainAnimalContainment: ConceptStandard,
	MaintainAnimalFeed:        ConceptStandard,
	MaintainCleanFacilities:   ConceptStandard,
	MaintainEquipment:         ConceptStandard,
	MaintainEssentialRepairs:  ConceptStandard,
	MaintainFireSafety:        ConceptStandard,
	MaintainFlooring:          ConceptStandard,
	MaintainFoodStorage:       ConceptStandard,
	MaintainHerd:              ConceptStandard,
	MaintainHomeCoverage:      ConceptStandard,
	MaintainLighting:          ConceptStandard,
	MaintainMedicalReserves:   ConceptStandard,
	MaintainPopulation:        ConceptStandard,
	MaintainRefrigeration:     ConceptStandard,
	MaintainResource:          ConceptStandard,
	MaintainRoutes:            ConceptStandard,
	MaintainStoneShell:        ConceptStandard,
	MaintainStorage:           ConceptStandard,
}

// GoalConcept classifies id. Mood goals (one per pawn) are Responses. Rules
// carry no GoalID, so no id maps to ConceptRule; an unknown id is
// ConceptUnknown.
func GoalConcept(id GoalID) Concept {
	if IsMoodGoal(id) {
		return ConceptResponse
	}
	return goalConcepts[id]
}
