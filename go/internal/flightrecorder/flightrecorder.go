// Package flightrecorder is an opt-in bounded native timeline: requests reach
// durable storage before dispatch. It replaces controller/rimgovernor/flight_recorder.py
// for the Go controller. Construction is always explicit; there is no hidden
// global recorder, unlike the Python module's env-var singleton.
package flightrecorder

import (
	"context"
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
)

const (
	minSegmentBytes = 1024
	minSegments     = 2
	minPayloadBytes = 128
)

// Recorder appends one JSON line per event to path, rotating into numbered
// segments (path.1 is the newest rotated segment) once the active segment
// reaches SegmentBytes, and retaining at most Segments total files. Every
// exported method is safe for concurrent use.
type Recorder struct {
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

// Option configures a Recorder at construction. Defaults match the Python
// FlightRecorder: 8 MiB segments, 8 retained segments, 256 KiB payloads.
type Option func(*Recorder)

func SegmentBytes(n int64) Option { return func(r *Recorder) { r.segmentBytes = n } }
func Segments(n int) Option       { return func(r *Recorder) { r.segments = n } }
func PayloadBytes(n int) Option   { return func(r *Recorder) { r.payloadBytes = n } }

// RunID overrides the recorder's run identifier, otherwise a random hex value.
func RunID(id string) Option { return func(r *Recorder) { r.run = id } }

// New opens (or creates) path for durable append and records one "coverage"
// event describing what the timeline does and does not capture.
func New(path string, opts ...Option) (*Recorder, error) {
	if path == "" {
		return nil, errors.New("flightrecorder: path required")
	}
	r := &Recorder{path: path, segmentBytes: 8 << 20, segments: 8, payloadBytes: 256 << 10}
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
	if _, err := r.Event("coverage", nil, true, map[string]any{
		"coverage": "All bridge.Client native requests, responses and exceptions, including background reads. " +
			"Requests/errors are fsynced; response rows become durable at the next durable record or rotation. " +
			"A host crash can leave an explicit unmatched request. No in-game per-frame/pawn transition trace.",
	}); err != nil {
		return nil, err
	}
	return r, nil
}

// Event appends one record. durable forces an fsync before returning, so a
// caller can be certain the record survives a crash; the natural use is a
// durable "request" row bracketing a non-durable "response" row that becomes
// durable only at the next durable record or segment rotation. context and
// payload are marshaled as JSON objects; a nil map encodes as {}.
func (r *Recorder) Event(kind string, context map[string]any, durable bool, payload map[string]any) (uint64, error) {
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
			"preview":        string(encoded[:r.payloadBytes]),
		}
		for _, key := range []string{"request", "tool", "category"} {
			if value, ok := payload[key]; ok && correlatable(value) {
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
	if context == nil {
		context = map[string]any{}
	}
	row := struct {
		Version  int             `json:"version"`
		Run      string          `json:"run"`
		Sequence uint64          `json:"sequence"`
		WallTime float64         `json:"wall_time"`
		Kind     string          `json:"kind"`
		Context  map[string]any  `json:"context"`
		Payload  json.RawMessage `json:"payload"`
	}{1, r.run, sequence, float64(began.UnixNano()) / 1e9, kind, context, encoded}
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

func correlatable(value any) bool {
	switch v := value.(type) {
	case string:
		return len(v) <= 256
	case int, int32, int64, uint, uint32, uint64, float64:
		return true
	default:
		return false
	}
}

func (r *Recorder) ensureOpen() error {
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
func (r *Recorder) rotateLocked() error {
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

func (r *Recorder) segmentPath(index int) string { return fmt.Sprintf("%s.%d", r.path, index) }

// Close finishes the current segment durably. Later events reopen it.
func (r *Recorder) Close() error {
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

// Stats reports counters useful for diagnostics; it never fails.
type Stats struct {
	Records           uint64
	Truncated         uint64
	Rotations         uint64
	DurableRecords    uint64
	RecordingSeconds  float64
	RetentionSegments int
	SegmentBytes      int64
	PayloadBytes      int
}

func (r *Recorder) Stats() Stats {
	r.mu.Lock()
	defer r.mu.Unlock()
	return Stats{
		Records: r.records, Truncated: r.truncate, Rotations: r.rotate, DurableRecords: r.durable,
		RecordingSeconds: r.elapsed.Seconds(), RetentionSegments: r.segments,
		SegmentBytes: r.segmentBytes, PayloadBytes: r.payloadBytes,
	}
}

type actionContextKey struct{}

// WithAction attaches an action/goal correlation pair to ctx; Event calls
// made against a bridge that reads it via ActionFrom include it in context.
func WithAction(ctx context.Context, actionID, goalID string) context.Context {
	if actionID == "" {
		return ctx
	}
	return context.WithValue(ctx, actionContextKey{}, map[string]any{"action_id": actionID, "goal_id": goalID})
}

// ActionFrom reads back the correlation WithAction attached, or nil.
func ActionFrom(ctx context.Context) map[string]any {
	value, _ := ctx.Value(actionContextKey{}).(map[string]any)
	return value
}
