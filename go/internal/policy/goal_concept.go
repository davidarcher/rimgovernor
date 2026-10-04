package policy

// Concept is the kind of thing a GoalID actually is (#1012). The table
// matches the concept table in docs/developers/architecture/control-loop.md.
type Concept string

const (
	ConceptUnknown   Concept = ""
	ConceptStandard  Concept = "Standard"
	ConceptProject   Concept = "Project"
	ConceptResponse  Concept = "Response"
	ConceptSafeguard Concept = "Safeguard"
)

// GoalConcept classifies id from the detector registry. Safeguards carry no GoalID, so no id maps to
// ConceptSafeguard; an unknown id is ConceptUnknown.
func GoalConcept(id GoalID) Concept { return goalDetectorIndex[id].Concept }

// IsIncidentKind reports whether id is a Response, whose occurrences are
// incidents (#1020): the review opens and closes them and never files a goal
// row for them.
func IsIncidentKind(id GoalID) bool { return GoalConcept(id) == ConceptResponse }

// IsProjectKind reports whether id is a Project, whose rows live in the
// projects table (#1911): the review files one per world and never a goal row.
func IsProjectKind(id GoalID) bool { return GoalConcept(id) == ConceptProject }
