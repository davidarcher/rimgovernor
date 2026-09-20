package upkeep

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A changed bill can keep producing after its original order fails inspection.
// This only hands off to watchFeed's bounded NeedRecovered wait and verifyFeed's
// native reachable-stock check; it does not credit production or complete a plan.
func feedBillNeedsRecovery(need policy.GoalID, state store.PlanState) bool {
	if need != policy.MaintainAnimalFeed || len(state.Spec.Actions()) != 1 || len(state.Progress) != 1 {
		return false
	}
	action := state.Spec.Actions()[0]
	if action.Kind() != domain.ProductionBillAction {
		return false
	}
	view := state.Progress[0].View()
	reason, known := view.UnsuccessfulReason.Value()
	return view.Action == action.ID() && view.Stage == domain.Unsuccessful && !view.Unresolved && known && reason == domain.OutcomeNotAchieved
}
