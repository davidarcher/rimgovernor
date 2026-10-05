package nativeaccept

import (
	"bufio"
	"bytes"
	"errors"
	"io"
	"os"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// FlightRow is one flight-recorder row a FlightTail read: the service's
// own journal of what it did (scheduler_step, worker_outcome, native_*
// rows; see package telemetry). Tick is the game tick the row was stamped
// with, when the service knew one.
type FlightRow struct {
	Sequence uint64
	Kind     string
	Tick     int64
	HasTick  bool
	Context  map[string]any
	Payload  map[string]any
}

// FlightTail follows a service's flight recorder from where it left off,
// so a harness can wake on what the service just did instead of polling
// its state on a timer (#267). Each Next opens the active segment, reads
// the complete lines appended since the previous call and closes it
// again: the file is never held open, so the recorder's rotation (a
// close-and-rename) is not blocked on Windows. A rotation between two
// reads is detected as the file shrinking; the rows that landed in the
// rotated segment are skipped, which a caller polling anyway tolerates.
type FlightTail struct {
	path   string
	offset int64
}

// NewFlightTail starts a tail at the end of path (what the recorder wrote
// before now is history, not a wake), or at the start when the file does
// not exist yet.
func NewFlightTail(path string) *FlightTail {
	t := &FlightTail{path: path}
	if info, err := os.Stat(path); err == nil {
		t.offset = info.Size()
	}
	return t
}

// Next returns the rows appended since the previous call. A missing file
// is not an error (the service has not opened its recorder yet); a
// corrupt line is skipped.
func (t *FlightTail) Next() ([]FlightRow, error) {
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
	var rows []FlightRow
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
		if row, ok := parseFlightRow(line); ok {
			rows = append(rows, row)
		}
	}
	return rows, nil
}

// lastFlightRow returns the last row of the recorder at path that match
// accepts, with its JSON line. tail > 0 reads only that many trailing
// bytes (the first, possibly partial, line is dropped); 0 reads the whole
// segment. False when no row matches or the file cannot be read.
func lastFlightRow(path string, tail int64, match func(FlightRow) bool) (FlightRow, string, bool) {
	file, err := os.Open(path)
	if err != nil {
		return FlightRow{}, "", false
	}
	defer file.Close()
	skipFirst := false
	if info, err := file.Stat(); err == nil && tail > 0 && info.Size() > tail {
		if _, err := file.Seek(info.Size()-tail, io.SeekStart); err != nil {
			return FlightRow{}, "", false
		}
		skipFirst = true
	}
	scanner := bufio.NewScanner(file)
	scanner.Buffer(make([]byte, 0, 64<<10), 64<<20)
	var (
		last  FlightRow
		line  string
		found bool
	)
	for scanner.Scan() {
		if skipFirst {
			skipFirst = false
			continue
		}
		if row, ok := parseFlightRow(scanner.Bytes()); ok && match(row) {
			last, line, found = row, strings.TrimSpace(scanner.Text()), true
		}
	}
	return last, line, found
}

func parseFlightRow(line []byte) (FlightRow, bool) {
	if len(bytes.TrimSpace(line)) == 0 {
		return FlightRow{}, false
	}
	record, ok := bridge.DecodeFlightLine(line)
	if !ok {
		return FlightRow{}, false
	}
	row := FlightRow{Sequence: record.Sequence, Kind: record.Kind, Context: record.Context, Payload: record.Payload}
	if tick, ok := row.Context["tick"].(float64); ok {
		row.Tick, row.HasTick = int64(tick), true
	}
	return row, true
}

// Record is the row as the bridge's timeline reader holds it, for the
// kind helpers in package bridge (flightrows.go).
func (r FlightRow) Record() bridge.TimelineRecord {
	return bridge.TimelineRecord{Kind: r.Kind, Sequence: r.Sequence, HasSeq: true, Context: r.Context, Payload: r.Payload}
}

// Fields is the row's data as one flat map (bridge.RowFields): a decision
// row's attrs with its verdict, reason, target and dur_ms.
func (r FlightRow) Fields() map[string]any { return bridge.RowFields(r.Record()) }

// WorkerOutcome reports whether row is a worker_outcome row (the v2
// dispatch row once #2064 moves it): the service reconciled an action to a
// new outcome (a stage change, a completion, a failure), the moment a
// watch's sample is most likely to have changed.
func WorkerOutcome(row FlightRow) bool { return row.Kind == "worker_outcome" || row.Kind == "dispatch" }
