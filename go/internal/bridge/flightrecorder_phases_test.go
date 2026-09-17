package bridge

import (
	"bytes"
	"context"
	"path/filepath"
	"strings"
	"testing"
	"time"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

// TestPhaseTimingRecordedAndSummarized drives typed reads through a recorded
// client and checks each call leaves gate/call/decode phases on its receipt
// row, a correlated ProtoJSON decode row, and that the sampler attributes
// them to the inner rimgovernor/* tool with wall TPS from reply ticks.
func TestPhaseTimingRecordedAndSummarized(t *testing.T) {
	tick := int64(1000)
	s := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		tick += 600
		time.Sleep(2 * time.Millisecond)
		switch arg.Tool {
		case "rimgovernor/lifecycle_read_identity":
			reply := pbLoaded()
			reply.GetLoaded().Context.Tick = proto.Int64(tick)
			return pbResult(reply), nil
		case "rimgovernor/clock_read_status":
			status := &k.Status{Context: &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(1)},
				State: &k.Status_NeverStarted{NeverStarted: &k.NeverStarted{}}, NativeTickBoundary: proto.Bool(true), DurableEvents: proto.Bool(true),
				NewestCursor: proto.Int64(0), ObservedSpeed: k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum(), ActualPaused: proto.Bool(true)}
			return pbResult(&k.StatusReply{Outcome: &k.StatusReply_Status{Status: status}}), nil
		}
		return &mcp.CallToolResult{IsError: true}, nil
	}}
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	rec, err := NewFlightRecorder(path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { rec.Close() })
	client, err := open(context.Background(), "fixture-game", time.Second, rec, s.factory(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Close() })
	for i := 0; i < 3; i++ {
		if _, _, err = client.Identity(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	// Status validation may reject the minimal fixture; only the recorded
	// receipt matters here, so the typed outcome is ignored.
	_, _, _ = client.ReadClockStatus(context.Background(), pbIdentity())

	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	responses, decodes := 0, 0
	for _, row := range rows {
		switch row.Kind {
		case "native_response":
			responses++
			timing, ok := row.Payload["timing"].(map[string]any)
			if !ok {
				t.Fatalf("response without timing: %+v", row.Payload)
			}
			for _, key := range []string{"gate_wait_ms", "call_ms", "decode_ms", "total_ms", "response_bytes"} {
				if _, ok := number(timing[key]); !ok {
					t.Fatalf("timing missing %s: %+v", key, timing)
				}
			}
			if field(timing, "total_ms") < field(timing, "call_ms") {
				t.Fatalf("total below call: %+v", timing)
			}
			if row.Payload["tool"] == "games_call_tool" && !strings.HasPrefix(row.Payload["native_tool"].(string), "rimgovernor/") {
				t.Fatalf("native tool not attributed: %+v", row.Payload)
			}
			if row.WallTime == 0 {
				t.Fatal("wall time not surfaced on timeline row")
			}
		case "native_decode":
			decodes++
			if _, ok := number(row.Payload["request"]); !ok || row.Payload["ok"] != true {
				t.Fatalf("decode row incomplete: %+v", row.Payload)
			}
		}
	}
	// Each typed call is one describe plus one call receipt.
	if responses != 8 || decodes != 4 {
		t.Fatalf("responses %d decodes %d", responses, decodes)
	}

	summary := SummarizePhases(rows)
	var identityCall, identityDetail *ToolPhases
	for i := range summary.Tools {
		tool := &summary.Tools[i]
		if tool.NativeTool == "rimgovernor/lifecycle_read_identity" {
			switch tool.Wrapper {
			case "games_call_tool":
				identityCall = tool
			case "games_tool_detail":
				identityDetail = tool
			}
		}
	}
	if identityCall == nil || identityDetail == nil {
		t.Fatalf("identity phases missing: %+v", summary.Tools)
	}
	if identityCall.Calls != 3 || identityDetail.Calls != 3 || identityCall.Errors != 0 {
		t.Fatalf("identity counts: %+v %+v", identityCall, identityDetail)
	}
	// Sub-millisecond phases (decode, gate wait) can legitimately read 0 on
	// Windows' coarse monotonic clock, so only the slept call phase is bounded.
	if identityCall.CallMs < 3*2 || identityCall.ResponseBytes == 0 {
		t.Fatalf("identity phases not accumulated: %+v", identityCall)
	}
	if identityDetail.ProtoDecodeMs != 0 {
		t.Fatalf("describe round trip must not carry proto decode: %+v", identityDetail)
	}
	if summary.Untimed != 0 || summary.Gaps != 0 {
		t.Fatalf("unexpected untimed/gaps: %+v", summary)
	}
	clock := summary.Clock
	if clock.TickSamples != 4 || clock.TicksAdvanced != 3*600 || clock.WallSecs <= 0 || clock.WallTPS <= 0 || clock.Resets != 0 {
		t.Fatalf("clock sample: %+v", clock)
	}
	if clock.ClockSamples != 1 || clock.PausedSamples != 1 {
		t.Fatalf("paused sample: %+v", clock)
	}

	var report bytes.Buffer
	WritePhaseReport(&report, summary)
	if !strings.Contains(report.String(), "rimgovernor/lifecycle_read_identity") || !strings.Contains(report.String(), "wall TPS") {
		t.Fatalf("report: %s", report.String())
	}
}

func TestSummarizePhasesHandlesLegacyRowsGapsAndResets(t *testing.T) {
	rows := []TimelineRecord{
		{Kind: "coverage", WallTime: 10, Payload: map[string]any{}},
		{Kind: "recording_gap", Reason: "Retention or sequence discontinuity"},
		// Pre-timing row: counted, no phases.
		{Kind: "native_response", WallTime: 11, Payload: map[string]any{"request": 1.0, "tool": "games_call_tool", "result": map[string]any{"payload": `{"loaded":{"context":{"tick":"500"}}}`}}},
		{Kind: "native_response", WallTime: 12, Payload: map[string]any{"request": 2.0, "tool": "games_call_tool", "native_tool": "x/read", "timing": map[string]any{"gate_wait_ms": 1.0, "call_ms": 5.0, "decode_ms": 0.5, "total_ms": 7.0, "response_bytes": 100.0}, "result": map[string]any{"payload": `{"loaded":{"context":{"tick":"1100"}}}`}}},
		// Tick went backwards: a load or rewind, not negative progress.
		{Kind: "native_response", WallTime: 13, Payload: map[string]any{"request": 3.0, "tool": "games_call_tool", "native_tool": "x/read", "timing": map[string]any{"gate_wait_ms": 0.0, "call_ms": 3.0, "decode_ms": 0.5, "total_ms": 4.0, "response_bytes": 50.0}, "result": map[string]any{"payload": `{"loaded":{"context":{"tick":"200"}}}`}}},
		{Kind: "native_decode", WallTime: 13, Payload: map[string]any{"request": 3.0, "native_tool": "x/read", "proto_decode_ms": 0.25, "ok": true}},
		{Kind: "native_decode", WallTime: 13, Payload: map[string]any{"request": 99.0, "native_tool": "orphan", "proto_decode_ms": 9.0, "ok": true}},
		{Kind: "native_error", WallTime: 14, Payload: map[string]any{"request": 4.0, "tool": "games_start", "native_tool": "games_start", "error": "boom", "timing": map[string]any{"gate_wait_ms": 0.0, "call_ms": 2.0, "decode_ms": 0.0, "total_ms": 2.0, "response_bytes": 0.0}}},
	}
	summary := SummarizePhases(rows)
	if summary.Records != 8 || summary.Gaps != 1 || summary.Untimed != 1 || summary.WallSecs != 4 {
		t.Fatalf("summary: %+v", summary)
	}
	if len(summary.Tools) != 3 || summary.Tools[0].NativeTool != "x/read" {
		t.Fatalf("tools: %+v", summary.Tools)
	}
	read := summary.Tools[0]
	if read.Calls != 2 || read.TotalMs != 11 || read.CallMs != 8 || read.ProtoDecodeMs != 0.25 || read.ResponseBytes != 150 {
		t.Fatalf("read phases: %+v", read)
	}
	clock := summary.Clock
	if clock.TickSamples != 3 || clock.TicksAdvanced != 600 || clock.WallSecs != 1 || clock.WallTPS != 600 || clock.Resets != 1 {
		t.Fatalf("clock: %+v", clock)
	}
	for _, tool := range summary.Tools {
		if tool.Wrapper == "games_start" && tool.Errors != 1 {
			t.Fatalf("error not counted: %+v", tool)
		}
	}
}
