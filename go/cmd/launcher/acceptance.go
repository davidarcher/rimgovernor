package main

import (
	"errors"
	"fmt"
	"io"
	"strings"
	"sync"
	"time"
)

// The Acceptance tab: the registered native acceptance cases, each runnable on
// demand in a visible game window for local playtesting. The launcher shells
// out to the acceptance command (`go run`, the same way the controller is
// built) instead of linking the case packages: the registry is that command's.

// AcceptCase is one registered case: its "<area>/<case>" name and the one-line
// scope the registry carries.
type AcceptCase struct {
	Name  string `json:"name"`
	Scope string `json:"scope"`
}

// ParseCaseList reads `acceptance list`'s "<name>\t<scope>" lines.
func ParseCaseList(out string) []AcceptCase {
	var cases []AcceptCase
	for _, line := range strings.Split(strings.ReplaceAll(out, "\r", ""), "\n") {
		name, scope, _ := strings.Cut(line, "\t")
		if name = strings.TrimSpace(name); name != "" {
			cases = append(cases, AcceptCase{Name: name, Scope: strings.TrimSpace(scope)})
		}
	}
	return cases
}

// AcceptRunArgs are `go run` arguments for one windowed run of a case on root.
// The series is left alone: a playtest is not a measurement. fresh discards the
// case's checkpoint ring, otherwise a case whose last run failed resumes.
func AcceptRunArgs(name, root, output, rimgovernor string, fresh bool) []string {
	args := []string{"run", "./internal/nativeaccept/cmd/acceptance", "run", name,
		"-root", root, "-output", output, "-headless=false", "-no-series"}
	if rimgovernor != "" {
		args = append(args, "-rimgovernor", rimgovernor)
	}
	if fresh {
		args = append(args, "-fresh")
	}
	return args
}

// Run states.
const (
	acceptIdle    = "idle"
	acceptRunning = "running"
	acceptPassed  = "passed"
	acceptFailed  = "failed"
	acceptStopped = "stopped"
)

// acceptKeepLines is how much of a run's output the tab shows.
const acceptKeepLines = 400

// AcceptCasesView is the case list, loaded once per launcher process (a landing
// that adds cases restarts the launcher).
type AcceptCasesView struct {
	Cases   []AcceptCase `json:"cases"`
	Loading bool         `json:"loading"`
	Error   string       `json:"error"`
}

// AcceptRunView is what the tab polls while a run is in flight or after one.
type AcceptRunView struct {
	State     string   `json:"state"`
	Case      string   `json:"case"`
	ElapsedMs int64    `json:"elapsedMs"`
	Output    []string `json:"output"`
	Dir       string   `json:"dir"`
	Error     string   `json:"error"`
}

// acceptProc is a started acceptance run.
type acceptProc interface {
	Wait() error
	// Stop ends the run and everything it started.
	Stop()
}

// acceptHost is what the runner needs of the launcher.
type acceptHost interface {
	// List is `acceptance list`'s stdout.
	List() (string, error)
	// Guard is nil when a run may start: the game copy and the controller are
	// not in use.
	Guard() error
	// OutputDir is a fresh output directory for a run of name.
	OutputDir(name string) string
	// Spawn starts the run, its output going to out.
	Spawn(name, output string, fresh bool, out io.Writer) (acceptProc, error)
}

type acceptRunner struct {
	host acceptHost
	now  func() time.Time

	mu      sync.Mutex
	cases   []AcceptCase
	tried   bool // a load was started; a failed one is not retried until the launcher restarts
	loading bool
	listErr string

	state    string
	name     string
	dir      string
	errMsg   string
	started  time.Time
	ended    time.Time
	lines    []string
	partial  string
	proc     acceptProc
	stopping bool
}

func newAcceptRunner(host acceptHost) *acceptRunner {
	return &acceptRunner{host: host, now: time.Now, state: acceptIdle}
}

// Running reports whether a run is in flight.
func (r *acceptRunner) Running() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.state == acceptRunning
}

// Cases is the registry, loading it in the background on first use.
func (r *acceptRunner) Cases() AcceptCasesView {
	r.mu.Lock()
	defer r.mu.Unlock()
	if !r.tried {
		r.tried, r.loading = true, true
		go r.load()
	}
	return AcceptCasesView{Cases: append([]AcceptCase{}, r.cases...), Loading: r.loading, Error: r.listErr}
}

func (r *acceptRunner) load() {
	out, err := r.host.List()
	r.mu.Lock()
	defer r.mu.Unlock()
	r.loading = false
	if err != nil {
		r.listErr = err.Error()
		return
	}
	r.cases = ParseCaseList(out)
}

// View is the current run.
func (r *acceptRunner) View() AcceptRunView {
	r.mu.Lock()
	defer r.mu.Unlock()
	end := r.ended
	if r.state == acceptRunning {
		end = r.now()
	}
	var elapsed int64
	if !r.started.IsZero() {
		elapsed = end.Sub(r.started).Milliseconds()
	}
	lines := append([]string{}, r.lines...)
	if r.partial != "" {
		lines = append(lines, r.partial)
	}
	return AcceptRunView{State: r.state, Case: r.name, ElapsedMs: elapsed, Output: lines, Dir: r.dir, Error: r.errMsg}
}

// Write collects the run's output by line.
func (r *acceptRunner) Write(p []byte) (int, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	text := r.partial + strings.ReplaceAll(string(p), "\r", "")
	parts := strings.Split(text, "\n")
	r.partial = parts[len(parts)-1]
	r.lines = append(r.lines, parts[:len(parts)-1]...)
	if n := len(r.lines); n > acceptKeepLines {
		r.lines = append([]string(nil), r.lines[n-acceptKeepLines:]...)
	}
	return len(p), nil
}

// Run starts one case. It refuses while a run is in flight, while the registry
// has not loaded or does not know the case, and while the guard refuses.
func (r *acceptRunner) Run(name string, fresh bool) error {
	r.mu.Lock()
	if r.state == acceptRunning {
		r.mu.Unlock()
		return fmt.Errorf("%s is still running", r.name)
	}
	known := false
	for _, c := range r.cases {
		known = known || c.Name == name
	}
	r.mu.Unlock()
	if !known {
		return fmt.Errorf("unknown case %q", name)
	}
	if err := r.host.Guard(); err != nil {
		return err
	}
	dir := r.host.OutputDir(name)
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.state == acceptRunning {
		return fmt.Errorf("%s is still running", r.name)
	}
	proc, err := r.host.Spawn(name, dir, fresh, r)
	if err != nil {
		return err
	}
	r.state, r.name, r.dir, r.errMsg = acceptRunning, name, dir, ""
	r.started, r.ended, r.lines, r.partial = r.now(), time.Time{}, nil, ""
	r.proc, r.stopping = proc, false
	go r.wait(proc)
	return nil
}

func (r *acceptRunner) wait(proc acceptProc) {
	err := proc.Wait()
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.proc != proc {
		return
	}
	r.proc, r.ended = nil, r.now()
	if r.partial != "" {
		r.lines, r.partial = append(r.lines, r.partial), ""
	}
	switch {
	case r.stopping:
		r.state = acceptStopped
	case err != nil:
		r.state, r.errMsg = acceptFailed, err.Error()
	default:
		r.state = acceptPassed
	}
}

// Stop ends the run in flight. The game it opened stays up: Close game ends it.
func (r *acceptRunner) Stop() error {
	r.mu.Lock()
	proc := r.proc
	r.stopping = proc != nil
	r.mu.Unlock()
	if proc == nil {
		return errors.New("no acceptance case is running")
	}
	proc.Stop()
	return nil
}
