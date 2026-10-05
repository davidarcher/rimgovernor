package bridge

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/gabp"
	"github.com/davidarcher/RimGovernor/go/internal/gabp/gabptest"
)

type modLogRow struct {
	Kind    string         `json:"kind"`
	Context map[string]any `json:"context"`
	Payload map[string]any `json:"payload"`
}

func readModLogRows(t *testing.T, path string) []modLogRow {
	t.Helper()
	lines, err := readFlightLines(path)
	if err != nil {
		return nil
	}
	var rows []modLogRow
	for _, line := range lines {
		var row modLogRow
		if json.Unmarshal([]byte(line), &row) == nil && (row.Kind == "mod_log" || row.Kind == "log_overflow") {
			rows = append(rows, row)
		}
	}
	return rows
}

func modLogEventFrame(seq int, fields map[string]any) gabp.Message {
	fields["seq"] = seq
	if _, ok := fields["type"]; !ok {
		fields["type"] = "log"
	}
	return gabp.Message{V: gabp.Version, Type: gabp.TypeEvent, Channel: modLogChannel, Seq: seq, Payload: encode(fields)}
}

// The mod replays its ring on subscribe (late=true), then pushes live; the
// controller records each as a mod_log row in order with the mod's tick and
// trace, and the mod's overflow summary as one log_overflow row.
func TestModLogEventsBecomeFlightRows(t *testing.T) {
	game := startFakeGame(t)
	game.onModLogSubscribe = func(conn *gabptest.ServerConn) {
		conn.Send(modLogEventFrame(1, map[string]any{"type": "overflow", "dropped": 7, "tick": 40}))
		conn.Send(modLogEventFrame(2, map[string]any{"level": "warn", "component": "watchdog", "tick": 41, "trace": "aaaa/bbbb", "msg": "queued", "late": true, "at_unix_ms": 1700000000000}))
		conn.Send(modLogEventFrame(3, map[string]any{"level": "info", "component": "probe", "tick": 42, "msg": "live", "suppressed": 4}))
	}
	path := filepath.Join(t.TempDir(), "flight.jsonl")
	recorder, err := NewFlightRecorder(path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = recorder.Close() })
	client, err := Open(context.Background(), ProcessConfig{GameID: "fixture", Launch: game.spec, Timeout: 20 * time.Second, Recorder: recorder})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Close() })
	started, err := client.GamesStart(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := client.ConnectWithPoll(context.Background(), started); err != nil {
		t.Fatal(err)
	}
	var rows []modLogRow
	for deadline := time.Now().Add(20 * time.Second); len(rows) < 3 && time.Now().Before(deadline); time.Sleep(10 * time.Millisecond) {
		rows = readModLogRows(t, path)
	}
	if len(rows) != 3 {
		t.Fatalf("rows: %+v", rows)
	}
	if rows[0].Kind != "log_overflow" || rows[0].Payload["dropped"] != float64(7) || rows[0].Payload["side"] != "mod" {
		t.Fatalf("overflow row: %+v", rows[0])
	}
	late := rows[1]
	if late.Kind != "mod_log" || late.Payload["seq"] != float64(2) || late.Payload["late"] != true || late.Payload["source"] != "watchdog" || late.Payload["msg"] != "queued" {
		t.Fatalf("late row: %+v", late)
	}
	if late.Context["tick"] != float64(41) || late.Context["trace_id"] != "aaaa" || late.Context["span_id"] != "bbbb" || late.Context["level"] != "WARN" || late.Context["component"] != "mod" {
		t.Fatalf("late context: %+v", late.Context)
	}
	if late.Context["at"] != "2023-11-14T22:13:20Z" {
		t.Fatalf("late row keeps the mod's time: %v", late.Context["at"])
	}
	live := rows[2]
	if live.Payload["seq"] != float64(3) || live.Payload["late"] != nil || live.Payload["suppressed"] != float64(4) || live.Context["tick"] != float64(42) {
		t.Fatalf("live row: %+v", live)
	}
}

// offer never blocks the GABP reader: a burst past the queue is dropped and
// counted in one log_overflow row, and the rest is written in order.
func TestModLogOfferNeverBlocksAndCountsOverflow(t *testing.T) {
	path := filepath.Join(t.TempDir(), "flight.jsonl")
	recorder, err := NewFlightRecorder(path)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = recorder.Close() })
	sink := newModLogSink(recorder)
	total := modLogQueueDepth + 10
	for i := 1; i <= total; i++ { // no writer is running: a blocking offer would hang here
		sink.offer(gabp.Event{Channel: modLogChannel, Payload: encode(map[string]any{"type": "log", "seq": i, "level": "info", "msg": "m"})})
	}
	sink.offer(gabp.Event{Channel: "attention/opened", Payload: encode(map[string]any{"seq": 0})})
	done := make(chan struct{})
	close(done)
	sink.run(done)
	rows := readModLogRows(t, path)
	if len(rows) != modLogQueueDepth+1 {
		t.Fatalf("rows %d", len(rows))
	}
	for i := 0; i < modLogQueueDepth; i++ {
		if rows[i].Kind != "mod_log" || rows[i].Payload["seq"] != float64(i+1) {
			t.Fatalf("row %d out of order: %+v", i, rows[i])
		}
	}
	last := rows[len(rows)-1]
	if last.Kind != "log_overflow" || last.Payload["dropped"] != float64(10) || last.Payload["side"] != "controller" {
		t.Fatalf("overflow summary: %+v", last)
	}
}
