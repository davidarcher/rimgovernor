package httpapi

import (
	"net/http"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// AccessKind is the flight row kind of one HTTP request (flight-rows.md).
const AccessKind = "http_access"

// slowAccess is the duration at or above which a successful poll is still
// recorded.
const slowAccess = 250 * time.Millisecond

// accessComponent names the subsystem on the row.
const accessComponent = "httpapi"

// Access rows (#2055): Server.Handler wraps the routes so every request
// writes one http_access row to Config.Access, except a successful GET of a
// frequently polled path under slowAccess, which would otherwise drown the
// stream. Errors, slow requests and every non-GET are always kept. A pprof
// request holds the connection for the whole capture, so its row carries
// long_lived and its dur_ms is not request latency.

// polledPaths are the exact paths the launcher and spectator poll.
var polledPaths = map[string]bool{
	"/api/state":         true,
	"/api/spectator/now": true,
	"/api/routines":      true,
	"/api/health":        true,
}

// polledPrefixes cover the presentation reads and the notifications feed.
var polledPrefixes = []string{"/api/presentation/"}

func isPolled(method, path string) bool {
	if method != http.MethodGet {
		return false
	}
	if polledPaths[path] {
		return true
	}
	for _, prefix := range polledPrefixes {
		if strings.HasPrefix(path, prefix) {
			return true
		}
	}
	return false
}

func isLongLived(path string) bool {
	return path == strings.TrimSuffix(pprofPrefix, "/") || strings.HasPrefix(path, pprofPrefix)
}

// keepAccess reports whether a finished request is recorded.
func keepAccess(method, path string, status int, dur time.Duration) bool {
	if isLongLived(path) || !isPolled(method, path) {
		return true
	}
	return status < 200 || status > 299 || dur >= slowAccess
}

// statusRecorder captures the status and body size. Unwrap exposes the real
// writer to http.NewResponseController (the pprof CPU profile lifts its
// write deadline through it); Flush delegates for streaming responses.
type statusRecorder struct {
	http.ResponseWriter
	status int
	bytes  int64
}

func (r *statusRecorder) WriteHeader(status int) {
	if r.status == 0 {
		r.status = status
	}
	r.ResponseWriter.WriteHeader(status)
}

func (r *statusRecorder) Write(p []byte) (int, error) {
	if r.status == 0 {
		r.status = http.StatusOK
	}
	n, err := r.ResponseWriter.Write(p)
	r.bytes += int64(n)
	return n, err
}

func (r *statusRecorder) Flush() {
	if r.status == 0 {
		r.status = http.StatusOK
	}
	if f, ok := r.ResponseWriter.(http.Flusher); ok {
		f.Flush()
	}
}

func (r *statusRecorder) Unwrap() http.ResponseWriter { return r.ResponseWriter }

// accessHandler records each request of next to recorder; a nil recorder
// returns next unchanged.
func accessHandler(next http.Handler, recorder telemetry.Recorder) http.Handler {
	if recorder == nil {
		return next
	}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		rec := &statusRecorder{ResponseWriter: w}
		next.ServeHTTP(rec, r)
		status := rec.status
		if status == 0 {
			status = http.StatusOK
		}
		dur := time.Since(start)
		if !keepAccess(r.Method, r.URL.Path, status, dur) {
			return
		}
		level := "INFO"
		if status >= 500 {
			level = "WARN"
		}
		rowContext := map[string]any{"level": level, "at": start.UTC().Format(time.RFC3339Nano), telemetry.ComponentKey: accessComponent}
		telemetry.NewTrace().Stamp(rowContext)
		if tick, ok := telemetry.Tick(); ok {
			rowContext["tick"] = tick
		}
		_, _ = recorder.Event(AccessKind, rowContext, false, map[string]any{
			"method":     r.Method,
			"path":       r.URL.Path,
			"status":     status,
			"dur_ms":     float64(dur) / float64(time.Millisecond),
			"bytes":      rec.bytes,
			"long_lived": isLongLived(r.URL.Path),
		})
	})
}
