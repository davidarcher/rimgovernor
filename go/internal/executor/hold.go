package executor

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// reasonHeldReasons maps every policy.Reason a family's Admit-style check can
// return -- both the emergency subset (already handled by holdEmergency's own
// narrower EmergencyReason mapping, duplicated here so inspect()'s generic
// building refusal path and every per-family EvaluateXxx path share one
// table) and the ordinary (non-emergency) admission-refusal reasons -- to its
// durable domain.HeldReason mirror. A reason absent from this table is a bug:
// holdRefusal drops it silently rather than persisting a wrong one, so this
// must stay exhaustive over policy.Reason's declared constants.
var reasonHeldReasons = map[policy.Reason]domain.HeldReason{
	policy.UnsafeThreat:    domain.HeldUnsafeThreat,
	policy.CriticalMedical: domain.HeldCriticalMedical,
	policy.StaleFacts:      domain.HeldStaleFacts,
	policy.UnknownFacts:    domain.HeldUnknownFacts,

	policy.NotReady:          domain.HeldNotReady,
	policy.AlreadyReserved:   domain.HeldAlreadyReserved,
	policy.UnsafePlacement:   domain.HeldUnsafePlacement,
	policy.MaterialRequired:  domain.HeldMaterialRequired,
	policy.DependencyBlocked: domain.HeldDependencyBlocked,
	policy.GeometryBlocked:   domain.HeldGeometryBlocked,
	policy.InvalidHeld:       domain.HeldInvalidHeld,

	policy.NativeIneligible:          domain.HeldNativeIneligible,
	policy.ExcavationUnsupported:     domain.HeldExcavationUnsupported,
	policy.ExcavationGeometryChanged: domain.HeldExcavationGeometryChanged,
	policy.UnsafeRoute:               domain.HeldUnsafeRoute,
	policy.RoofSupportRisk:           domain.HeldRoofSupportRisk,
	policy.StorageMissing:            domain.HeldStorageMissing,
	policy.UrgentCompetingWork:       domain.HeldUrgentCompetingWork,
}

// holdRefusal durably records a refused-but-not-yet-dispatched action's
// ordinary (non-emergency) admission-refusal reasons via journal.Hold, the
// same deduplicated, best-effort pattern holdEmergency already established:
// a durable-write failure here must not mask the caller's ErrHeld return --
// the action stays Pending/Prepared regardless, so the next inspection
// recomputes and retries recording the reason. Shared by every family whose
// Admit-style check produces []policy.Refusal, not just the emergency-aware
// ones holdEmergency serves.
func (e *Executor) holdRefusal(ctx context.Context, plan domain.PlanID, actionID domain.ActionID, refused []policy.Refusal, tick domain.Tick, progress domain.Progress) domain.Progress {
	seen := map[domain.HeldReason]bool{}
	var reasons []domain.HeldReason
	for _, refusal := range refused {
		held, ok := reasonHeldReasons[refusal.Reason]
		if !ok || seen[held] {
			continue
		}
		seen[held] = true
		reasons = append(reasons, held)
	}
	if len(reasons) == 0 {
		return progress
	}
	if next, err := e.journal.Hold(ctx, plan, actionID, reasons, tick); err == nil {
		return next
	}
	return progress
}
