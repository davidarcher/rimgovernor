package nativeaccept

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

func TestHarnessAttention(t *testing.T) {
	const blocked = `{"isError":true,"structuredContent":{"status":"blocked_by_attention","attention":{"attentionId":"old"}},"content":[{"type":"text","text":"blocking attention"}]}`
	const attention = `{"structuredContent":{"supported":true,"blocking":true,"attention":{"attentionId":"current","severity":"error","summary":"genuine simulation fault","samples":["NullReferenceException in simulation"]}}}`
	const ok = `{"structuredContent":{"success":true}}`
	const refused = `{"isError":true,"content":[{"type":"text","text":"native fault"}]}`
	for _, tc := range []struct {
		name, tool, first, read, ack, second string
		wantErr, wantRefused                 bool
	}{
		{name: "debug start", tool: "rimworld/start_debug_game_ready", first: ok, read: attention, ack: ok},
		{name: "blocked then recovered", first: blocked, read: attention, ack: ok, second: ok},
		{name: "no attention", tool: "rimworld/start_debug_game_ready", first: ok, read: `{"structuredContent":{"supported":true,"attention":null,"blocking":false}}`},
		{name: "cleared before read", first: blocked, read: `{"structuredContent":{"attention":null}}`, second: ok},
		{name: "read fails", first: blocked, read: refused, wantErr: true, wantRefused: true},
		{name: "ack fails", first: blocked, read: attention, ack: refused, wantErr: true, wantRefused: true},
		{name: "missing id", first: blocked, read: `{"structuredContent":{"attention":{"summary":"bad"}}}`, wantErr: true, wantRefused: true},
		{name: "malformed read", tool: "rimworld/start_debug_game_ready", first: ok, read: `{"structuredContent":{"attention":42}}`, wantErr: true},
		{name: "retry remains blocked", first: blocked, read: attention, ack: ok, second: blocked, wantErr: true, wantRefused: true},
		{name: "retry genuine failure", first: blocked, read: attention, ack: ok, second: refused, wantErr: true, wantRefused: true},
		{name: "ordinary refusal", first: refused, wantErr: true, wantRefused: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			tool := tc.tool
			if tool == "" {
				tool = "rimworld/set_time_speed"
			}
			rows := []bridge.TranscriptRow{{Kind: "session", GameID: "game", Tools: []string{"games_call_tool", "games_tool_detail", "games_tool_names", "games_status", "games_connect", "games_get_attention", "games_ack_attention"}}}
			add := func(tool string, args, result string) {
				rows = append(rows, bridge.TranscriptRow{Kind: "call", Tool: tool, Arguments: json.RawMessage(args), Result: json.RawMessage(result)})
			}
			args := `{"gameId":"game","tool":"` + tool + `","arguments":{}}`
			add("games_call_tool", args, tc.first)
			readAck := func() {
				add("games_get_attention", `{"gameId":"game"}`, tc.read)
				if tc.ack != "" {
					add("games_ack_attention", `{"gameId":"game","attentionId":"current"}`, tc.ack)
				}
			}
			if tc.read != "" {
				readAck()
			}
			if tc.second != "" {
				add("games_call_tool", args, tc.second)
				if tc.second == blocked {
					readAck()
				}
			}
			replay, err := bridge.NewReplay(rows)
			if err != nil {
				t.Fatal(err)
			}
			defer replay.Close()
			client, err := replay.Open(context.Background(), time.Minute)
			if err != nil {
				t.Fatal(err)
			}
			defer client.Close()
			h := NewHarness(client, t.TempDir())
			payload, err := h.Call(context.Background(), "step", tool, nil)
			if (err != nil) != tc.wantErr || errors.Is(err, bridge.ErrRefused) != tc.wantRefused {
				t.Fatalf("unexpected error: %v", err)
			}
			if err == nil && payload["success"] != true {
				t.Fatalf("lost payload: %v", payload)
			}
			requireConsumed(t, replay)
			data, err := os.ReadFile(filepath.Join(h.Output, "0001-step.json"))
			if err != nil {
				t.Fatal(err)
			}
			if tc.first == blocked && !strings.Contains(string(data), "blocked_by_attention") {
				t.Fatalf("lost original refusal: %s", data)
			}
			if tc.read == attention && !strings.Contains(string(data), "genuine simulation fault") {
				t.Fatalf("lost attention: %s", data)
			}
			r := NewReport("attention test", true)
			r["passed"] = !tc.wantErr
			r.Finalize(h.Output)
			data, err = os.ReadFile(filepath.Join(h.Output, "result.json"))
			if err != nil {
				t.Fatal(err)
			}
			if tc.read == attention && (!strings.Contains(string(data), "genuine simulation fault") || !strings.Contains(string(data), "0001-step.json")) {
				t.Fatalf("result.json lost genuine fault evidence: %s", data)
			}
		})
	}
}
