package buildingruntime

import (
	"context"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// admitMethod runs a planner's building-method admission and logs why a
// refused one was refused (#1233): planners map every refusal to
// shared_admission_refused, which alone hides a development gate from a
// footprint clash.
func admitMethod(ctx context.Context, journal *store.Store, r store.BuildingMethodRequest) (store.BuildingMethodDecision, error) {
	decision, err := journal.AdmitBuildingMethod(ctx, r)
	if err == nil && !decision.Admitted {
		clockEvent(ctx, "clock-scheduler", "admission", "method admission refused", "goal", string(r.Goal), "method", string(r.Method), "refused", refusalSummary(decision.Refused))
	}
	return decision, err
}

// refusalSummary reads each refusal as reason[/resource]@action.
func refusalSummary(refused []policy.Refusal) string {
	if len(refused) == 0 {
		return "none"
	}
	parts := make([]string, len(refused))
	for i, f := range refused {
		s := string(f.Reason)
		if f.Resource != "" {
			s += "/" + string(f.Resource)
		}
		if f.Action != "" {
			s += "@" + string(f.Action)
		}
		parts[i] = s
	}
	return fmt.Sprintf("[%s]", strings.Join(parts, " "))
}
