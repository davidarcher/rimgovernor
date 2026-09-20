package nativeaccept

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// The transcripts under testdata/transcripts are harness-shaped recordings
// (RecordEnv's format, the receipts shaped as GABS returns them); a live run
// under RIMGOVERNOR_ACCEPT_RECORD replaces them with real ones. Each test
// here runs harness code against one in milliseconds and ends with the
// transcript fully consumed, so a wait or parser that starts making a
// different call sequence fails here before a run does (#282).

func replayHarness(t *testing.T, name string) (*Harness, *bridge.Replay) {
	t.Helper()
	h, replay, err := ReplayHarness(context.Background(), filepath.Join("testdata", "transcripts", name), t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = h.Client.Close(); replay.Close() })
	return h, replay
}

func requireConsumed(t *testing.T, replay *bridge.Replay) {
	t.Helper()
	if err := replay.Err(); err != nil {
		t.Fatal(err)
	}
	if remaining := replay.Remaining(); remaining != 0 {
		t.Fatalf("%d recorded calls were never made", remaining)
	}
}

func TestReplayTickBoundedWait(t *testing.T) {
	h, replay := replayHarness(t, "tick.jsonl")
	err := WaitProgress(context.Background(), Wait{Ticks: 1000, Tick: h.Tick, Interval: time.Millisecond}, func(context.Context) (string, bool, error) {
		return Signature("plan", "waiting"), false, nil
	})
	var wait *WaitError
	if !errors.As(err, &wait) || wait.Outcome != WaitTicks {
		t.Fatalf("expected the tick budget to end the wait: %v", err)
	}
	if wait.TicksElapsed != 1300 || wait.Rounds != 3 || wait.Signature != "plan|waiting" {
		t.Fatalf("wait error: %+v", wait)
	}
	requireConsumed(t, replay)
	// Every replayed call still lands as an evidence row carrying its tick.
	rows, _ := filepath.Glob(filepath.Join(h.Output, "*-tick.json"))
	if len(rows) != 3 {
		t.Fatalf("evidence rows: %v", rows)
	}
	data, _ := os.ReadFile(rows[2])
	if !strings.Contains(string(data), `"tick": 1400`) {
		t.Fatalf("last evidence row lacks the reply tick:\n%s", data)
	}
}

func TestReplayHarnessTick(t *testing.T) {
	h, replay := replayHarness(t, "tick.jsonl")
	tick, err := h.Tick(context.Background())
	if err != nil || tick != 100 {
		t.Fatalf("tick %d: %v", tick, err)
	}
	if replay.Consumed() != 1 || replay.Remaining() != 2 {
		t.Fatalf("consumed %d remaining %d", replay.Consumed(), replay.Remaining())
	}
}

func TestReplayDiscoveryAndFixtureToolWait(t *testing.T) {
	h, replay := replayHarness(t, "discovery.jsonl")
	names, err := h.Discovery(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if strings.Join(names, ",") != "home/status,home/colony_facts,rimgovernor/lifecycle_read_identity" {
		t.Fatalf("discovery: %v", names)
	}
	if err := WaitForNativeTool(context.Background(), h.Client, "test/throughput_prepare", time.Minute); err != nil {
		t.Fatal(err)
	}
	requireConsumed(t, replay)
}

func TestReplayAuthorityCeremony(t *testing.T) {
	h, replay := replayHarness(t, "authority.jsonl")
	identity := map[string]any{"colonyId": "e8e3bf970585487baec750084f138753", "loadToken": "8de1a627d4a64722a24156436f4fca81", "mapId": 0}
	grant, err := GrantAuto(context.Background(), h.WireFunc(), "grant", identity)
	if err != nil {
		t.Fatal(err)
	}
	if GrantGeneration(grant) != 2 {
		t.Fatalf("grant generation: %v", grant)
	}
	revoked, err := RevokeManual(context.Background(), h.WireFunc(), "release", identity, grant)
	if err != nil {
		t.Fatal(err)
	}
	if AsString(revoked["reason"]) != "REVOCATION_REASON_MANUAL" {
		t.Fatalf("revoked: %v", revoked)
	}
	requireConsumed(t, replay)
}

func TestReplayRefusalReachesTheHarness(t *testing.T) {
	h, replay := replayHarness(t, "refused.jsonl")
	identity := map[string]any{"colonyId": "e8e3bf970585487baec750084f138753", "loadToken": "8de1a627d4a64722a24156436f4fca81", "mapId": 0}
	_, err := h.Wire(context.Background(), "colony-facts-poll", "observations_read_colony_facts", map[string]any{
		"page": map[string]any{"limit": 256}, "planning": false, "scope": map[string]any{"expectedIdentity": identity},
	})
	if !errors.Is(err, bridge.ErrRefused) {
		t.Fatalf("expected the recorded refusal: %v", err)
	}
	var refusal *bridge.Refusal
	if !errors.As(err, &refusal) || len(refusal.Result.Text) != 1 || !strings.Contains(refusal.Result.Text[0], "not found for game") {
		t.Fatalf("refusal text: %+v", refusal)
	}
	requireConsumed(t, replay)
	rows, _ := filepath.Glob(filepath.Join(h.Output, "0001-colony-facts-poll.json"))
	if len(rows) != 1 {
		t.Fatalf("evidence row: %v", rows)
	}
	data, _ := os.ReadFile(rows[0])
	if !strings.Contains(string(data), `"error": "bridge read refused: rimgovernor/observations_read_colony_facts: Tool 'rimgovernor/observations_read_colony_facts' not found`) {
		t.Fatalf("evidence row lacks the refusal:\n%s", data)
	}
}

func TestReplayUnrecordedCallFailsWithDiff(t *testing.T) {
	h, replay := replayHarness(t, "tick.jsonl")
	if _, err := h.Wire(context.Background(), "tick", "lifecycle_read_identity", map[string]any{"scope": "all"}); err == nil {
		t.Fatal("a call the transcript never recorded succeeded")
	}
	var mismatch *bridge.ReplayMismatch
	if !errors.As(replay.Err(), &mismatch) || mismatch.Sequence != 2 || mismatch.Phase != "tick" {
		t.Fatalf("mismatch: %v", replay.Err())
	}
	text := replay.Err().Error()
	for _, want := range []string{"row 2 (phase tick)", `-     "request": "{}"`, `+     "request": "{\"scope\":\"all\"}"`} {
		if !strings.Contains(text, want) {
			t.Fatalf("mismatch text lacks %q:\n%s", want, text)
		}
	}
	// Past the recording's end.
	h2, replay2 := replayHarness(t, "tick.jsonl")
	for i := 0; i < 3; i++ {
		if _, err := h2.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := h2.Call(context.Background(), "status", "home/status", nil); err == nil {
		t.Fatal("a call past the transcript succeeded")
	}
	if err := replay2.Err(); err == nil || !strings.Contains(err.Error(), "exhausted; unrecorded call games_call_tool home/status") {
		t.Fatalf("exhausted: %v", err)
	}
}

func TestRecordingTranscriptFollowsEnv(t *testing.T) {
	t.Setenv(RecordEnv, "")
	config, err := WithRecording(bridge.ProcessConfig{GameID: "g"})
	if err != nil || config.Transcript != nil {
		t.Fatalf("unset env attached a transcript: %+v %v", config.Transcript, err)
	}
	own := &bridge.Transcript{}
	config, err = WithRecording(bridge.ProcessConfig{Transcript: own})
	if err != nil || config.Transcript != own {
		t.Fatal("an explicit transcript was replaced")
	}
}
