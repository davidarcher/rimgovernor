package cases

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// acquireReplay is a fake GABS answering the two wire calls
// ScenarioClock.Acquire issues (authority status, then SetMode Auto).
func acquireReplay(t *testing.T, identity map[string]any) *bridge.Replay {
	t.Helper()
	call := func(method string, request map[string]any, reply map[string]any) bridge.TranscriptRow {
		wire, err := json.Marshal(request)
		if err != nil {
			t.Fatal(err)
		}
		args, err := json.Marshal(map[string]any{"gameId": "game", "tool": "rimgovernor/" + method, "arguments": map[string]any{"request": string(wire)}})
		if err != nil {
			t.Fatal(err)
		}
		payload, err := json.Marshal(reply)
		if err != nil {
			t.Fatal(err)
		}
		result, err := json.Marshal(map[string]any{"structuredContent": map[string]any{"payload": string(payload)}})
		if err != nil {
			t.Fatal(err)
		}
		return bridge.TranscriptRow{Kind: "call", Tool: "games_call_tool", Arguments: args, Result: result}
	}
	rows := []bridge.TranscriptRow{
		{Kind: "session", GameID: "game", Tools: []string{"games_call_tool", "games_tool_detail", "games_tool_names", "games_status", "games_connect", "games_get_attention", "games_ack_attention"}},
		call("authority_read_status", map[string]any{"identity": identity},
			map[string]any{"status": map[string]any{"context": map[string]any{"nativeGeneration": 3}, "active": map[string]any{"mode": "MODE_MANUAL"}}}),
		call("authority_control", map[string]any{"setMode": map[string]any{"identity": identity, "expectedGeneration": "3", "mode": "MODE_AUTO"}},
			map[string]any{"granted": map[string]any{"authority": map[string]any{"mode": "MODE_AUTO"}, "context": map[string]any{"nativeGeneration": 4}}}),
	}
	replay, err := bridge.NewReplay(rows)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { replay.Close() })
	return replay
}

func acquireHarness(t *testing.T, identity map[string]any) (*na.Harness, *bridge.Replay) {
	t.Helper()
	replay := acquireReplay(t, identity)
	client, err := replay.Open(context.Background(), time.Minute)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { client.Close() })
	return na.NewHarness(client, t.TempDir()), replay
}

// Runtime caches the scenario runtime per harness (#597): after
// Serve/Reattach replaces the session's harness, the cached runtime binds
// a closed bridge, so the next Runtime must rebuild it on the new harness
// and re-acquire the clock there rather than hand back the stale one.
func TestRuntimeRebindsAfterReattach(t *testing.T) {
	identity := map[string]any{"colonyId": "colony", "loadToken": "load", "mapId": 0.0}
	first, firstReplay := acquireHarness(t, identity)
	s := &session{report: na.Report{}, Session: &na.Session{Harness: first, Identity: identity, Game: &na.Game{}}}
	ctx := context.Background()
	rt1, err := s.Runtime(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if again, err := s.Runtime(ctx); err != nil || again != rt1 {
		t.Fatalf("second Runtime on the same harness = %p, %v; want the cached %p", again, err, rt1)
	}
	if err := firstReplay.Err(); err != nil {
		t.Fatal(err)
	}

	// Serve releases the bridge; Reattach installs a harness on a new client.
	second, secondReplay := acquireHarness(t, identity)
	s.Session.Harness = second
	rt2, err := s.Runtime(ctx)
	if err != nil {
		t.Fatalf("Runtime after reattach: %v", err)
	}
	if rt2 == rt1 {
		t.Fatal("Runtime returned the runtime bound to the pre-Serve harness")
	}
	if err := secondReplay.Err(); err != nil {
		t.Fatalf("clock was not re-acquired on the reattached harness: %v", err)
	}
	// The rebuilt runtime's Query and Clock.Wire reach the new bridge: the
	// exhausted replay refuses further calls, and the refusal names the
	// call, proving it went out on that client rather than the old one.
	if _, err := rt2.Query(ctx, "probe", "home/status", nil); err == nil || !strings.Contains(err.Error(), "transcript exhausted") {
		t.Fatalf("Query on the rebuilt runtime: %v", err)
	}
	if err := firstReplay.Err(); err != nil {
		t.Fatalf("the probe reached the pre-Serve bridge: %v", err)
	}
	if again, err := s.Runtime(ctx); err != nil || again != rt2 {
		t.Fatalf("Runtime after rebind = %p, %v; want the cached %p", again, err, rt2)
	}
}
