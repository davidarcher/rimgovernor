// Flight recorder: a bounded native timeline. Requests reach durable
// storage before dispatch. Construction is always explicit; there is no hidden
// global recorder. serve opens one under the profile by default, so a
// path may already hold an earlier launch's rows: the sequence continues
// from them and the run id tells the launches apart.
package bridge

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// FlightSchemaVersion is the envelope version every row is written with.
// v2 keeps the v1 field names; what changed is the context contract
// (level, at, tick when known, component, trace_id and span_id on every row)
// and the decision-row payload shape. Readers never gate on the version: a
// v1 row decodes as before, and the kind and the contract in
// docs/developers/contracts/flight-rows.md say which payload shape a row has.
const FlightSchemaVersion = 2

const (
	// DefaultFlightSegmentBytes and DefaultFlightSegments are the retention:
	// 32 MiB x 16 files (the active one included) = 512 MiB, pruned by count
	// only.
	DefaultFlightSegmentBytes = 32 << 20
	DefaultFlightSegments     = 16

	// DefaultFlightPayloadBytes caps one row's payload. A larger payload is
	// replaced by a flightPreviewBytes preview and its hash: the cap bounds
	// what one reply can take from the ring, it is not a copy of the reply.
	// 64 KiB keeps the replies the readers decode (a pawn list is ~15 KiB)
	// and truncates a whole-map cell read, which was 100-260 KB of base64.
	DefaultFlightPayloadBytes = 64 << 10
	flightPreviewBytes        = 4 << 10

	minSegmentBytes = 1024
	minSegments     = 2
	minPayloadBytes = 128
)

// FlightRecorder appends one JSON line per event to path, rotating into numbered
// segments (path.1 is the newest rotated segment) once the active segment
// reaches SegmentBytes, and retaining at most Segments total files. Every
// exported method is safe for concurrent use.
type FlightRecorder struct {
	path         string
	segmentBytes int64
	segments     int
	payloadBytes int
	run          string

	mu       sync.Mutex
	file     *os.File
	size     int64
	sequence uint64
	records  uint64
	truncate uint64
	rotate   uint64
	durable  uint64
	elapsed  time.Duration
}

// FlightRecorderOption configures a FlightRecorder at construction. Defaults:
// 32 MiB segments, 16 retained segments (512 MiB, pruned by count only),
// 64 KiB payloads.
type FlightRecorderOption func(*FlightRecorder)

func FlightSegmentBytes(n int64) FlightRecorderOption {
	return func(r *FlightRecorder) { r.segmentBytes = n }
}
func FlightSegments(n int) FlightRecorderOption { return func(r *FlightRecorder) { r.segments = n } }
func FlightPayloadBytes(n int) FlightRecorderOption {
	return func(r *FlightRecorder) { r.payloadBytes = n }
}

// FlightRunID overrides the recorder's run identifier, otherwise a random hex value.
func FlightRunID(id string) FlightRecorderOption { return func(r *FlightRecorder) { r.run = id } }

// New opens (or creates) path for durable append and records one "coverage"
// event describing what the timeline does and does not capture.
func NewFlightRecorder(path string, opts ...FlightRecorderOption) (*FlightRecorder, error) {
	if path == "" {
		return nil, errors.New("flightrecorder: path required")
	}
	r := &FlightRecorder{path: path, segmentBytes: DefaultFlightSegmentBytes, segments: DefaultFlightSegments, payloadBytes: DefaultFlightPayloadBytes}
	for _, opt := range opts {
		opt(r)
	}
	if r.segmentBytes < minSegmentBytes || r.segments < minSegments || r.payloadBytes < minPayloadBytes {
		return nil, errors.New("flightrecorder: recorder bounds are too small")
	}
	if r.run == "" {
		var buf [16]byte
		if _, err := rand.Read(buf[:]); err != nil {
			return nil, fmt.Errorf("flightrecorder: run id: %w", err)
		}
		r.run = hex.EncodeToString(buf[:])
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return nil, fmt.Errorf("flightrecorder: %w", err)
	}
	last, err := lastFlightSequence(path, r.segmentPath(1))
	if err != nil {
		return nil, err
	}
	r.sequence = last
	if _, err := r.Event("coverage", nil, true, map[string]any{
		"coverage": "All bridge.Client native calls (one row each, plus an in-flight marker for a slow one) and exceptions, including background reads." +
			"Records reach the OS on each write (a process crash loses none); fsync happens on rotation, close and this row. " +
			"A machine crash can lose the unsynced tail. No in-game per-frame/pawn transition trace.",
	}); err != nil {
		return nil, err
	}
	return r, nil
}

