package httpapi

import (
	"context"
	"encoding/json"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// Telemetry (#299): the flight recorder serve keeps under the profile is
// readable over the controller's own listener, so a live game that seems
// to be doing nothing has evidence beyond stderr. Both routes are
// read-only and unauthenticated like /api/state, and answer 404 when the
// service runs without a recorder (serve --no-flight-recorder).
//
//   - GET /api/telemetry/events?since=<seq>&kind=<k,...>&limit=<n> pages the
//     retained segments by sequence: the rows after since (default 0, the
//     whole ring), of the named kinds (default every kind), at most limit
//     (default telemetryDefaultLimit, at most telemetryMaxLimit) and never
//     past the response bound. next_since is the last sequence returned,
//     the value to pass for the following page; more says whether a row
//     was left out. A "recording_gap" row (a corrupt line, or a sequence
//     the ring rotated away) is returned in place, with no sequence.
//   - GET /api/telemetry/metrics is the #297 block computed live over the
//     current launch's rows (nativeaccept.MetricNames; the harness-only
//     metrics — boot, waits, launches — stay 0) plus tick, tps, authority
//     and last_step_ms.
//
// Neither route follows the tail: a client polls at its own cadence. Both
// routes share one bridge.TimelineReader, so a poll decodes the rows
// appended since the last one rather than the retained ring (#375).

const (
	telemetryEventsPath   = "/api/telemetry/events"
	telemetryMetricsPath  = "/api/telemetry/metrics"
	telemetryDefaultLimit = 200
	telemetryMaxLimit     = 1000
)

// TelemetryEvent is one flight-recorder row on the wire.
type TelemetryEvent struct {
	Sequence *uint64        `json:"sequence"`
	Run      string         `json:"run,omitempty"`
	WallTime float64        `json:"wall_time"`
	Kind     string         `json:"kind"`
	Context  map[string]any `json:"context,omitempty"`
	Payload  map[string]any `json:"payload,omitempty"`
	// Gap fields, set on a "recording_gap" row.
	Reason string `json:"reason,omitempty"`
	Before uint64 `json:"before,omitempty"`
	After  uint64 `json:"after,omitempty"`
}

// TelemetryEvents is one page of GET /api/telemetry/events.
type TelemetryEvents struct {
	Events       []TelemetryEvent `json:"events"`
	NextSince    uint64           `json:"next_since"`
	LastSequence uint64           `json:"last_sequence"`
	More         bool             `json:"more"`
}

// TelemetryMetrics is GET /api/telemetry/metrics.
type TelemetryMetrics struct {
	Tick       *int64             `json:"tick"`
	TPS        float64            `json:"tps"`
	Authority  *Generation        `json:"authority"`
	LastStepMs float64            `json:"last_step_ms"`
	Run        string             `json:"run,omitempty"`
	Metrics    map[string]float64 `json:"metrics"`
}

// telemetryMetricNames mirrors nativeaccept.MetricNames (#297): the block
// carries every name, 0 when the source is absent.
var telemetryMetricNames = []string{
	"wall_ms", "boot_ms", "ticks_advanced", "wall_tps",
	"waits", "waits_stalled", "max_quiet_ms",
	"native_calls", "native_errors", "native_bytes",
	"reads_per_step_mean", "cache_hit_ratio",
	"native_queue_ms_mean", "native_exec_ms_mean",
	"service_launches", "evidence_bytes",
}

// handleTelemetry answers the /api/telemetry routes.
func (s *Server) handleTelemetry(w http.ResponseWriter, r *http.Request) bool {
	if r.URL.Path != telemetryEventsPath && r.URL.Path != telemetryMetricsPath {
		return false
	}
	if s.config.FlightRecorder == "" {
		s.failure(w, r, 404, "not_found", "Telemetry is unavailable: the service runs without a flight recorder")
		return true
	}
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		w.Header().Set("Allow", "GET, HEAD")
		s.failure(w, r, 405, "method_not_allowed", "Telemetry is read-only")
		return true
	}
	if len(r.RequestURI) > 2048 || r.ContentLength != 0 || len(r.TransferEncoding) != 0 {
		s.failure(w, r, 400, "invalid_request", "Telemetry reads require a bounded URL and no body")
		return true
	}
	query, err := url.ParseQuery(r.URL.RawQuery)
	if err != nil {
		s.failure(w, r, 400, "invalid_query", "Invalid query")
		return true
	}
	ctx, cancel := context.WithTimeout(r.Context(), s.config.ReadTimeout)
	defer cancel()
	switch r.URL.Path {
	case telemetryEventsPath:
		since, kinds, limit, err := parseTelemetryQuery(query)
		if err != "" {
			s.failure(w, r, 400, "invalid_query", err)
			return true
		}
		rows, readErr := s.telemetry.Read()
		if readErr != nil {
			s.readFailure(w, r, readErr)
			return true
		}
		s.write(w, r, 200, telemetryEvents(rows, since, kinds, limit, s.config.MaxResponseBytes-4096))
	case telemetryMetricsPath:
		if len(query) != 0 {
			s.failure(w, r, 400, "invalid_query", "This route accepts no query parameters")
			return true
		}
		rows, readErr := s.telemetry.Read()
		if readErr != nil {
			s.readFailure(w, r, readErr)
			return true
		}
		snapshot, snapErr := s.snapshots.Snapshot(ctx)
		if snapErr != nil {
			s.readFailure(w, r, snapErr)
			return true
		}
		s.write(w, r, 200, telemetryMetrics(rows, snapshot, ringBytes(s.config.FlightRecorder), time.Now()))
	}
	return true
}

