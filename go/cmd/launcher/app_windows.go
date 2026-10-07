//go:build windows

package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/setup"
)

// Controller states.
const (
	ctrlStopped  = "stopped"
	ctrlStarting = "starting"
	ctrlRunning  = "running"
)

// Artifact is one status row.
type Artifact struct {
	Name   string `json:"name"`
	State  string `json:"state"`
	Detail string `json:"detail"`
}

// View is what the page polls.
type View struct {
	Artifacts  []Artifact `json:"artifacts"`
	Controller string     `json:"controller"`
	Message    string     `json:"message"`
	GameUp     bool       `json:"gameUp"`
	Log        []string   `json:"log"`
	Settings   Settings   `json:"settings"`
	Saves      []string   `json:"saves"`
}

const (
	artLayout     = "Game files"
	artMod        = "RimGovernor mod"
	artController = "Controller"
)

// Jobs: one of each runs at a time. setup covers the game layout and
// the mod, in that order (the mod needs the layout).
const (
	jobSetup      = "setup"
	jobController = "controller"
)

type app struct {
	repo, private string
	layout        setup.Layout

	mu        sync.Mutex
	artifacts []Artifact
	busy      map[string]bool
	// failed jobs are not retried by the poll until the next focus.
	failed     map[string]bool
	modPending bool
	lastFull   time.Time
	ctrl       string
	message    string
	gameUp     bool
	log        []string
	settings   Settings
	port       int // the port the running controller listens on; 0 = recorded or firstPort
	cmd        *exec.Cmd
	tail       *logTail      // the Log panel: the newest run's collapsed flight rows
	recorder   *recorderTail // the Problems tab's reader of the flight recorder
	colony     *colonyRunner // new-colony generation (#2025)
	loading    loadSlot      // the save load in flight (one at a time)
	accept     *acceptRunner // the Acceptance tab's case runs
}

func newApp(repo string) *app {
	a := &app{repo: repo, private: filepath.Join(repo, ".rimgovernor"), layout: setup.NewLayout(repo), ctrl: ctrlStopped,
		busy: map[string]bool{}, failed: map[string]bool{}}
	a.recorder = newRecorderTail(filepath.Join(a.layout.Root, "profile", "flight", "flight.jsonl"))
	a.tail = &logTail{recorder: a.recorder}
	for _, n := range []string{artLayout, artMod, artController} {
		a.artifacts = append(a.artifacts, Artifact{Name: n, State: StateBuilding, Detail: "Checking"})
	}
	s, err := LoadSettings(a.settingsPath())
	if err != nil {
		a.logf("settings: %v (using defaults)", err)
	}
	a.settings = s
	spec := DefaultNewColonySpec()
	if s.NewColony != nil {
		spec = *s.NewColony
	}
	a.colony = newColonyRunner(colonyApp{a}, spec)
	a.accept = newAcceptRunner(acceptHostApp{a})
	return a
}

func (a *app) settingsPath() string  { return filepath.Join(a.private, "launcher.json") }
func (a *app) recordPath() string    { return filepath.Join(a.private, "launcher-controller.json") }
func (a *app) controllerExe() string { return filepath.Join(a.private, "go", "rimgovernor.exe") }
func (a *app) configPath() string    { return filepath.Join(a.layout.Root, "config", "config.json") }
func (a *app) url(port int) string   { return "http://127.0.0.1:" + strconv.Itoa(port) }

// Write appends build output to the log panel.
func (a *app) Write(p []byte) (int, error) {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.log = append(a.log, strings.Split(strings.TrimRight(strings.ReplaceAll(string(p), "\r", ""), "\n"), "\n")...)
	if n := len(a.log); n > 3000 {
		a.log = append([]string(nil), a.log[n-3000:]...)
	}
	return len(p), nil
}

func (a *app) logf(format string, args ...any) { fmt.Fprintf(a, format+"\n", args...) }

func (a *app) set(name, state, detail string) {
	a.mu.Lock()
	defer a.mu.Unlock()
	for i := range a.artifacts {
		if a.artifacts[i].Name == name {
			if a.artifacts[i].State == state && a.artifacts[i].Detail == detail {
				return
			}
			a.artifacts[i].State, a.artifacts[i].Detail = state, detail
		}
	}
	if state == StateFailed {
		a.log = append(a.log, name+": "+detail)
	}
}

func (a *app) setController(state, message string) {
	a.mu.Lock()
	a.ctrl, a.message = state, message
	a.mu.Unlock()
}

