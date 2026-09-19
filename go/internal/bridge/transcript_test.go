package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// A session recorded against the in-memory GABS replays call for call: the
// same receipts, the same refusals, the same int64 observations, and a
// call the recording never saw fails with the recorded row's diff.
func TestTranscriptRecordsAndReplays(t *testing.T) {
	path := filepath.Join(t.TempDir(), "transcript.jsonl")
	transcript, err := OpenTranscript(path)
	if err != nil {
		t.Fatal(err)
	}
	s := &testServer{handler: func(_ context.Context, args nativeArgument) (*mcp.CallToolResult, error) {
		switch args.Tool {
		case "fixture/read":
			return structured(`{"tick":9007199254740993,"success":true}`), nil
		case "fixture/refused":
			return &mcp.CallToolResult{IsError: true, Content: []mcp.Content{&mcp.TextContent{Text: "Tool 'fixture/refused' not found"}}, StructuredContent: json.RawMessage(`{"requested":"fixture/refused"}`)}, nil
		}
		return nil, errors.New("boom")
	}}
	client, err := open(context.Background(), "fixture-game", time.Second, nil, transcript, s.factory(t))
	if err != nil {
		t.Fatal(err)
	}
	ctx := WithTranscriptPhase(context.Background(), "read-tick")
	live, err := client.NativeCall(ctx, "fixture/read", json.RawMessage(`{"a": 1}`))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := client.NativeCall(context.Background(), "fixture/refused", nil); !errors.Is(err, ErrRefused) {
		t.Fatalf("refusal: %v", err)
	}
	if _, err := client.NativeCall(context.Background(), "fixture/boom", nil); !errors.Is(err, ErrTransport) {
		t.Fatalf("transport error: %v", err)
	}
	if _, err := client.NativeNames(context.Background(), "", "fixture"); err != nil {
		t.Fatal(err)
	}
	_ = client.Close()
	if err := transcript.Close(); err != nil {
		t.Fatal(err)
	}

	rows, err := ReadTranscript(path)
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 5 || rows[0].Kind != "session" || rows[0].GameID != "fixture-game" || len(rows[0].Tools) == 0 {
		t.Fatalf("rows: %+v", rows)
	}
	if rows[1].Phase != "read-tick" || rows[1].NativeTool != "fixture/read" || rows[1].Tool != "games_call_tool" || len(rows[1].Result) == 0 {
		t.Fatalf("call row: %+v", rows[1])
	}
	if rows[2].Phase != "" || len(rows[2].Result) == 0 || rows[2].Error != "" {
		t.Fatalf("refusal row keeps its receipt: %+v", rows[2])
	}
	if rows[3].Error == "" || len(rows[3].Result) != 0 {
		t.Fatalf("transport failure row: %+v", rows[3])
	}
	if rows[4].Tool != "games_tool_names" {
		t.Fatalf("names row: %+v", rows[4])
	}

	replay, err := NewReplay(rows)
	if err != nil {
		t.Fatal(err)
	}
	defer replay.Close()
	played, err := replay.Open(context.Background(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer played.Close()
	// Different spacing, same arguments.
	replayed, err := played.NativeCall(context.Background(), "fixture/read", json.RawMessage(`{"a":1}`))
	if err != nil {
		t.Fatal(err)
	}
	if string(replayed.Structured) != string(live.Structured) {
		t.Fatalf("structured content changed in replay:\n%s\n%s", live.Structured, replayed.Structured)
	}
	if !strings.Contains(string(replayed.Structured), "9007199254740993") {
		t.Fatalf("int64 observation rounded: %s", replayed.Structured)
	}
	if _, err := played.NativeCall(context.Background(), "fixture/refused", nil); !errors.Is(err, ErrRefused) {
		t.Fatalf("replayed refusal: %v", err)
	}
	if _, err := played.NativeCall(context.Background(), "fixture/boom", nil); !errors.Is(err, ErrTransport) {
		t.Fatalf("replayed transport error: %v", err)
	}
	if replay.Err() != nil {
		t.Fatal(replay.Err())
	}
	if replay.Consumed() != 3 || replay.Remaining() != 1 {
		t.Fatalf("consumed %d remaining %d", replay.Consumed(), replay.Remaining())
	}

	// A call the recording never saw at this point.
	if _, err := played.NativeCall(context.Background(), "fixture/other", json.RawMessage(`{"b":2}`)); err == nil {
		t.Fatal("unrecorded call succeeded")
	}
	var mismatch *ReplayMismatch
	if !errors.As(replay.Err(), &mismatch) || mismatch.Sequence != rows[4].Sequence {
		t.Fatalf("mismatch: %v", replay.Err())
	}
	text := replay.Err().Error()
	for _, want := range []string{"row 5", "recorded games_tool_names", "got games_call_tool fixture/other", `- tool: games_tool_names`, `+ tool: games_call_tool`} {
		if !strings.Contains(text, want) {
			t.Fatalf("mismatch text lacks %q:\n%s", want, text)
		}
	}
	// The replay stays failed.
	if _, err := played.NativeNames(context.Background(), "", "fixture"); err == nil {
		t.Fatal("call after a mismatch succeeded")
	}
}

func TestReplayExhaustedTranscript(t *testing.T) {
	rows := []TranscriptRow{{Kind: "session", GameID: "g", Tools: []string{"games_call_tool", "games_tool_detail"}}}
	replay, err := NewReplay(rows)
	if err != nil {
		t.Fatal(err)
	}
	defer replay.Close()
	client, err := replay.Open(context.Background(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	if _, err := client.NativeCall(context.Background(), "fixture/read", nil); err == nil {
		t.Fatal("call past the transcript succeeded")
	}
	if err := replay.Err(); err == nil || !strings.Contains(err.Error(), "exhausted") {
		t.Fatalf("exhausted: %v", err)
	}
	if _, err := NewReplay(nil); err == nil {
		t.Fatal("a transcript without a session row was accepted")
	}
}

func TestLineDiff(t *testing.T) {
	got := lineDiff("a\nb\nc", "a\nx\nc")
	if got != "  a\n- b\n+ x\n  c" {
		t.Fatalf("diff:\n%s", got)
	}
	if lineDiff("same", "same") != "" {
		t.Fatal("equal inputs differ")
	}
}