// parseTelemetryQuery reads since, kind and limit; the error is the 400
// detail, empty when the query is valid.
func parseTelemetryQuery(query url.Values) (since uint64, kinds map[string]bool, limit int, detail string) {
	limit = telemetryDefaultLimit
	for key, values := range query {
		if len(values) != 1 {
			return 0, nil, 0, "Each of since, kind and limit may appear once"
		}
		value := strings.TrimSpace(values[0])
		switch key {
		case "since":
			n, err := strconv.ParseUint(value, 10, 64)
			if err != nil {
				return 0, nil, 0, "since must be a non-negative sequence"
			}
			since = n
		case "limit":
			n, err := strconv.Atoi(value)
			if err != nil || n < 1 || n > telemetryMaxLimit {
				return 0, nil, 0, "limit must be 1.." + strconv.Itoa(telemetryMaxLimit)
			}
			limit = n
		case "kind":
			kinds = map[string]bool{}
			for _, kind := range strings.Split(value, ",") {
				kind = strings.TrimSpace(kind)
				if kind == "" || len(kind) > 64 {
					return 0, nil, 0, "kind is a comma-separated list of row kinds"
				}
				kinds[kind] = true
			}
		default:
			return 0, nil, 0, "Unknown query parameter " + key
		}
	}
	return since, kinds, limit, ""
}

// telemetryEvents pages rows: those after since, of the named kinds (or
// all), up to limit rows and budget encoded bytes. A gap row is kept when
// the sequence it stands before is after since (the paging client has not
// seen it), and always on a whole-ring read.
func telemetryEvents(rows []bridge.TimelineRecord, since uint64, kinds map[string]bool, limit, budget int) TelemetryEvents {
	page := TelemetryEvents{Events: []TelemetryEvent{}, NextSince: since}
	for i := len(rows) - 1; i >= 0; i-- {
		if rows[i].HasSeq {
			page.LastSequence = rows[i].Sequence
			break
		}
	}
	used := 0
	for _, row := range rows {
		if row.Kind == "recording_gap" {
			if since != 0 && row.Before <= since {
				continue
			}
		} else if !row.HasSeq || row.Sequence <= since {
			continue
		}
		if kinds != nil && !kinds[row.Kind] {
			continue
		}
		event := telemetryEvent(row)
		encoded, err := json.Marshal(event)
		if err != nil {
			continue
		}
		if len(page.Events) >= limit || used+len(encoded)+1 > budget {
			page.More = true
			break
		}
		used += len(encoded) + 1
		page.Events = append(page.Events, event)
		if row.HasSeq {
			page.NextSince = row.Sequence
		}
	}
	return page
}

