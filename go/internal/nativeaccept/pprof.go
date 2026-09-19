package nativeaccept

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"time"
)

// Every service a run launches is profiled (#301): serve gets --pprof, a
// CPU profile runs from launch for the case's budget and is ended at Stop
// (DELETE /debug/pprof/profile) so it covers the run whatever its length,
// and Stop takes a heap snapshot before the kill. Both land beside the
// service's logs as cpu.pprof and heap.pprof (`go tool pprof <file>`) and
// under the launch's report entry as "pprof". A capture the service did
// not answer (it exited first, or crashed) is recorded as skipped, never
// as a failure. PprofEnv=0 opts out.
const PprofEnv = "RIMGOVERNOR_ACCEPT_PPROF"

// ProfileServices reports whether launched services are profiled: true
// unless PprofEnv opts out.
func ProfileServices() bool { return !envOptsOut(PprofEnv) }

// defaultProfileSeconds bounds the CPU profile of a launch whose report
// carries no budget (a bare LaunchService report).
const defaultProfileSeconds = 30 * 60

// profileStopWait bounds how long Stop waits for the ended CPU profile to
// arrive, and each of its own requests.
const profileStopWait = 30 * time.Second

// serviceProfile is one launch's captures in flight.
type serviceProfile struct {
	url    string
	dir    string
	client *http.Client
	cancel context.CancelFunc
	cpu    chan string // the CPU capture's outcome, once
	entry  map[string]any
}

// startProfile begins the CPU profile of a launched service for seconds,
// recording the outcome on entry["pprof"] as the captures complete.
func startProfile(url, dir string, seconds int, entry map[string]any) *serviceProfile {
	ctx, cancel := context.WithCancel(context.Background())
	p := &serviceProfile{url: url, dir: dir, client: &http.Client{}, cancel: cancel, cpu: make(chan string, 1), entry: map[string]any{"seconds": seconds}}
	entry["pprof"] = p.entry
	go func() {
		p.cpu <- p.fetch(ctx, fmt.Sprintf("/debug/pprof/profile?seconds=%d", seconds), "cpu.pprof")
	}()
	return p
}

// fetch saves one pprof route's body under dir as name and returns the
// outcome recorded on the entry: the file's path, or why it was skipped.
func (p *serviceProfile) fetch(ctx context.Context, route, name string) string {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, p.url+route, nil)
	if err != nil {
		return "skipped: " + err.Error()
	}
	resp, err := p.client.Do(req)
	if err != nil {
		return "skipped: " + err.Error()
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 512))
		return fmt.Sprintf("skipped: status %d: %s", resp.StatusCode, string(body))
	}
	path := filepath.Join(p.dir, name)
	file, err := os.Create(path)
	if err != nil {
		return "skipped: " + err.Error()
	}
	_, err = io.Copy(file, resp.Body)
	if closeErr := file.Close(); err == nil {
		err = closeErr
	}
	if err != nil {
		return "skipped: " + err.Error()
	}
	return path
}

// stop ends the captures: the heap snapshot first (the service is still
// running), then the CPU profile, waited for so its data is on disk before
// the caller kills the process. running is false when the service already
// exited, in which case nothing is requested and the CPU capture's own
// outcome (its connection dropped) is recorded.
func (p *serviceProfile) stop(running bool) {
	defer p.cancel()
	ctx, cancel := context.WithTimeout(context.Background(), profileStopWait)
	defer cancel()
	if running {
		p.entry["heap"] = p.fetch(ctx, "/debug/pprof/heap", "heap.pprof")
		req, err := http.NewRequestWithContext(ctx, http.MethodDelete, p.url+"/debug/pprof/profile", nil)
		if err == nil {
			resp, err := p.client.Do(req)
			if err == nil {
				resp.Body.Close()
			}
		}
	} else {
		p.entry["heap"] = "skipped: service exited"
	}
	select {
	case outcome := <-p.cpu:
		p.entry["cpu"] = outcome
	case <-ctx.Done():
		p.cancel()
		p.entry["cpu"] = "skipped: the profile did not arrive within " + profileStopWait.String()
	}
}

// profileSeconds is the CPU profile length for a launch: the report's
// budget (budget_ms), else defaultProfileSeconds.
func profileSeconds(report Report) int {
	if ms, ok := asUint64(report[BudgetMsKey]); ok && ms > 0 {
		return int((ms + 999) / 1000)
	}
	return defaultProfileSeconds
}
