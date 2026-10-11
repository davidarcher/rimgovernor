package bridge

import (
	"errors"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// nativeToolVocabulary is the closed set of native_tool metric values: the
// reviewed native methods and the transport wrappers. Anything else (an
// acceptance probe's ad-hoc tool) is reported as "other".
var nativeToolVocabulary = telemetry.Where(func(tool string) bool {
	return reviewedNativeMethods[tool] || slices.Contains(wrapperTools, tool)
})

// recordCallMetrics records a completed call's measured phases and outcome.
// native reports the companion's own split when its reply carried one.
func recordCallMetrics(nativeTool string, timing *callTiming, call, decode float64, native *nativeTimingReport, err error) {
	tool := nativeToolVocabulary.Label(nativeTool)
	outcome := "ok"
	var refusal *Refusal
	switch {
	case errors.As(err, &refusal):
		outcome = "refused"
	case err != nil:
		outcome = "error"
	}
	telemetry.NativeCall(tool, outcome)
	telemetry.NativePhase(tool, telemetry.PhaseCall, call)
	if timing != nil {
		telemetry.NativePhase(tool, telemetry.PhaseGateWait, millis(timing.gateWait))
	}
	if decode >= 0 {
		telemetry.NativePhase(tool, telemetry.PhaseDecode, decode)
	}
	if native != nil {
		telemetry.NativePhase(tool, telemetry.PhaseNativeQueue, native.queueMs)
		telemetry.NativePhase(tool, telemetry.PhaseNativeExecute, native.executeMs)
	}
}
