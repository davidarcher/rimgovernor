package policy

// Department is the colony area a Concern belongs to, like a Civ advisor (#1027).
// It is a tag for grouping goals in panels only; it never ranks goals or
// budgets labor. The table matches the department table in
// docs/developers/architecture/control-loop.md.
type Department string

const (
	DepartmentUnknown  Department = ""
	DepartmentFood     Department = "Food"
	DepartmentShelter  Department = "Shelter"
	DepartmentIndustry Department = "Industry"
	DepartmentMilitary Department = "Military"
	DepartmentMedical  Department = "Medical"
	DepartmentPeople   Department = "People"
	DepartmentUpkeep   Department = "Upkeep"
	// DepartmentSystem holds game-plumbing goals; panels do not show it.
	DepartmentSystem Department = "System"
)

// DepartmentOf tags id with its colony area; an unknown id is DepartmentUnknown.
func DepartmentOf(id ConcernID) Department { return inspectionIndex[id].Department }
