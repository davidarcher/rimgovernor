package policy

// ConcernType is what kind of thing a Concern is: a Standard, a Project or an
// Incident (#1012). The table matches the type table in
// docs/developers/architecture/control-loop.md.
type ConcernType string

const (
	UnknownConcern  ConcernType = ""
	StandardConcern ConcernType = "Standard"
	ProjectConcern  ConcernType = "Project"
	IncidentConcern ConcernType = "Incident"
)

// ConcernTypeOf classifies id from the inspection registry; an unknown id is
// UnknownConcern.
func ConcernTypeOf(id ConcernID) ConcernType { return inspectionIndex[id].Type }

// IsIncidentKind reports whether id is an Incident Concern, whose occurrences
// are incidents (#1020): the rounds open and close them and never file a
// Standard row for them.
func IsIncidentKind(id ConcernID) bool { return ConcernTypeOf(id) == IncidentConcern }

// IsProjectKind reports whether id is a Project Concern, whose rows live in
// the projects table (#1911): the rounds file one per world and never a
// Standard row.
func IsProjectKind(id ConcernID) bool { return ConcernTypeOf(id) == ProjectConcern }
