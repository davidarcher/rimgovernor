package store

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Replacement is observed and guarded natively by owned bill identity and
// unchanged settings. It may supersede an iteration still waiting for inputs.
func mealReplacementOpenWorkExempt(goal GoalState, plan domain.PlanSpec) bool {
	if len(plan.Actions()) != 1 || goal.Goal.Source != domain.AutopilotGoal {
		return false
	}
	bill, ok := plan.Actions()[0].ProductionBill()
	return ok && bill.Mode() == domain.FoodTarget && bill.Replaces() != ""
}
