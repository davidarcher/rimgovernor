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

// GoalDomain tags id with its colony area; an unknown id is DomainUnknown.
func GoalDomain(id GoalID) Domain { return goalDetectorIndex[id].Domain }
