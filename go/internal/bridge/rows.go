package bridge

import (
	"context"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// Canonical row readers follow docs/developers/contracts/otel-signals.md.

// IsNativeReply identifies completed native calls.
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

// WorkerStepTarget is the target of the clock worker's per-step planner_step row
// (the per-planner rows name the planner); attrs admitted, running,
// window_ticks and planner_failures describe the step.
const WorkerStepTarget = "worker_step"

// IsWorkerStep reports whether row is the clock worker's per-step row.
func IsWorkerStep(row TimelineRecord) bool {
	target, _ := row.Payload[telemetry.TargetKey].(string)
	return row.Kind == "planner_step" && target == WorkerStepTarget
}

// IsCombatStop reports whether row is a clock_stop the combat metrics wrote
// when a combat window resumed (it carries the combat `event`), as opposed to
// the stop a Stopped clock event recorded.
func IsCombatStop(row TimelineRecord) bool {
	_, ok := row.Payload["event"]
	return row.Kind == "clock_stop" && ok
}

// IsWindowStop reports whether row is a clock_stop recorded for a Stopped
// clock event (not a combat resume).
func IsWindowStop(row TimelineRecord) bool {
	return row.Kind == "clock_stop" && !IsCombatStop(row)
}

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

// StepFields is a clock_step decision row's data flat, with the step wall as
// elapsed_ms (dur_ms) and the step's cause as reason (target): the field names
// the step readers aggregate. The read tally and the rest sit under attrs.
func StepFields(row TimelineRecord) map[string]any {
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

// nativeRow writes one of the bridge's own rows (native_call, native_request,
// native_frame, native_poll) as an OTEL log record. It returns the id that ties
// a native_request marker to the native_call answering it. Callers write these
// rows only while c.recording is set. The record's context is filled with INFO,
// now and component bridge where the caller left them out; a durable row is
// fsynced by the file sink at once.
func (c *Client) nativeRow(ctx context.Context, kind string, rowContext map[string]any, durable bool, payload map[string]any) uint64 {
	id := c.rowIDs.Add(1)
	rc := make(map[string]any, len(rowContext)+3)
	for k, v := range rowContext {
		rc[k] = v
	}
	if _, ok := rc["level"]; !ok {
		rc["level"] = "INFO"
	}
	if _, ok := rc["at"]; !ok {
		rc["at"] = time.Now().UTC().Format(time.RFC3339Nano)
	}
	if _, ok := rc[telemetry.ComponentKey]; !ok {
		rc[telemetry.ComponentKey] = "bridge"
	}
	if kind == "native_request" {
		// The marker's identity: the answering native_call's request names it.
		answered := make(map[string]any, len(payload)+1)
		for k, v := range payload {
			answered[k] = v
		}
		answered["request"] = id
		payload = answered
	}
	if durable {
		telemetry.EmitDurableRow(ctx, kind, rc, payload)
	} else {
		telemetry.EmitRow(ctx, kind, rc, payload)
	}
	return id
}
