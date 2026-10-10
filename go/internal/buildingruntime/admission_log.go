package buildingruntime

import (
	"context"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// admitMethod runs a planner's building-method admission and files one
// admission row per outcome that matters: why a refused one was refused
// (planners map every refusal to shared_admission_refused, which
// alone hides a development gate from a footprint clash) and an admitted
// one, which commits the method to the plan. An error from the store files
// nothing: the planner's own step row carries it.
func admitMethod(ctx context.Context, journal *store.Store, r store.BuildingMethodRequest) (store.BuildingMethodDecision, error) {
	decision, err := journal.AdmitBuildingMethod(ctx, r)
	if err == nil {
		telemetry.Decide(ctx, methodAdmissionDecision(r, decision))
	}
	return decision, err
}

// methodAdmissionDecision is the admission row of a method admission: target
// the method, the concern in attrs, and for a refusal the first refusal's
// reason with every refusal listed in refused.
func methodAdmissionDecision(r store.BuildingMethodRequest, decision store.BuildingMethodDecision) telemetry.Decision {
	d := telemetry.Decision{Kind: "admission", Component: "clock-scheduler", Verdict: "admitted", Reason: "method_committed", Target: string(r.Method),
		Attrs: map[string]any{"concern": r.Owner.OwnerID()}}
	if !decision.Admitted {
		d.Verdict, d.Reason = "refused", "unspecified"
		if len(decision.Refused) > 0 {
			d.Reason = string(decision.Refused[0].Reason)
		}
		d.Attrs["refused"] = refusalSummary(decision.Refused)
	}
	return d
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

// admissionRefused is the verdict of a method the shared admission check
// turned down: it keeps the shared_admission_refused kind and carries the
// decision's first refusal, reason as the subject and resource as the
// detail, so the status line and the journal name why. A decision
// with no refusal listed means the policy admitted fewer candidates than it
// was given without refusing any; that names itself.
func admissionRefused(decision store.BuildingMethodDecision) Verdict {
	if len(decision.Refused) == 0 {
		return refuse(policy.CauseSharedAdmission, "candidates_left_unadmitted", "")
	}
	first := decision.Refused[0]
	return refuse(policy.CauseSharedAdmission, string(first.Reason), string(first.Resource))
}
