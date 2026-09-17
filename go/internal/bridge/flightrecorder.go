// Flight recorder: an opt-in bounded native timeline. Requests reach durable
// storage before dispatch. Construction is always explicit; there is no hidden
// global recorder.
package bridge

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
// 8 MiB segments, 8 retained segments, 256 KiB payloads.
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
	r := &FlightRecorder{path: path, segmentBytes: 8 << 20, segments: 8, payloadBytes: 256 << 10}
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
			"preview":        string(encoded[:r.payloadBytes]),
		}
		for _, key := range []string{"request", "tool", "native_tool", "category", "timing"} {
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

type actionContextKey struct{}

// WithFlightAction attaches an action/goal correlation pair to ctx; Event calls
// made against a bridge that reads it via flightActionFrom include it in context.
func WithFlightAction(ctx context.Context, actionID, goalID string) context.Context {
	if actionID == "" {
		return ctx
	}
	return context.WithValue(ctx, actionContextKey{}, map[string]any{"action_id": actionID, "goal_id": goalID})
}

// flightActionFrom reads back the correlation WithFlightAction attached, or nil.
func flightActionFrom(ctx context.Context) map[string]any {
	value, _ := ctx.Value(actionContextKey{}).(map[string]any)
	return value
}
