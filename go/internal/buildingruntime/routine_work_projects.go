package buildingruntime

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Include selected player intent before admission and admitted shared projects.
// A cancelled, unresolved native order can still need a qualified builder.
func routineProjectDefinitions(plans []store.PlanState, current domain.GenerationSnapshot) []string {
	names := map[string]bool{}
	for _, plan := range plans {
		if plan.Retired {
			continue
		}
		admissions := map[domain.ActionID]domain.GenerationSnapshot{}
		for _, a := range plan.Admissions {
			admissions[a.Action] = a.Admission.Snapshot
		}
		for _, progress := range plan.Progress {
			building, ok := progress.Action().Building()
			if !ok || !domain.GoalWorkOpen([]domain.Progress{progress}) {
				continue
			}
			v := progress.View()
			scope, admitted := admissions[v.Action]
			selected := v.Plan == current.Plan && v.Revision == current.Revision
			if !admitted && !selected {
				continue
			}
			if admitted && (scope.Colony != current.Colony || scope.Load != current.Load || scope.Map != current.Map) {
				continue
			}
			names[building.Definition()] = true
		}
	}
	result := make([]string, 0, len(names))
	for name := range names {
		result = append(result, name)
	}
	sort.Strings(result)
	return result
}

func routineProjectWork(names []string, definitions []observation.PlanningDefinition) domain.Fact[[]policy.WorkRequirement] {
	byName := map[string]observation.PlanningDefinition{}
	for _, d := range definitions {
		byName[d.Name] = d
	}
	minimum := 0
	for _, name := range names {
		d, exists := byName[name]
		skill, known := d.ConstructionSkill.Value()
		if !exists || !known {
			return domain.Unknown[[]policy.WorkRequirement]()
		}
		minimum = max(minimum, int(skill))
	}
	if len(names) == 0 {
		return domain.Known([]policy.WorkRequirement{})
	}
	return domain.Known([]policy.WorkRequirement{{Work: "Construction", Skill: "Construction", Minimum: minimum}})
}
