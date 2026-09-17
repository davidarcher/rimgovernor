package buildingruntime

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Include player guidance before admission and admitted shared projects.
// A cancelled, unresolved native order can still need a qualified builder.
// player maps each submitted plan for the current world to its revision.
// routineOpenActions visits every open action of every live plan that is
// either admitted for the current world or selected player intent. player
// maps each submitted plan for the current world to its revision.
func routineOpenActions(plans []store.PlanState, current domain.GenerationSnapshot, player map[domain.PlanID]uint64, visit func(domain.Action)) {
	for _, plan := range plans {
		if plan.Retired {
			continue
		}
		admissions := map[domain.ActionID]domain.GenerationSnapshot{}
		for _, a := range plan.Admissions {
			admissions[a.Action] = a.Admission.Snapshot
		}
		for _, progress := range plan.Progress {
			if !domain.GoalWorkOpen([]domain.Progress{progress}) {
				continue
			}
			v := progress.View()
			scope, admitted := admissions[v.Action]
			selected := v.Plan == current.Plan && v.Revision == current.Revision || domain.PlanRevision(player[v.Plan]) == v.Revision
			if !admitted && !selected {
				continue
			}
			if admitted && (scope.Colony != current.Colony || scope.Load != current.Load || scope.Map != current.Map) {
				continue
			}
			visit(progress.Action())
		}
	}
}

func routineProjectDefinitions(plans []store.PlanState, current domain.GenerationSnapshot, player map[domain.PlanID]uint64) []string {
	names := map[string]bool{}
	routineOpenActions(plans, current, player, func(a domain.Action) {
		if building, ok := a.Building(); ok {
			names[building.Definition()] = true
		}
	})
	result := make([]string, 0, len(names))
	for name := range names {
		result = append(result, name)
	}
	sort.Strings(result)
	return result
}

// routineProjectBill is one open production bill's bench and recipe, the
// pair whose census row names the work type a pawn needs enabled for it.
type routineProjectBill struct{ Bench, Recipe string }

func routineProjectBills(plans []store.PlanState, current domain.GenerationSnapshot, player map[domain.PlanID]uint64) []routineProjectBill {
	seen := map[routineProjectBill]bool{}
	var result []routineProjectBill
	routineOpenActions(plans, current, player, func(a domain.Action) {
		bill, ok := a.ProductionBill()
		if !ok {
			return
		}
		row := routineProjectBill{Bench: bill.Bench(), Recipe: bill.Recipe()}
		if !seen[row] {
			seen[row] = true
			result = append(result, row)
		}
	})
	sort.Slice(result, func(i, j int) bool {
		return result[i].Bench < result[j].Bench || result[i].Bench == result[j].Bench && result[i].Recipe < result[j].Recipe
	})
	return result
}

// routineBillWork resolves each open bill against the fresh bench census:
// the recipe row on the bill's bench names the work type and skill floor.
// A bill whose bench or recipe the census no longer describes, or whose
// work is unobserved, makes the whole requirement Unknown; the planner
// then waits rather than assigning against a guess.
func routineBillWork(bills []routineProjectBill, census []bridge.GearBenchRead) domain.Fact[[]policy.WorkRequirement] {
	if len(bills) == 0 {
		return domain.Known([]policy.WorkRequirement{})
	}
	benches := map[string]policy.GearBench{}
	for _, row := range census {
		benches[row.Bench.ID] = row.Bench
	}
	var out []policy.WorkRequirement
	for _, bill := range bills {
		bench, exists := benches[bill.Bench]
		recipes, known := bench.Recipes.Value()
		if !exists || !known {
			return domain.Unknown[[]policy.WorkRequirement]()
		}
		found := false
		for _, recipe := range recipes {
			if recipe.Definition != bill.Recipe {
				continue
			}
			work, known := recipe.RequiredWork.Value()
			if !known {
				return domain.Unknown[[]policy.WorkRequirement]()
			}
			out = mergeWorkRequirements(out, work)
			found = true
		}
		if !found {
			return domain.Unknown[[]policy.WorkRequirement]()
		}
	}
	return domain.Known(out)
}

// mergeWorkRequirements folds rows by work type keeping the highest skill
// floor, in first-seen order.
func mergeWorkRequirements(into, rows []policy.WorkRequirement) []policy.WorkRequirement {
	for _, row := range rows {
		merged := false
		for i := range into {
			if into[i].Work == row.Work {
				into[i].Minimum = max(into[i].Minimum, row.Minimum)
				if into[i].Skill == "" {
					into[i].Skill = row.Skill
				}
				merged = true
			}
		}
		if !merged {
			into = append(into, row)
		}
	}
	return into
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
