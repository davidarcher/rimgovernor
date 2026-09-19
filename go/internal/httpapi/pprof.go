package httpapi

import (
	"net/http"
	"net/http/pprof"
	runtimepprof "runtime/pprof"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Profiling (Config.Pprof) mounts net/http/pprof under /debug/pprof/ on the
// controller's own listener, behind the same local-only and origin checks
// as every other route; it is off by default and the routes answer 404
// when it is. The CPU profile route is the controller's own handler with
// pprof's contract (GET /debug/pprof/profile?seconds=N, the write deadline
// lifted past N as pprof does) plus an early end: DELETE
// /debug/pprof/profile stops the capture in flight and the GET completes
// with the profile so far, so a caller that started a profile for a run's
// whole budget still receives it when the run ends sooner.

const pprofPrefix = "/debug/pprof/"

// cpuProfileRun coordinates the process's single CPU profile: the runtime
// allows one at a time, so an early stop reaches the capture in flight.
type cpuProfileRun struct {
	mu   sync.Mutex
	stop chan struct{}
}

var cpuProfile cpuProfileRun

var pprofMux = func() *http.ServeMux {
	mux := http.NewServeMux()
	mux.HandleFunc(pprofPrefix, pprof.Index)
	mux.HandleFunc(pprofPrefix+"cmdline", pprof.Cmdline)
	mux.HandleFunc(pprofPrefix+"symbol", pprof.Symbol)
	mux.HandleFunc(pprofPrefix+"trace", pprof.Trace)
	mux.HandleFunc(pprofPrefix+"profile", cpuProfile.serve)
	return mux
}()

// PprofHandler is the /debug/pprof handler Config.Pprof mounts, for a
// stand-in server that wants the same routes (acceptance fakes).
func PprofHandler() http.Handler { return pprofMux }

// handlePprof answers the /debug/pprof routes when profiling is on.
func (s *Server) handlePprof(w http.ResponseWriter, r *http.Request) bool {
	if r.URL.Path != strings.TrimSuffix(pprofPrefix, "/") && !strings.HasPrefix(r.URL.Path, pprofPrefix) {
		return false
	}
	if !s.config.Pprof {
		s.failure(w, r, 404, "not_found", "Profiling is not enabled (serve --pprof)")
		return true
	}
	pprofMux.ServeHTTP(w, r)
	return true
}

func (c *cpuProfileRun) serve(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("X-Content-Type-Options", "nosniff")
	switch r.Method {
	case http.MethodGet, http.MethodHead:
	case http.MethodDelete:
		c.mu.Lock()
		stop := c.stop
		c.stop = nil
		c.mu.Unlock()
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		if stop == nil {
			w.WriteHeader(http.StatusNotFound)
			w.Write([]byte("no CPU profile in flight\n"))
			return
		}
		close(stop)
		w.Write([]byte("CPU profile stopped\n"))
		return
	default:
		w.Header().Set("Allow", "GET, HEAD, DELETE")
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	seconds, err := strconv.ParseInt(r.FormValue("seconds"), 10, 64)
	if seconds <= 0 || err != nil {
		seconds = 30
	}
	duration := time.Duration(seconds) * time.Second
	// The whole capture, plus the server's own allowance for the write.
	if srv, ok := r.Context().Value(http.ServerContextKey).(*http.Server); ok && srv.WriteTimeout > 0 {
		_ = http.NewResponseController(w).SetWriteDeadline(time.Now().Add(duration + srv.WriteTimeout))
	}
	stop := make(chan struct{})
	c.mu.Lock()
	if c.stop != nil {
		c.mu.Unlock()
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		w.WriteHeader(http.StatusInternalServerError)
		w.Write([]byte("Could not enable CPU profiling: a profile is already in flight\n"))
		return
	}
	// Reserve the slot before starting so a concurrent DELETE sees it.
	c.stop = stop
	c.mu.Unlock()
	w.Header().Set("Content-Type", "application/octet-stream")
	w.Header().Set("Content-Disposition", `attachment; filename="profile"`)
	if err := runtimepprof.StartCPUProfile(w); err != nil {
		c.release(stop)
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		w.Header().Del("Content-Disposition")
		w.WriteHeader(http.StatusInternalServerError)
		w.Write([]byte("Could not enable CPU profiling: " + err.Error() + "\n"))
		return
	}
	timer := time.NewTimer(duration)
	defer timer.Stop()
	select {
	case <-timer.C:
	case <-r.Context().Done():
	case <-stop:
	}
	runtimepprof.StopCPUProfile()
	c.release(stop)
}

// release clears the slot if it still holds this capture.
func (c *cpuProfileRun) release(stop chan struct{}) {
	c.mu.Lock()
	if c.stop == stop {
		c.stop = nil
	}
	c.mu.Unlock()
}
