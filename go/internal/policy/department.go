package policy

// Department is the colony area a Concern belongs to, like a Civ advisor (#1027).
// It is a tag for grouping goals in panels only; it never ranks goals or
// budgets labor. The table matches the department table in
// docs/developers/architecture/control-loop.md.
type Department string

const (
	DepartmentUnknown    Department = ""
	DepartmentFood       Department = "Food"
	DepartmentShelter    Department = "Shelter"
	DepartmentIndustry   Department = "Industry"
	DepartmentMilitary   Department = "Military"
	DepartmentMedical    Department = "Medical"
	DepartmentPeople     Department = "People"
	DepartmentStorage    Department = "Storage"
	DepartmentSanitation Department = "Sanitation"
	// DepartmentSystem holds game-plumbing goals; panels do not show it.
	DepartmentSystem Department = "System"
)

// DepartmentOf tags id with its colony area; an unknown id is DepartmentUnknown.
func DepartmentOf(id ConcernID) Department { return inspectionIndex[id].Department }

// StockpileZoneLimit is the number of stockpile-zone creations one method of
// concern may carry; zero means the concern owns no store. A department's
// concerns own their stores (epic #2176): ClearHomeObstructions keeps its chunk
// dump until that zone goes, and MaintainStockpiles applies every declaration
// of the storage planner in one batch.
func StockpileZoneLimit(concern ConcernID) int {
	if concern == MaintainStockpiles {
		return 16
	}
	if concern == ClearHomeObstructions {
		return 1
	}
	if d, ok := InspectionFor(concern); ok {
		switch d.Department {
		case DepartmentFood, DepartmentIndustry, DepartmentMilitary, DepartmentMedical, DepartmentPeople:
			return 1
		}
	}
	return 0
}