func (a *app) view() View {
	a.mu.Lock()
	defer a.mu.Unlock()
	return View{
		Artifacts:  append([]Artifact(nil), a.artifacts...),
		Controller: a.ctrl,
		Message:    a.message,
		GameUp:     a.gameUp,
		Log:        append([]string{}, a.log...),
		Settings:   a.settings,
		Saves:      ListSaves(a.savesDir()),
	}
}

func (a *app) savesDir() string { return filepath.Join(a.layout.Root, "profile", "Saves") }

func (a *app) saveSettings(s Settings) error {
	if err := s.Validate(); err != nil {
		return err
	}
	if err := SaveSettings(a.settingsPath(), s); err != nil {
		return err
	}
	a.mu.Lock()
	a.settings = s
	spec := DefaultNewColonySpec()
	if s.NewColony != nil {
		spec = *s.NewColony
	}
	a.colony = newColonyRunner(colonyApp{a}, spec)
	a.mu.Unlock()
	return nil
}

// job runs fn in the background unless a job of that name is running (or
// failed since the last focus, for the poll); ok reports success.
func (a *app) job(name string, poll bool, fn func() (ok bool)) {
	a.mu.Lock()
	if a.busy[name] || (poll && a.failed[name]) {
		a.mu.Unlock()
		return
	}
	a.busy[name] = true
	a.mu.Unlock()
	go func() {
		ok := fn()
		a.mu.Lock()
		a.busy[name], a.failed[name] = false, !ok
		a.mu.Unlock()
	}()
}

// focus is the full check: the window gained focus, or the launcher
// opened. It hashes the native sources and runs go build (cheap when
// warm), so it is not part of the poll; it is debounced.
func (a *app) focus() {
	a.mu.Lock()
	if time.Since(a.lastFull) < 5*time.Second {
		a.mu.Unlock()
		return
	}
	a.lastFull = time.Now()
	a.failed = map[string]bool{}
	a.mu.Unlock()
	a.poll()
	a.job(jobSetup, false, a.prepareSetup)
	a.job(jobController, false, a.prepareController)
}

// poll is the cheap check every few seconds: file presence and mtimes,
// the game process and the controller's health. A pending mod update
// applies once the game has closed.
func (a *app) poll() {
	running, err := setup.RunningGames(a.layout.GameCopy)
	a.mu.Lock()
	if err == nil {
		a.gameUp = len(running) > 0
	}
	gameUp, pending, state, port, own := a.gameUp, a.modPending, a.ctrl, a.activePort(), a.cmd != nil
	a.mu.Unlock()
	if _, err := os.Stat(a.configPath()); err != nil || (pending && !gameUp) {
		a.job(jobSetup, true, a.prepareSetup)
	}
	if state != ctrlStarting && !own {
		// Adopt only the controller this launcher recorded; another
		// checkout answering on the same port is not ours.
		if a.recordedAlive(port) && healthy(a.url(port)) {
			a.setController(ctrlRunning, "")
		} else if state == ctrlRunning {
			a.setController(ctrlStopped, "The controller stopped responding.")
		}
	}
}

func (a *app) isBusy(name string) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.busy[name]
}

func (a *app) monitor() {
	for {
		time.Sleep(3 * time.Second)
		a.poll()
	}
}

