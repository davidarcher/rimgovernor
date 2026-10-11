package policy

// ConcernType is what kind of thing a Concern is: a Standard, a Once
// Standard (a build-once outcome that settles once and only a measured
// regression reopens) or an Incident. The table matches the type table in
// docs/developers/architecture/control-loop.md.
type ConcernType string

const (
	UnknownConcern  ConcernType = ""
	StandardConcern ConcernType = "Standard"
	OnceConcern     ConcernType = "Once"
	IncidentConcern ConcernType = "Incident"
)

// ConcernTypeOf classifies id from the inspection registry; an unknown id is
// UnknownConcern.
func ConcernTypeOf(id ConcernID) ConcernType { return inspectionIndex[id].Type }

// IsIncidentKind reports whether id is an Incident Concern, whose occurrences
// are incidents: the rounds open and close them and never file a
// Standard row for them.
func IsIncidentKind(id ConcernID) bool { return ConcernTypeOf(id) == IncidentConcern }

// IsStandardKind reports whether id is a Standard Concern, Once or not: the
// rounds file one Standard row per world for it.
func IsStandardKind(id ConcernID) bool {
	t := ConcernTypeOf(id)
	return t == StandardConcern || t == OnceConcern
}

// IsOnceKind reports whether id's Standard is Once (domain.Standard.Once).
func IsOnceKind(id ConcernID) bool { return ConcernTypeOf(id) == OnceConcern }
