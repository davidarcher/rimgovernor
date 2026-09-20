package cases

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/postmortem"
)

// These are retained step shapes, including the MCP content blocks that used
// to be the only place a remote failure's cause could be found.
func TestFailedStepReachesCaseResultAndDiagnosis(t *testing.T) {
	for _, tc := range []struct{ name, step, summary, kind, detail string }{
		{"fixture", `{"request":{"tool":"test/berserk_prepare","arguments":{}},"result":{"isError":true,"content":[{"type":"text","text":"Prepare failed"}],"structuredContent":{"exception":"System.InvalidOperationException: Healthy baseline required.\n at BerserkFixture.Prepare()"}}}`, "System.InvalidOperationException: Healthy baseline required.", "fixture exception", "BerserkFixture.Prepare"},
		{"attention", `{"request":{"tool":"test/berserk_prepare","arguments":{}},"result":{"isError":true,"content":[{"type":"text","text":"Tool blocked by blocking attention.\nattentionId: attention-42\nSummary: Game logged a mote exception\nSample messages:\nNullReferenceException in Mote.Tick"}],"structuredContent":{"attentionId":"attention-42","sampleMessages":["NullReferenceException in Mote.Tick"]}}}`, "Game logged a mote exception", "blocking attention", "attention-42"},
		{"prerequisite", `{"request":{"tool":"test/berserk_prepare","arguments":{}},"result":{"isError":true,"content":[{"type":"text","text":"Required map is not loaded.\nLoad a colony before retrying."}]}}`, "Required map is not loaded.", "refusal", "Load a colony before retrying."},
	} {
		t.Run(tc.name, func(t *testing.T) {
			var step struct {
				Request struct {
					Tool      string          `json:"tool"`
					Arguments json.RawMessage `json:"arguments"`
				} `json:"request"`
				Result json.RawMessage `json:"result"`
			}
			if err := json.Unmarshal([]byte(tc.step), &step); err != nil {
				t.Fatal(err)
			}
			dir := t.TempDir()
			transcript := filepath.Join(t.TempDir(), "transcript.jsonl")
			row, err := json.Marshal(map[string]any{"sequence": 2, "kind": "call", "tool": "games_call_tool", "native_tool": step.Request.Tool, "arguments": map[string]any{"gameId": "trial", "tool": step.Request.Tool, "arguments": step.Request.Arguments}, "result": step.Result})
			if err != nil {
				t.Fatal(err)
			}
			data := append([]byte("{\"sequence\":1,\"kind\":\"session\",\"game_id\":\"trial\",\"tools\":[\"games_call_tool\",\"games_tool_detail\",\"games_tool_names\",\"games_status\",\"games_connect\",\"games_start\",\"games_stop\"]}\n"), row...)
			if err := os.WriteFile(transcript, data, 0644); err != nil {
				t.Fatal(err)
			}
			h, replay, err := na.ReplayHarness(context.Background(), transcript, dir)
			if err != nil {
				t.Fatal(err)
			}
			defer replay.Close()
			defer h.Client.Close()
			_, err = h.Call(context.Background(), "prepare", step.Request.Tool, nil)
			var refusal *bridge.Refusal
			if !errors.Is(err, bridge.ErrRefused) || !errors.As(err, &refusal) {
				t.Fatalf("lost refusal: %v", err)
			}
			if !strings.Contains(err.Error(), step.Request.Tool+": "+tc.summary) || !strings.Contains(err.Error(), tc.kind) || strings.Contains(err.Error(), "\n") {
				t.Fatalf("error: %v", err)
			}
			if err := replay.Err(); err != nil {
				t.Fatal(err)
			}
			report := na.NewReport("step failure", true)
			report["error"] = err.Error()
			diagnose(context.Background(), dir, report)
			if report.Finalize(dir) != 1 {
				t.Fatal("failed case passed")
			}
			result, err := os.ReadFile(filepath.Join(dir, "result.json"))
			if err != nil {
				t.Fatal(err)
			}
			var saved struct {
				Error string `json:"error"`
			}
			if err := json.Unmarshal(result, &saved); err != nil {
				t.Fatal(err)
			}
			if !strings.Contains(saved.Error, tc.summary) {
				t.Fatalf("result error: %s", saved.Error)
			}
			diagnosis, err := os.ReadFile(filepath.Join(dir, "diagnosis.txt"))
			if err != nil {
				t.Fatal(err)
			}
			for _, want := range []string{tc.kind, tc.summary, "0001-prepare.json result"} {
				if !strings.Contains(string(diagnosis), want) {
					t.Fatalf("diagnosis lacks %q: %s", want, diagnosis)
				}
			}
			if tc.name == "attention" {
				for _, want := range []string{"attention-42", "NullReferenceException in Mote.Tick"} {
					if !strings.Contains(string(diagnosis), want) {
						t.Fatalf("attention lacks %q: %s", want, diagnosis)
					}
				}
			}
			evidence, err := os.ReadFile(filepath.Join(dir, "0001-prepare.json"))
			if err != nil || !strings.Contains(string(evidence), tc.detail) {
				t.Fatalf("full detail lost: %v %s", err, evidence)
			}
			// Old runs carry the bare error: diagnosis must use their receipt too.
			if err := os.WriteFile(filepath.Join(dir, "0001-prepare.json"), []byte(tc.step), 0644); err != nil {
				t.Fatal(err)
			}
			if got := postmortem.Collect(context.Background(), dir, nil).Text(); !strings.Contains(got, tc.kind+": "+step.Request.Tool+": "+tc.summary) {
				t.Fatalf("old step: %s", got)
			}
		})
	}
}
