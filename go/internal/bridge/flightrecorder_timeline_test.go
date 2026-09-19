package bridge

import (
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

// timelineRecorder writes fixed-size rows into a small ring so a few dozen
// rows rotate it several times.
func timelineRecorder(t *testing.T, path string, segments int) *FlightRecorder {
	t.Helper()
	r, err := NewFlightRecorder(path, FlightSegmentBytes(minSegmentBytes), FlightSegments(segments), FlightPayloadBytes(minPayloadBytes))
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	t.Cleanup(func() { r.Close() })
	return r
}

func writeRows(t *testing.T, r *FlightRecorder, n int) {
	t.Helper()
	for i := 0; i < n; i++ {
		if _, err := r.Event("sample", nil, false, map[string]any{"pad": strings.Repeat("y", 40)}); err != nil {
			t.Fatalf("Event: %v", err)
		}
	}
}

// expectSameAsFresh asserts the reader's view equals a one-shot read of
// the ring.
func expectSameAsFresh(t *testing.T, reader *TimelineReader, path string) []TimelineRecord {
	t.Helper()
	got, err := reader.Read()
	if err != nil {
		t.Fatalf("Read: %v", err)
	}
	want, err := ReadTimeline(path)
	if err != nil {
		t.Fatalf("ReadTimeline: %v", err)
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("reader diverged from a fresh read:\n got %d rows %+v\nwant %d rows %+v", len(got), summarizeRows(got), len(want), summarizeRows(want))
	}
	return got
}

func summarizeRows(rows []TimelineRecord) []string {
	out := make([]string, 0, len(rows))
	for _, row := range rows {
		if row.HasSeq {
			out = append(out, fmt.Sprint(row.Sequence))
		} else {
			out = append(out, fmt.Sprintf("gap(%s %d<-%d %s:%d)", row.Reason, row.Before, row.After, filepath.Base(row.File), row.Line))
		}
	}
	return out
}

// A polled reader decodes each appended byte once: a fixed-size batch of
// new rows costs the batch, not the ring, across rotations that shift
// segments and drop the oldest.
func TestTimelineReaderDecodesNewBytesOnly(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	r := timelineRecorder(t, path, 3)
	writeRows(t, r, 40)
	if r.FlightRecorderStats().Rotations < 2 {
		t.Fatalf("fixture rotated %d times, want 2+", r.FlightRecorderStats().Rotations)
	}
	reader := NewTimelineReader(path)
	expectSameAsFresh(t, reader, path)
	filled := reader.decoded
	if filled == 0 {
		t.Fatal("expected the first read to decode the ring")
	}
	// A poll with nothing new decodes nothing.
	expectSameAsFresh(t, reader, path)
	if reader.decoded != filled {
		t.Fatalf("idle poll decoded %d bytes", reader.decoded-filled)
	}
	rotations := r.FlightRecorderStats().Rotations
	for batch := 0; batch < 12; batch++ {
		before := reader.decoded
		writeRows(t, r, 5)
		rows := expectSameAsFresh(t, reader, path)
		if rows[len(rows)-1].Sequence != r.FlightRecorderStats().Records {
			t.Fatalf("batch %d: last row %d, recorder at %d", batch, rows[len(rows)-1].Sequence, r.FlightRecorderStats().Records)
		}
		// Five rows of ~200 bytes; well under one 1 KiB segment.
		if decoded := reader.decoded - before; decoded > 2*minSegmentBytes {
			t.Fatalf("batch %d decoded %d bytes for five rows", batch, decoded)
		}
	}
	if r.FlightRecorderStats().Rotations == rotations {
		t.Fatal("expected the batches to rotate the ring")
	}
	if len(reader.rotated) != 2 {
		t.Fatalf("expected 2 cached rotated segments, got %d", len(reader.rotated))
	}
}

// The ring's leading gap, a rotation's shift and a corrupt line all read
// the same through the reader and a fresh read, before and after the
// rotation that moves them.
func TestTimelineReaderKeepsGapsAcrossRotation(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	r := timelineRecorder(t, path, 3)
	writeRows(t, r, 40)
	if err := r.Close(); err != nil {
		t.Fatal(err)
	}
	corrupt, err := os.OpenFile(path, os.O_APPEND|os.O_WRONLY, 0o644)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = corrupt.WriteString("{\"sequence\":\n"); err != nil {
		t.Fatal(err)
	}
	corrupt.Close()
	reader := NewTimelineReader(path)
	rows := expectSameAsFresh(t, reader, path)
	var corruptRows, retentionRows int
	for _, row := range rows {
		switch {
		case row.Kind != "recording_gap":
		case row.File != "":
			corruptRows++
		default:
			retentionRows++
		}
	}
	if corruptRows != 1 || retentionRows == 0 {
		t.Fatalf("expected one corrupt-line gap and a retention gap, got %v", summarizeRows(rows))
	}
	for i := 0; i < 4; i++ {
		writeRows(t, r, 6)
		rows = expectSameAsFresh(t, reader, path)
	}
	for _, row := range rows {
		if row.Kind == "recording_gap" && row.File != "" && filepath.Base(row.File) == filepath.Base(path) {
			t.Fatalf("corrupt-line gap still names the active file after rotation: %+v", row)
		}
	}
}

// A row the recorder is mid-write (no newline yet) reads as it would
// fresh, and is decoded again once complete.
func TestTimelineReaderRereadsAPartialRow(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	r := timelineRecorder(t, path, 2)
	writeRows(t, r, 3)
	if err := r.Close(); err != nil {
		t.Fatal(err)
	}
	reader := NewTimelineReader(path)
	expectSameAsFresh(t, reader, path)
	line := "{\"version\":1,\"run\":\"r\",\"sequence\":7,\"wall_time\":1,\"kind\":\"late\",\"context\":{},\"payload\":{}}"
	file, err := os.OpenFile(path, os.O_APPEND|os.O_WRONLY, 0o644)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = file.WriteString(line[:20]); err != nil {
		t.Fatal(err)
	}
	rows := expectSameAsFresh(t, reader, path)
	if last := rows[len(rows)-1]; last.Kind != "recording_gap" || last.Line != 5 {
		t.Fatalf("expected the partial row as a corrupt-line gap on line 5, got %+v", last)
	}
	if _, err = file.WriteString(line[20:] + "\n"); err != nil {
		t.Fatal(err)
	}
	file.Close()
	rows = expectSameAsFresh(t, reader, path)
	if last := rows[len(rows)-1]; last.Kind != "late" || last.Sequence != 7 || rows[len(rows)-2].Kind != "recording_gap" || rows[len(rows)-2].After != 4 {
		t.Fatalf("expected the completed row after a sequence gap, got %v", summarizeRows(rows))
	}
}

// A ring replaced under the reader (a shorter file, or a new launch's file
// of the same length with different leading bytes) is decoded afresh.
func TestTimelineReaderRecoversFromAReplacedRing(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "timeline.jsonl")
	r := timelineRecorder(t, path, 2)
	writeRows(t, r, 8)
	if err := r.Close(); err != nil {
		t.Fatal(err)
	}
	reader := NewTimelineReader(path)
	expectSameAsFresh(t, reader, path)
	for _, name := range []string{path, path + ".1"} {
		if err := os.Remove(name); err != nil && !os.IsNotExist(err) {
			t.Fatal(err)
		}
	}
	if rows, err := reader.Read(); err != nil || rows != nil {
		t.Fatalf("expected no rows from an empty ring, got %v %v", rows, err)
	}
	other := timelineRecorder(t, path, 2)
	writeRows(t, other, 2)
	rows := expectSameAsFresh(t, reader, path)
	if len(rows) == 0 || rows[len(rows)-1].Run != other.run {
		t.Fatalf("expected the new launch's rows, got %v", summarizeRows(rows))
	}
}