// Event appends one record. durable forces an fsync before returning, so a
// caller can be certain the record survives a crash; the natural use is a
// durable "request" row bracketing a non-durable "response" row that becomes
// durable only at the next durable record or segment rotation. context and
// payload are marshaled as JSON objects; a nil map encodes as {}. Every
// row carries a trace_id: the one its context names (a scheduler step, a
// Worker dispatch), else a fresh single-row trace, so `rimgovernor trace`
// can address any row and a consumer never has to special-case the rows
// written outside a traced unit of work.
func (r *FlightRecorder) Event(kind string, context map[string]any, durable bool, payload map[string]any) (uint64, error) {
	if r == nil {
		return 0, errors.New("flightrecorder: recorder required")
	}
	began := time.Now()
	r.mu.Lock()
	defer r.mu.Unlock()
	if payload == nil {
		payload = map[string]any{}
	}
	encoded, err := json.Marshal(payload)
	if err != nil {
		return 0, fmt.Errorf("flightrecorder: encode payload: %w", err)
	}
	if len(encoded) > r.payloadBytes {
		sum := sha256.Sum256(encoded)
		correlated := map[string]any{
			"truncated":      true,
			"original_bytes": len(encoded),
			"sha256":         hex.EncodeToString(sum[:]),
			"preview":        string(encoded[:min(r.payloadBytes, flightPreviewBytes)]),
		}
		for _, key := range []string{"request", "tool", "native_tool", "category", "timing", telemetry.VerdictKey, telemetry.ReasonKey, telemetry.TargetKey, telemetry.DurMsKey} {
			if value, ok := payload[key]; ok && flightCorrelatable(value) {
				correlated[key] = value
			}
		}
		r.truncate++
		encoded, err = json.Marshal(correlated)
		if err != nil {
			return 0, fmt.Errorf("flightrecorder: encode truncated payload: %w", err)
		}
	}
	r.sequence++
	sequence := r.sequence
	context = stampFlightContext(context, began)
	row := struct {
		Version  int             `json:"version"`
		Run      string          `json:"run"`
		Sequence uint64          `json:"sequence"`
		WallTime float64         `json:"wall_time"`
		Kind     string          `json:"kind"`
		Context  map[string]any  `json:"context"`
		Payload  json.RawMessage `json:"payload"`
	}{FlightSchemaVersion, r.run, sequence, float64(began.UnixNano()) / 1e9, kind, context, encoded}
	line, err := json.Marshal(row)
	if err != nil {
		return 0, fmt.Errorf("flightrecorder: encode record: %w", err)
	}
	line = append(line, '\n')
	if err = r.ensureOpen(); err != nil {
		return 0, err
	}
	if r.size >= r.segmentBytes {
		if err = r.rotateLocked(); err != nil {
			return 0, err
		}
	}
	n, err := r.file.Write(line)
	if err != nil {
		return 0, fmt.Errorf("flightrecorder: write: %w", err)
	}
	r.size += int64(n)
	if durable {
		if err = r.file.Sync(); err != nil {
			return 0, fmt.Errorf("flightrecorder: fsync: %w", err)
		}
		r.durable++
	}
	r.records++
	r.elapsed += time.Since(began)
	return sequence, nil
}

// defaultFlightComponent names the writer of a row whose context names none:
// the recorder's own bridge layer. Producers that know better pass component.
const defaultFlightComponent = "bridge"

// stampFlightContext returns context with the v2 envelope guarantees filled
// in: level (Info), at, tick (when the service has observed one), component
// and the trace ids (a fresh single-row trace when the caller named none).
// Keys the caller set win. The caller's map is never modified.
func stampFlightContext(context map[string]any, now time.Time) map[string]any {
	stamped := make(map[string]any, len(context)+6)
	for k, v := range context {
		stamped[k] = v
	}
	if _, ok := stamped["level"]; !ok {
		stamped["level"] = "INFO"
	}
	if _, ok := stamped["at"]; !ok {
		stamped["at"] = now.UTC().Format(time.RFC3339Nano)
	}
	if _, ok := stamped["tick"]; !ok {
		if tick, known := telemetry.Tick(); known {
			stamped["tick"] = tick
		}
	}
	if _, ok := stamped[telemetry.ComponentKey]; !ok {
		stamped[telemetry.ComponentKey] = defaultFlightComponent
	}
	if _, traced := stamped[telemetry.TraceIDKey]; !traced {
		telemetry.NewTrace().Stamp(stamped)
	}
	return stamped
}

