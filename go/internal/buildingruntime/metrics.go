package buildingruntime

import (
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// The closed sets the scheduler, admission and dispatch metrics draw their
// attribute values from. A value outside a set is reported as "other".
var (
	stepCauseVocabulary = telemetry.Words(string(StepTimer), string(StepWake), string(StepSettled), string(StepFull), string(StepLive))
	// refusalReasonVocabulary is every policy.Reason; a test keeps it equal to
	// the constants declared in package policy.
	refusalReasonVocabulary = telemetry.Words(
		string(policy.NotReady), string(policy.AlreadyReserved), string(policy.UnknownFacts), string(policy.StaleFacts),
		string(policy.UnsafePlacement), string(policy.UnsafeThreat), string(policy.CriticalMedical), string(policy.MaterialRequired),
		string(policy.DependencyBlocked), string(policy.GeometryBlocked), string(policy.InvalidHeld),
		"unsafe_route", "roof_support_risk", "missing_storage", "urgent_competing_work")
	// actionKindVocabulary is the kinds the Worker may dispatch.
	actionKindVocabulary = telemetry.Where(func(kind string) bool { return roundsExecutableKind(domain.ActionKind(kind)) })
)

func millisOf(d time.Duration) float64 { return float64(d) / float64(time.Millisecond) }

// recordAdmission counts a method admission by verdict and, per refusal, its reason.
func recordAdmission(admitted bool, refused []policy.Refusal) {
	if admitted {
		telemetry.Admission("admitted", telemetry.NoReason)
		return
	}
	if len(refused) == 0 {
		telemetry.Admission("refused", refusalReasonVocabulary.Label("unspecified"))
		return
	}
	for _, f := range refused {
		telemetry.Admission("refused", refusalReasonVocabulary.Label(string(f.Reason)))
	}
}

// recordDispatchOutcome counts one dispatched action: failed on a returned
// error, refused (once per reason) when the game or executor declined it, else
// applied.
func recordDispatchOutcome(kind domain.ActionKind, result executor.Result, err error) {
	label := actionKindVocabulary.Label(string(kind))
	switch {
	case err != nil:
		telemetry.ActionOutcome(label, "failed", telemetry.NoReason)
	case len(result.Refused) > 0:
		for _, r := range result.Refused {
			telemetry.ActionOutcome(label, "refused", refusalReasonVocabulary.Label(string(r.Reason)))
		}
	default:
		telemetry.ActionOutcome(label, "applied", telemetry.NoReason)
	}
}

// recordStepMetrics records what a finished scheduler step measured. The
// pause, stop and pace figures come from the fields the step's clock_step row
// carries, so the metric and the row cannot disagree about when one applies.
func recordStepMetrics(cause StepCause, elapsed, criticalWave time.Duration, row map[string]any) {
	telemetry.SchedulerStep(stepCauseVocabulary.Label(string(cause)), millisOf(elapsed))
	if criticalWave > 0 {
		telemetry.CriticalWave(millisOf(criticalWave))
	}
	if ms, ok := row["stop_latency_ms"].(float64); ok {
		telemetry.StopLatency(ms)
	}
	if ms, ok := row["controller_pause_ms"].(float64); ok {
		telemetry.ControllerPause(ms)
	}
	if s, ok := row["stop_pause_s"].(float64); ok {
		telemetry.StopPause(s * 1000)
	}
	if tps, ok := row["effective_tps"].(float64); ok {
		telemetry.TicksPerSecond(tps)
	}
}
