package bridge

import (
	"context"
	"encoding/json"
	"slices"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// A typed call records every phase it measured under its reviewed native tool,
// and counts the call; the companion's queue/execute split rides in the reply.
func TestTypedCallRecordsPhaseMetrics(t *testing.T) {
	noRollup(t)
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, _ nativeArgument) (*callResult, error) {
		result := pbResult(pbLoaded())
		var envelope map[string]any
		if err := json.Unmarshal(result.StructuredContent.(json.RawMessage), &envelope); err != nil {
			return nil, err
		}
		envelope["timing"] = map[string]any{"queueMs": 0.5, "executeMs": 1.5}
		return structured(string(encode(envelope))), nil
	}}
	client, err := open(context.Background(), "fixture-game", time.Second, false, nil, s.factory(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Close() })
	collect := telemetrytest.Metrics(t)
	if _, _, err = client.Identity(context.Background()); err != nil {
		t.Fatal(err)
	}
	all := collect()
	phases, unit := telemetrytest.SeriesOf(all, telemetry.NativePhaseMetric)
	if unit != "ms" {
		t.Fatalf("unit %q", unit)
	}
	got := map[string]bool{}
	for _, s := range phases {
		if s.Attrs["native_tool"] == "rimgovernor/lifecycle_read_identity" {
			got[s.Attrs["phase"]] = true
		}
		if tool := s.Attrs["native_tool"]; tool != telemetry.Other && !reviewedNativeMethods[tool] && !slices.Contains(wrapperTools, tool) {
			t.Errorf("native_tool %q is outside the closed set", tool)
		}
	}
	for _, phase := range []string{telemetry.PhaseGateWait, telemetry.PhaseCall, telemetry.PhaseDecode, telemetry.PhaseProtoDecode, telemetry.PhaseNativeQueue, telemetry.PhaseNativeExecute} {
		if !got[phase] {
			t.Errorf("no %s phase recorded for the typed call: %v", phase, got)
		}
	}
	calls, _ := telemetrytest.SeriesOf(all, telemetry.NativeCallsMetric)
	ok := false
	for _, s := range calls {
		ok = ok || s.Attrs["native_tool"] == "rimgovernor/lifecycle_read_identity" && s.Attrs["outcome"] == "ok" && s.Value >= 1 // the describe round trip counts under the same tool
	}
	if !ok {
		t.Fatalf("calls: %+v", calls)
	}
}

func TestNativeToolVocabularyIsClosed(t *testing.T) {
	for _, tool := range []string{"rimgovernor/lifecycle_read_identity", "games_call_tool"} {
		if nativeToolVocabulary.Label(tool).String() != tool {
			t.Errorf("%s should be a member", tool)
		}
	}
	for _, tool := range []string{"rimgovernor/probe_adhoc_1234", "", "pawn-9"} {
		if nativeToolVocabulary.Label(tool).String() != telemetry.Other {
			t.Errorf("%q should collapse to other", tool)
		}
	}
}
