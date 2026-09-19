package nativeaccept

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestEvidenceSequenceContinuesAcrossHarnessesOnOneOutput(t *testing.T) {
	output := t.TempDir()
	first := NewHarness(nil, output)
	second := NewHarness(nil, filepath.Join(output, "..", filepath.Base(output)))
	other := NewHarness(nil, filepath.Join(output, "warm"))
	if got := nextEvidenceSequence(first.Output); got != 1 {
		t.Fatalf("first sequence = %d", got)
	}
	if got := nextEvidenceSequence(second.Output); got != 2 {
		t.Errorf("a reattached harness on the same output restarted at %d", got)
	}
	if got := nextEvidenceSequence(other.Output); got != 1 {
		t.Errorf("another output directory shares the sequence: %d", got)
	}
	if got := evidencePath(output, 12, "pause"); got != filepath.Join(output, "0012-pause.json") {
		t.Errorf("evidence path = %s", got)
	}
}

func TestEvidenceRowCarriesObservationStamps(t *testing.T) {
	sent := time.Date(2026, 9, 18, 10, 30, 0, 123456789, time.UTC)
	tick := uint64(90010)
	row := evidenceRow(7, "home/status", json.RawMessage(`{}`), sent, 250*time.Millisecond, json.RawMessage(`{"ok":true}`), nil, &tick)
	output := t.TempDir()
	writeEvidence(evidencePath(output, 7, "status"), row)
	data, err := os.ReadFile(filepath.Join(output, "0007-status.json"))
	if err != nil {
		t.Fatal(err)
	}
	var written map[string]any
	if err := json.Unmarshal(data, &written); err != nil {
		t.Fatal(err)
	}
	if written["sequence"] != 7.0 || written["observed_at"] != "2026-09-18T10:30:00.123Z" || written["elapsed_ms"] != 250.0 || written["tick"] != 90010.0 {
		t.Errorf("stamps = sequence %v observed_at %v elapsed_ms %v tick %v", written["sequence"], written["observed_at"], written["elapsed_ms"], written["tick"])
	}
	if result, _ := written["result"].(map[string]any); result["ok"] != true {
		t.Errorf("result = %v", written["result"])
	}

	failed := evidenceRow(8, "home/status", json.RawMessage(`{}`), sent, time.Second, nil, errors.New("timed out"), nil)
	if failed["error"] != "timed out" {
		t.Errorf("error = %v", failed["error"])
	}
	for _, absent := range []string{"result", "tick"} {
		if _, has := failed[absent]; has {
			t.Errorf("a failed call without an envelope or tick carries %s", absent)
		}
	}
}