// flightCorrelatable admits the small scalar keys (and one flat map of them,
// the call's timing phases) that survive payload truncation.
func flightCorrelatable(value any) bool {
	switch v := value.(type) {
	case string:
		return len(v) <= 256
	case int, int32, int64, uint, uint32, uint64, float64:
		return true
	case map[string]any:
		if len(v) > 16 {
			return false
		}
		for _, inner := range v {
			if _, nested := inner.(map[string]any); nested || !flightCorrelatable(inner) {
				return false
			}
		}
		return true
	default:
		return false
	}
}

func (r *FlightRecorder) ensureOpen() error {
	if r.file != nil {
		return nil
	}
	file, err := os.OpenFile(r.path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return fmt.Errorf("flightrecorder: open: %w", err)
	}
	info, err := file.Stat()
	if err != nil {
		file.Close()
		return fmt.Errorf("flightrecorder: stat: %w", err)
	}
	r.file = file
	r.size = info.Size()
	return nil
}

// rotateLocked finishes the active segment durably, shifts retained segments
// up by one index (dropping the oldest), and starts a fresh empty segment.
func (r *FlightRecorder) rotateLocked() error {
	if err := r.file.Sync(); err != nil {
		return fmt.Errorf("flightrecorder: rotate fsync: %w", err)
	}
	if err := r.file.Close(); err != nil {
		return fmt.Errorf("flightrecorder: rotate close: %w", err)
	}
	r.file = nil
	oldest := r.segmentPath(r.segments - 1)
	_ = os.Remove(oldest)
	for index := r.segments - 2; index >= 1; index-- {
		source := r.segmentPath(index)
		if _, err := os.Stat(source); err == nil {
			if err = os.Rename(source, r.segmentPath(index+1)); err != nil {
				return fmt.Errorf("flightrecorder: rotate shift: %w", err)
			}
		}
	}
	if err := os.Rename(r.path, r.segmentPath(1)); err != nil {
		return fmt.Errorf("flightrecorder: rotate archive: %w", err)
	}
	r.rotate++
	r.size = 0
	return r.ensureOpen()
}

func (r *FlightRecorder) segmentPath(index int) string { return fmt.Sprintf("%s.%d", r.path, index) }

// lastFlightSequence is the sequence of the last well-formed row already
// under the active path, else under its newest rotated segment (a crash
// between rotation and the next write leaves the active file absent), else
// 0. A new recorder continues from it so ReadTimeline sees one unbroken
// sequence across launches and a consumer paging by sequence never rewinds.
func lastFlightSequence(paths ...string) (uint64, error) {
	for _, path := range paths {
		lines, err := readFlightLines(path)
		if errors.Is(err, os.ErrNotExist) {
			continue
		}
		if err != nil {
			return 0, err
		}
		for i := len(lines) - 1; i >= 0; i-- {
			var row struct {
				Sequence *uint64 `json:"sequence"`
			}
			if json.Unmarshal([]byte(lines[i]), &row) == nil && row.Sequence != nil {
				return *row.Sequence, nil
			}
		}
		if len(lines) > 0 {
			return 0, nil
		}
	}
	return 0, nil
}

// Close finishes the current segment durably. Later events reopen it.
func (r *FlightRecorder) Close() error {
	if r == nil {
		return nil
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.file == nil {
		return nil
	}
	syncErr := r.file.Sync()
	closeErr := r.file.Close()
	r.file = nil
	if syncErr != nil {
		return syncErr
	}
	return closeErr
}

// FlightRecorderStats reports counters useful for diagnostics; it never fails.
type FlightRecorderStats struct {
	Records           uint64
	Truncated         uint64
	Rotations         uint64
	DurableRecords    uint64
	RecordingSeconds  float64
	RetentionSegments int
	SegmentBytes      int64
	PayloadBytes      int
}

func (r *FlightRecorder) FlightRecorderStats() FlightRecorderStats {
	r.mu.Lock()
	defer r.mu.Unlock()
	return FlightRecorderStats{
		Records: r.records, Truncated: r.truncate, Rotations: r.rotate, DurableRecords: r.durable,
		RecordingSeconds: r.elapsed.Seconds(), RetentionSegments: r.segments,
		SegmentBytes: r.segmentBytes, PayloadBytes: r.payloadBytes,
	}
}