// BenchmarkTimelineReaderPoll measures one poll of a filled ring after a
// fixed-size batch of new rows, the dashboard's steady state (#375);
// BenchmarkReadTimelineFilledRing is the one-shot read it replaces.
func BenchmarkTimelineReaderPoll(b *testing.B) {
	path, r := benchmarkRing(b)
	reader := NewTimelineReader(path)
	if _, err := reader.Read(); err != nil {
		b.Fatal(err)
	}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		b.StopTimer()
		benchmarkRows(b, r, 20)
		b.StartTimer()
		if _, err := reader.Read(); err != nil {
			b.Fatal(err)
		}
	}
}

func BenchmarkReadTimelineFilledRing(b *testing.B) {
	path, r := benchmarkRing(b)
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		b.StopTimer()
		benchmarkRows(b, r, 20)
		b.StartTimer()
		if _, err := ReadTimeline(path); err != nil {
			b.Fatal(err)
		}
	}
}

// benchmarkRing fills four 1 MiB segments (a bounded stand-in for the
// eight 8 MiB default) with rows of the recorder's usual shape.
func benchmarkRing(b *testing.B) (string, *FlightRecorder) {
	b.Helper()
	path := filepath.Join(b.TempDir(), "timeline.jsonl")
	r, err := NewFlightRecorder(path, FlightSegmentBytes(1<<20), FlightSegments(4))
	if err != nil {
		b.Fatal(err)
	}
	b.Cleanup(func() { r.Close() })
	for r.FlightRecorderStats().Rotations < 4 {
		benchmarkRows(b, r, 500)
	}
	return path, r
}

func benchmarkRows(b *testing.B, r *FlightRecorder, n int) {
	b.Helper()
	for i := 0; i < n; i++ {
		if _, err := r.Event("native_response", map[string]any{"trace_id": "t", "step": i}, false, map[string]any{
			"request": i, "tool": "games_call_tool", "native_tool": "rimgovernor/colony_facts",
			"timing":  map[string]any{"gate_wait_ms": 0.2, "call_ms": 3.5, "decode_ms": 0.4, "total_ms": 4.1},
			"preview": strings.Repeat("x", 300),
		}); err != nil {
			b.Fatal(err)
		}
	}
}
