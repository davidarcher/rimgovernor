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

// GoalConcept classifies id from the detector registry. Rules carry no GoalID, so no id maps to
// ConceptRule; an unknown id is ConceptUnknown.
func GoalConcept(id GoalID) Concept { return goalDetectorIndex[id].Concept }

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
