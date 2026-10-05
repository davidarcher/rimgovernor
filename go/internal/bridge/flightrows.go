package bridge

import (
	"encoding/json"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// Readers of the flight stream understand a kind under both its legacy name
// and the v2 name that replaces it (docs/developers/contracts/flight-rows.md)
// until the producer's piece moves. Each legacy branch below names the issue
// whose landing deletes it.

// IsNativeReply reports whether kind is a completed native call: the
// native_call row (#2057).
func IsNativeReply(kind string) bool { return kind == "native_call" }

// NativeReplyFailed reports whether a native_call row records a failure: ok
// false or an error text.
func NativeReplyFailed(row TimelineRecord) bool {
	if row.Kind != "native_call" {
		return false
	}
	if ok, has := row.Payload["ok"].(bool); has && !ok {
		return true
	}
	text, _ := row.Payload["error"].(string)
	return text != ""
}

// IsDispatchKind reports whether kind is the Worker's per-run row: the legacy
// worker_dispatch and worker_outcome rows (#2064) or the v2 dispatch.
func IsDispatchKind(kind string) bool {
	return kind == "worker_dispatch" || kind == "worker_outcome" || kind == "dispatch"
}

// IsSchedulerStepKind reports whether kind is the clock worker's per-step
// row (scheduler_step; the one place a rename follows).
func IsSchedulerStepKind(kind string) bool { return kind == "scheduler_step" }

// IsClockStopKind reports whether kind is a clock stop: the legacy
// scheduler_stop (#2064) or the v2 clock_stop.
func IsClockStopKind(kind string) bool { return kind == "scheduler_stop" || kind == "clock_stop" }

// IsAuthorityKind reports whether kind records an authority change: the
// legacy authority_change (#2064) or the v2 authority.
func IsAuthorityKind(kind string) bool { return kind == "authority_change" || kind == "authority" }

// IsDecisionRow reports whether the row has the decision shape.
func IsDecisionRow(row TimelineRecord) bool {
	_, ok := row.Payload[telemetry.VerdictKey]
	return ok
}

// RowFields is the row's data under one flat map: a decision row's attrs
// plus its verdict, reason, target and dur_ms (attrs win on a clash), any
// other row's payload.
func RowFields(row TimelineRecord) map[string]any {
	if !IsDecisionRow(row) {
		return row.Payload
	}
	out := map[string]any{}
	for _, key := range []string{telemetry.VerdictKey, telemetry.ReasonKey, telemetry.TargetKey, telemetry.DurMsKey} {
		if v, ok := row.Payload[key]; ok {
			out[key] = v
		}
	}
	if attrs, ok := row.Payload[telemetry.AttrsKey].(map[string]any); ok {
		for k, v := range attrs {
			out[k] = v
		}
	}
	return out
}

// StepFields is a clock_step row's payload in the legacy field names. The v2
// clock_step is a decision row: the step wall is dur_ms (legacy elapsed_ms),
// the step reason is target (legacy reason) and the read tally and the rest
// sit under attrs.
func StepFields(row TimelineRecord) map[string]any {
	if !IsDecisionRow(row) {
		return row.Payload
	}
	out := RowFields(row)
	if ms, ok := out[telemetry.DurMsKey]; ok {
		out["elapsed_ms"] = ms
	}
	if target, ok := out[telemetry.TargetKey].(string); ok && target != "" {
		out["reason"] = target
	}
	return out
}

// WindowRefusal reports whether row is an admission decision that turned the
// clock window down (target window, verdict refused or held), as opposed to a
// method admission.
func WindowRefusal(row TimelineRecord) bool {
	if row.Kind != "admission" {
		return false
	}
	verdict, _ := row.Payload[telemetry.VerdictKey].(string)
	target, _ := row.Payload[telemetry.TargetKey].(string)
	return target == "window" && (verdict == "refused" || verdict == "held")
}

// DecodeFlightLine decodes one flight.jsonl line. It is the one decoder of a
// row: TimelineReader and nativeaccept.FlightTail both use it. A line without
// a sequence and a kind is not a row. A row's missing context or payload
// decodes as an empty map; the timeline reader treats that as corrupt.
func DecodeFlightLine(line []byte) (TimelineRecord, bool) {
	var raw struct {
		Run      string         `json:"run"`
		Sequence *uint64        `json:"sequence"`
		WallTime float64        `json:"wall_time"`
		Kind     *string        `json:"kind"`
		Context  map[string]any `json:"context"`
		Payload  map[string]any `json:"payload"`
	}
	if err := json.Unmarshal(line, &raw); err != nil || raw.Sequence == nil || raw.Kind == nil {
		return TimelineRecord{}, false
	}
	return TimelineRecord{Kind: *raw.Kind, Sequence: *raw.Sequence, HasSeq: true, Run: raw.Run, WallTime: raw.WallTime, Context: raw.Context, Payload: raw.Payload}, true
}
