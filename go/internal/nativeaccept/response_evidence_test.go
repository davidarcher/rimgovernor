package nativeaccept

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestResponseEvidenceKeepsAcceptanceSeparateFromNativeEffect(t *testing.T) {
	path := filepath.Join(t.TempDir(), "flight.jsonl")
	rows := []string{
		`{"sequence":1,"kind":"planner_step","context":{"tick":102},"payload":{"verdict":"ok","target":"rounds","attrs":{"scope":"immediate","observation_tick":102}}}`,
		`{"sequence":2,"kind":"planner_step","context":{"tick":103},"payload":{"verdict":"admitted","target":"tend","attrs":{"scope":"immediate"}}}`,
		`{"sequence":3,"kind":"dispatch","context":{"tick":104},"payload":{"verdict":"completed","attrs":{"dispatch_tick":104,"receipt":"accepted"}}}`,
		`{"sequence":4,"kind":"clock_step","context":{},"payload":{"controller_pause_ms":200,"critical_wave_ms":150}}`,
	}
	if err := os.WriteFile(path, []byte(strings.Join(rows, "\n")+"\n"), 0600); err != nil {
		t.Fatal(err)
	}
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
