package main

import (
	"context"
	"fmt"
	"net/http"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// The optional local telemetry stack (tools/observability/compose.yml, see
// docs/developers/observability.md). The controller never depends on it: the
// launcher only starts and stops the container and, when the Settings toggle
// is on, hands serve the OTLP endpoint.
const (
	obsProject = "rimgovernor-observability"
	// obsOTLPEndpoint is the stack's OTLP gRPC port, serve's default protocol.
	obsOTLPEndpoint = "http://localhost:4317"
	grafanaHealth   = grafanaURL + "/api/health"
	// obsSnapshotDir/obsSnapshotFile place serve's --observe-snapshot copy at
	// <profile>/observe/current.sqlite, the directory compose.yml mounts.
	obsSnapshotDir  = "observe"
	obsSnapshotFile = "current.sqlite"
	// obsStartWindow is how long after Start the stack may still be coming up
	// (an image pull included) before status falls back to stopped.
	obsStartWindow = 5 * time.Minute

	dockerMissing = "Docker is not installed or not on PATH. The observability stack is optional; Play works without it."
	dockerDown    = "Docker is installed but its daemon is not running. Start Docker Desktop and try again."
)

// Observability states shown on the launcher page.
const (
	obsStopped  = "stopped"
	obsStarting = "starting"
	obsHealthy  = "healthy"
)

// ObsView is what the launcher page polls for the Observability section.
type ObsView struct {
	State string `json:"state"`
	// Problem is a plain line when Docker is missing, its daemon is down or
	// the last Start/Stop failed; "" otherwise.
	Problem string `json:"problem"`
	// Dashboards are the deep links the page shows while the stack is healthy.
	Dashboards []DashboardLink `json:"dashboards"`
}

// cmdRunner runs a command to completion and returns its combined output.
type cmdRunner interface {
	Run(ctx context.Context, name string, args ...string) ([]byte, error)
	LookPath(name string) (string, error)
}

// composeArgs is the docker command line running verb (for example "up -d")
// against the checked-in compose file.
func composeArgs(file string, verb ...string) []string {
	return append([]string{"compose", "-f", file, "-p", obsProject}, verb...)
}

type obsController struct {
	run     cmdRunner
	compose string // path of compose.yml
	probe   func() bool
	logf    func(format string, args ...any)
	now     func() time.Time

	mu      sync.Mutex
	busy    bool
	startAt time.Time // last accepted Start; zero when none or stopped
	problem string
}

func newObsController(repo string, run cmdRunner, logf func(string, ...any)) *obsController {
	return &obsController{
		run:     run,
		compose: filepath.Join(repo, "tools", "observability", "compose.yml"),
		probe:   probeGrafana,
		logf:    logf,
		now:     time.Now,
	}
}

// probeGrafana is whether Grafana answers its health endpoint.
func probeGrafana() bool {
	resp, err := (&http.Client{Timeout: time.Second}).Get(grafanaHealth)
	if err != nil {
		return false
	}
	resp.Body.Close()
	return resp.StatusCode == http.StatusOK
}

// dockerReady is "" when docker is on PATH and its daemon answers, else else
// the message for the player.
func (o *obsController) dockerReady(ctx context.Context) string {
	if _, err := o.run.LookPath("docker"); err != nil {
		return dockerMissing
	}
	if out, err := o.run.Run(ctx, "docker", "info", "--format", "{{.ServerVersion}}"); err != nil {
		o.logOutput(out)
		return dockerDown
	}
	return ""
}

func (o *obsController) logOutput(out []byte) {
	for _, line := range strings.Split(strings.TrimSpace(strings.ReplaceAll(string(out), "\r", "")), "\n") {
		if line != "" {
			o.logf("observability: %s", line)
		}
	}
}

// View is the current state. It only probes Grafana over HTTP and looks for
// docker on PATH, so polling it is cheap and never needs the daemon.
func (o *obsController) View() ObsView {
	healthy := o.probe()
	o.mu.Lock()
	defer o.mu.Unlock()
	v := ObsView{State: obsStopped, Problem: o.problem, Dashboards: dashboardLinks}
	switch {
	case healthy:
		v.State = obsHealthy
		o.problem, v.Problem = "", ""
	case o.busy || !o.startAt.IsZero() && o.now().Sub(o.startAt) < obsStartWindow:
		v.State = obsStarting
	}
	if v.State == obsStopped && v.Problem == "" {
		if _, err := o.run.LookPath("docker"); err != nil {
			v.Problem = dockerMissing
		}
	}
	return v
}

// Start brings the stack up detached.
func (o *obsController) Start() { o.exec(true, "up", "-d") }

// Stop takes the stack down; its data volume is kept.
func (o *obsController) Stop() { o.exec(false, "down") }

func (o *obsController) exec(start bool, verb ...string) {
	o.mu.Lock()
	if o.busy {
		o.mu.Unlock()
		return
	}
	o.busy, o.problem = true, ""
	o.mu.Unlock()
	var err error
	if msg := o.dockerReady(context.Background()); msg != "" {
		err = problemError(msg)
	} else {
		args := composeArgs(o.compose, verb...)
		o.logf("observability: docker %s", strings.Join(args, " "))
		var out []byte
		out, err = o.run.Run(context.Background(), "docker", args...)
		o.logOutput(out)
		if err != nil {
			err = fmt.Errorf("docker compose %s failed: %v (see Details)", verb[0], err)
		}
	}
	o.mu.Lock()
	defer o.mu.Unlock()
	o.busy = false
	switch {
	case err != nil:
		o.problem = err.Error()
		o.startAt = time.Time{}
		o.logf("observability: %v", err)
	case start:
		o.startAt = o.now()
	default:
		o.startAt = time.Time{}
	}
}

// problemError carries a player-facing message verbatim.
type problemError string

func (e problemError) Error() string { return string(e) }