func telemetryEvent(row bridge.TimelineRecord) TelemetryEvent {
	event := TelemetryEvent{Run: row.Run, WallTime: row.WallTime, Kind: row.Kind, Context: row.Context, Payload: row.Payload}
	if row.HasSeq {
		sequence := row.Sequence
		event.Sequence = &sequence
	} else {
		event.Reason, event.Before, event.After = row.Reason, row.Before, row.After
	}
	return event
}

// telemetryMetrics computes the block over the newest run's rows (the
// launch answering the request), reading tick and authority from the
// snapshot the state route serves. The clock's wall TPS over the run is
// the block's wall_tps; tps is the same figure, the field the dashboard
// reads beside tick.
func telemetryMetrics(rows []bridge.TimelineRecord, snapshot Snapshot, evidenceBytes int64, now time.Time) TelemetryMetrics {
	out := TelemetryMetrics{Metrics: map[string]float64{}}
	for _, name := range telemetryMetricNames {
		out.Metrics[name] = 0
	}
	if tick, known := snapshot.Tick.Value(); known && snapshot.Connected {
		value := int64(tick)
		out.Tick = &value
	}
	if raw, known := snapshot.Generation.Value(); known && raw.Validate() == nil {
		out.Authority = generation(raw)
	}
	run := ""
	for i := len(rows) - 1; i >= 0; i-- {
		if rows[i].HasSeq {
			run = rows[i].Run
			break
		}
	}
	current := rows[:0:0]
	for _, row := range rows {
		if row.HasSeq && row.Run == run {
			current = append(current, row)
		}
	}
	out.Run = run
	summary := bridge.SummarizePhases(current)
	for _, row := range current {
		if row.Kind == "clock_step" {
			if elapsed, ok := row.Payload["elapsed_ms"].(float64); ok {
				out.LastStepMs = elapsed
			}
		}
	}
	if summary.FirstWall > 0 {
		out.Metrics["wall_ms"] = float64(now.UnixNano())/1e6 - summary.FirstWall*1e3
	}
	out.Metrics["ticks_advanced"] = float64(summary.Clock.TicksAdvanced)
	out.Metrics["wall_tps"] = summary.Clock.WallTPS
	out.TPS = summary.Clock.WallTPS
	var calls, errs, hits, bytes, timed uint64
	var queue, execute float64
	for _, tool := range summary.Tools {
		calls += tool.Calls
		errs += tool.Errors
		hits += tool.CacheHits
		bytes += tool.ResponseBytes
		timed += tool.NativeTimed
		queue += tool.NativeQueueMs
		execute += tool.NativeExecuteMs
	}
	out.Metrics["native_calls"] = float64(calls)
	out.Metrics["native_errors"] = float64(errs)
	out.Metrics["native_bytes"] = float64(bytes)
	if calls+hits > 0 {
		out.Metrics["cache_hit_ratio"] = float64(hits) / float64(calls+hits)
	}
	if summary.Steps.Steps > 0 {
		out.Metrics["reads_per_step_mean"] = float64(summary.Steps.Reads) / float64(summary.Steps.Steps)
	}
	if timed > 0 {
		out.Metrics["native_queue_ms_mean"] = queue / float64(timed)
		out.Metrics["native_exec_ms_mean"] = execute / float64(timed)
	}
	if run != "" {
		out.Metrics["service_launches"] = 1
	}
	out.Metrics["evidence_bytes"] = float64(evidenceBytes)
	return out
}

// ringBytes sums the sizes of the ring's files: the active path and its
// rotated segments.
func ringBytes(path string) int64 {
	var total int64
	if info, err := os.Stat(path); err == nil {
		total += info.Size()
	}
	matches, _ := filepath.Glob(path + ".*")
	for _, match := range matches {
		if _, err := strconv.Atoi(strings.TrimPrefix(match, path+".")); err != nil {
			continue
		}
		if info, err := os.Stat(match); err == nil {
			total += info.Size()
		}
	}
	return total
}
