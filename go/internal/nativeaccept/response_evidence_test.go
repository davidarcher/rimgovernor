package nativeaccept

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

func TestResponseEvidenceKeepsAcceptanceSeparateFromNativeEffect(t *testing.T) {
	path := telemetrytest.WriteLogs(t, t.TempDir(),
		telemetrytest.Row{Kind: "planner_step", Context: map[string]any{"tick": 102}, Payload: map[string]any{"verdict": "ok", "target": "rounds", "attrs": map[string]any{"scope": "immediate", "observation_tick": 102}}},
		telemetrytest.Row{Kind: "planner_step", Context: map[string]any{"tick": 103}, Payload: map[string]any{"verdict": "admitted", "target": "tend", "attrs": map[string]any{"scope": "immediate"}}},
		telemetrytest.Row{Kind: "dispatch", Context: map[string]any{"tick": 104}, Payload: map[string]any{"verdict": "completed", "attrs": map[string]any{"dispatch_tick": 104, "receipt": "accepted"}}},
		telemetrytest.Row{Kind: "clock_step", Context: map[string]any{}, Payload: map[string]any{"controller_pause_ms": 200, "critical_wave_ms": 150}},
	)
	r, err := NewResponseEvidence("medical/fixture", "Fast", 100)
	if err != nil {
		t.Fatal(err)
	}
	if err := r.ReadFlight(path); err != nil {
		t.Fatal(err)
	}
	if r.DetectionTick == nil || *r.DetectionTick != 102 || r.DecisionTick == nil || *r.DecisionTick != 103 || r.DispatchTick == nil || *r.DispatchTick != 104 || r.FirstEffectTick != nil {
		t.Fatalf("phases or unknown native outcome lost: %+v", r)
	}
	if len(r.ControllerPauseMS) != 1 || r.ControllerPauseMS[0] != 200 || len(r.ReviewInclusiveMS) != 1 || r.ReviewInclusiveMS[0] != 150 {
		t.Fatalf("wall accounting: %+v", r)
	}
	r.ObserveEffect(90, "setup")
	if r.FirstEffectTick != nil {
		t.Fatal("pre-onset activity became a response")
	}
	r.ObserveEffect(110, "observed_tended")
	if *r.FirstEffectTick != 110 {
		t.Fatal(r)
	}
}
