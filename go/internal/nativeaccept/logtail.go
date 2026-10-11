package nativeaccept

import (
	"bufio"
	"bytes"
	"errors"
	"io"
	"os"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/otel"
)

// LogRow is one row of a service's own journal of what it did (planner_step,
// dispatch, native_* rows; see package telemetry), read from its OTLP log ring
// (<recorder dir>/otlp/logs.jsonl, rebuilt by bridge.RecordFromLog). Tick is
// the game tick the row was stamped with, when the service knew one. Sequence
// numbers rows in the order this reader met them.
type LogRow struct {
	Sequence uint64
	Kind     string
	Tick     int64
	HasTick  bool
	Context  map[string]any
	Payload  map[string]any
}

// LogTail follows a service's OTLP log ring from where it left off, so a
// harness can wake on what the service just did instead of polling its state
// on a timer. Each Next opens the active segment (logs.jsonl), reads the
// complete lines appended since the previous call and closes it again: the file
// is never held open, so the sink's rotation (a close-and-rename) is not blocked
// on Windows. A rotation between two reads is detected as the file shrinking;
// the rows that landed in the rotated segment are skipped, which a caller
// polling anyway tolerates. The sink batches for up to 250 ms, so a row is
// visible that much after the service logged it.
type LogTail struct {
	path   string
	offset int64
	seq    uint64
}

// NewLogTail starts a tail at the end of path (what the service wrote
// before now is history, not a wake), or at the start when the file does
// not exist yet.
func NewLogTail(path string) *LogTail {
	t := &LogTail{path: path}
	if info, err := os.Stat(path); err == nil {
		t.offset = info.Size()
	}
	return t
}

// NewLogTailFromStart starts a tail at the first row of path: it
// returns every row the active file holds on its first Next.
func NewLogTailFromStart(path string) *LogTail { return &LogTail{path: path} }

// Next returns the rows appended since the previous call. A missing file
// is not an error (the service has not opened its sink yet); a
// corrupt line is skipped.
func (t *LogTail) Next() ([]LogRow, error) {
	file, err := os.Open(t.path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil, nil
		}
		return nil, err
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		return nil, err
	}
	if info.Size() < t.offset {
		t.offset = 0
	}
	if info.Size() == t.offset {
		return nil, nil
	}
	if _, err := file.Seek(t.offset, io.SeekStart); err != nil {
		return nil, err
	}
	reader := bufio.NewReaderSize(file, 64<<10)
	var rows []LogRow
	for {
		line, err := reader.ReadBytes('\n')
		if err != nil {
			// A partial trailing line is a write in progress; it is read
			// whole next time.
			if !errors.Is(err, io.EOF) {
				return rows, err
			}
			break
		}
		t.offset += int64(len(line))
		if row, ok := ParseLogRow(line); ok {
			t.seq++
			row.Sequence = t.seq
			rows = append(rows, row)
		}
	}
	return rows, nil
}

// lastLogRow returns the last row of the log ring file at path that match
// accepts, with its JSON line. tail > 0 reads only that many trailing
// bytes (the first, possibly partial, line is dropped); 0 reads the whole
// segment. False when no row matches or the file cannot be read.
func lastLogRow(path string, tail int64, match func(LogRow) bool) (LogRow, string, bool) {
	file, err := os.Open(path)
	if err != nil {
		return LogRow{}, "", false
	}
	defer file.Close()
	skipFirst := false
	if info, err := file.Stat(); err == nil && tail > 0 && info.Size() > tail {
		if _, err := file.Seek(info.Size()-tail, io.SeekStart); err != nil {
			return LogRow{}, "", false
		}
		skipFirst = true
	}
	scanner := bufio.NewScanner(file)
	scanner.Buffer(make([]byte, 0, 64<<10), 64<<20)
	var (
		last  LogRow
		line  string
		found bool
	)
	for scanner.Scan() {
		if skipFirst {
			skipFirst = false
			continue
		}
		if row, ok := ParseLogRow(scanner.Bytes()); ok && match(row) {
			last, line, found = row, strings.TrimSpace(scanner.Text()), true
		}
	}
	return last, line, found
}

// ParseLogRow rebuilds the row of one logs.jsonl line (false for a line that is no row).
func ParseLogRow(line []byte) (LogRow, bool) {
	if len(bytes.TrimSpace(line)) == 0 {
		return LogRow{}, false
	}
	log, ok := otel.DecodeLog(line)
	if !ok {
		return LogRow{}, false
	}
	record, ok := bridge.RecordFromLog(log)
	if !ok {
		return LogRow{}, false
	}
	row := LogRow{Kind: record.Kind, Context: record.Context, Payload: record.Payload}
	if tick, ok := row.Context["tick"].(float64); ok {
		row.Tick, row.HasTick = int64(tick), true
	}
	return row, true
}

// Record is the row as the bridge's timeline reader holds it, for the
// kind helpers in package bridge (rows.go).
func (r LogRow) Record() bridge.TimelineRecord {
	return bridge.TimelineRecord{Kind: r.Kind, Sequence: r.Sequence, HasSeq: true, Context: r.Context, Payload: r.Payload}
}

// Fields is the row's data as one flat map (bridge.RowFields): a decision
// row's attrs with its verdict, reason, target and dur_ms.
func (r LogRow) Fields() map[string]any { return bridge.RowFields(r.Record()) }

// WorkerOutcome reports whether row is a dispatch row that recorded an
// outcome change (attr changed): the service reconciled an action to a new
// outcome (a stage change, a completion, a failure), the moment a watch's
// sample is most likely to have changed.
func WorkerOutcome(row LogRow) bool {
	changed, _ := row.Fields()["changed"].(bool)
	return row.Kind == "dispatch" && changed
}