// prepareSetup: the game layout and production mod through
// the acceptance setup (in place, idempotent). Nothing is replaced while
// the game copy runs; the mod update is then left pending.
func (a *app) prepareSetup() bool {
	if a.accept.Running() {
		// The run's preflight owns the game copy and its mod; the next poll
		// restores the production build once the game closes.
		return true
	}
	current, err := na.SourceTreeHash(a.repo)
	if err != nil {
		a.set(artMod, StateFailed, err.Error())
		return false
	}
	manifest, _ := na.ReadPackageManifest(filepath.Join(a.layout.GameCopy, "Mods", "RimGovernor"))
	modState, modReason := ModState(manifest, current)
	running, err := setup.RunningGames(a.layout.GameCopy)
	if err != nil {
		a.set(artLayout, StateFailed, err.Error())
		return false
	}
	if len(running) > 0 {
		a.set(artLayout, StateOK, "In use by the running game")
		a.mu.Lock()
		a.gameUp, a.modPending = true, modState != StateOK
		a.mu.Unlock()
		if modState == StateOK {
			a.set(artMod, StateOK, "Up to date")
		} else {
			a.set(artMod, StatePending, "Update pending; applies after the game closes")
		}
		return true
	}
	if _, err := os.Stat(a.layout.GameCopy); err == nil && modState == StateOK {
		// Nothing to rebuild: a controller left running on this root is no
		// reason to refuse, so the existing game copy is adopted as it is.
		a.set(artLayout, StateOK, "Ready")
		a.set(artMod, StateOK, "Up to date")
		return true
	}
	// Setup refuses while this root's own processes run; they are this
	// root's to stop, by pid (the game itself was ruled out above).
	if owned, err := setup.ListProcesses(setup.HarnessImages...); err == nil {
		for _, p := range owned {
			if p.PID != os.Getpid() && (p.Under(a.layout.GameCopy) || p.Under(a.layout.Root) || p.Under(a.layout.Bin)) {
				a.logf("stopping %s (pid %d) left running on this root so setup can refresh it", p.Name, p.PID)
				kill(p.PID)
			}
		}
	}
	a.set(artLayout, StateBuilding, "Refreshing")
	if modState != StateOK {
		a.set(artMod, StateBuilding, "Building ("+modReason+")")
	}
	inputs, err := setup.Discover(setup.Overrides{Repo: a.repo})
	if err != nil {
		a.set(artLayout, StateFailed, err.Error())
		a.set(artMod, StateFailed, "Needs the game files")
		return false
	}
	if _, err := setup.Run(context.Background(), setup.Options{Layout: a.layout, Inputs: inputs, Fixtures: []string{}, SkipBinaries: true, Log: a}); err != nil {
		a.set(artLayout, StateFailed, err.Error())
		if modState != StateOK {
			a.set(artMod, StateFailed, "Build failed; see Details")
		}
		return false
	}
	a.mu.Lock()
	a.modPending = false
	a.mu.Unlock()
	a.set(artLayout, StateOK, "Ready")
	a.set(artMod, StateOK, "Up to date")
	return true
}

// goEnv is the toolchain every build here uses.
func goEnv() []string { return append(os.Environ(), "GOTOOLCHAIN=go1.27.1", "CGO_ENABLED=0") }

// prepareController rebuilds the controller and replaces it only when the
// bytes changed (a running one is renamed aside, which Windows allows).
func (a *app) prepareController() bool {
	exe := a.controllerExe()
	if _, err := os.Stat(exe); err != nil {
		a.set(artController, StateBuilding, "Building")
	}
	if err := os.MkdirAll(filepath.Dir(exe), 0755); err != nil {
		a.set(artController, StateFailed, err.Error())
		return false
	}
	fresh := exe + ".new"
	cmd := exec.Command("go", "build", "-o", fresh, "./cmd/rimgovernor")
	cmd.Dir, cmd.Env, cmd.Stdout, cmd.Stderr = filepath.Join(a.repo, "go"), goEnv(), a, a
	if err := cmd.Run(); err != nil {
		a.set(artController, StateFailed, "Build failed; see Details")
		return false
	}
	if sameFile(fresh, exe) {
		os.Remove(fresh)
		a.set(artController, StateOK, "Up to date")
		return true
	}
	if _, err := os.Stat(exe); err == nil {
		old := exe + ".old"
		if os.Remove(old) != nil {
			// A controller still runs from the image an earlier rebuild
			// renamed aside; it is stale, so end it rather than failing.
			a.stopStaleController(old)
			os.Remove(old)
		}
		if err := os.Rename(exe, old); err != nil {
			a.set(artController, StateFailed, err.Error())
			return false
		}
		os.Remove(old)
	}
	if err := os.Rename(fresh, exe); err != nil {
		a.set(artController, StateFailed, err.Error())
		return false
	}
	a.logf("controller rebuilt")
	a.set(artController, StateOK, "Rebuilt")
	return true
}

// stopStaleController ends every process running from the renamed-aside
// image old, including the one this launcher recorded.
func (a *app) stopStaleController(old string) {
	if procs, err := setup.ListProcesses(filepath.Base(old)); err == nil {
		for _, p := range procs {
			if strings.EqualFold(filepath.Clean(p.Path), filepath.Clean(old)) {
				a.logf("stopping stale controller (pid %d) running from %s", p.PID, old)
				kill(p.PID)
			}
		}
	}
	if r, ok := readRecord(a.recordPath()); ok && strings.EqualFold(filepath.Clean(processPath(r.PID)), filepath.Clean(old)) {
		a.stop()
	}
}

func sameFile(a, b string) bool {
	hash := func(p string) []byte {
		f, err := os.Open(p)
		if err != nil {
			return nil
		}
		defer f.Close()
		h := sha256.New()
		if _, err := io.Copy(h, f); err != nil {
			return nil
		}
		return h.Sum(nil)
	}
	ha, hb := hash(a), hash(b)
	return ha != nil && bytes.Equal(ha, hb)
}
