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
	EnsureMood:              ConceptResponse,

	AllowStartingSupplies: ConceptProject,
	EnsureCooking:         ConceptProject,
	MaintainButcherSpot:   ConceptProject,
	EnsureBasicPower:      ConceptProject,
	EnsureWorkAssignments: ConceptProject,
	EnsureResearch:        ConceptProject,
	EnsureDefensiveLayout: ConceptProject,
	ClearAncientShrine:    ConceptProject,

	// Chores: Standards whose target is no outstanding work.
	MaintainWaste:         ConceptStandard,
	RemoveBlight:          ConceptStandard,
	ManagePollution:       ConceptStandard,
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
	MaintainFirebreak:         ConceptStandard,
	MaintainMechs:             ConceptStandard,
	MaintainPsylink:           ConceptStandard,
	MaintainPermits:           ConceptStandard,
	MaintainIdeoRoles:         ConceptStandard,
	MaintainFlooring:          ConceptStandard,
	MaintainFoodStorage:       ConceptStandard,
	MaintainBabyFeeding:       ConceptStandard,
	MaintainHerd:              ConceptStandard,
	MaintainHomeCoverage:      ConceptStandard,
	MaintainShelter:           ConceptStandard,
	MaintainLighting:          ConceptStandard,
	MaintainArt:               ConceptStandard,
	MaintainMedicalReserves:   ConceptStandard,
	MaintainSurgery:           ConceptStandard,
	MaintainPopulation:        ConceptStandard,
	MaintainRefrigeration:     ConceptStandard,
	MaintainResource:          ConceptStandard,
	MaintainRoutes:            ConceptStandard,
	MaintainStoneShell:        ConceptStandard,
	MaintainStorage:           ConceptStandard,
}

// GoalConcept classifies id. Rules carry no GoalID, so no id maps to
// ConceptRule; an unknown id is ConceptUnknown.
func GoalConcept(id GoalID) Concept { return goalConcepts[id] }

// incidentKinds are the Responses whose occurrences live in the incidents
// table instead of the goal table (#1020): the review opens and closes
// their incidents and never files a goal row for them.
var incidentKinds = map[GoalID]bool{
	ActiveCombat:            true,
	CriticalMedicine:        true,
	RestoreWorkers:          true,
	AnswerDialog:            true,
	ConfirmColonyNames:      true,
	EnsureMood:              true,
	RecoverDisasterServices: true,
	TradeWithCaravan:        true,
}

// IsIncidentKind reports whether id's occurrences are incidents (#1020).
func IsIncidentKind(id GoalID) bool { return incidentKinds[id] }
