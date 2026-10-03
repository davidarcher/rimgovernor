package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// herdPolicyOf is the herd plan under the food plan being built: the plan's
// productive-animal terms are demand for the race's job.
func herdPolicyOf(f policy.RoutineFacts, plan policy.FoodPlan) policy.HerdPolicy {
	in := f.HerdPlanInput()
	in.Food = domain.Known(plan)
	return policy.PlanHerd(in).Policy
}
