//go:build windows

package main

import (
	"context"
	"fmt"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/setup"
	"golang.org/x/sys/windows"
)

func kill(pid int) {
	if p, err := os.FindProcess(pid); err == nil {
		p.Kill()
	}
	for i := 0; i < 40 && processPath(pid) != ""; i++ {
		time.Sleep(250 * time.Millisecond)
	}
}

func healthy(url string) bool {
	resp, err := (&http.Client{Timeout: time.Second}).Get(url + "/api/health")
	if err != nil {
		return false
	}
	resp.Body.Close()
	return resp.StatusCode == http.StatusOK
}

// ready is "" when Play can start, else what it waits on.
func (a *app) ready() string {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.busy[jobController] {
		return "Still rebuilding the controller"
	}
	for _, art := range a.artifacts {
		switch {
		case art.State == StateOK, art.State == StatePending:
		case art.State == StateFailed:
			return art.Name + ": " + art.Detail
		default:
			return "Still preparing " + strings.ToLower(art.Name)
		}
	}
	return ""
}

func (a *app) play() {
	// Serve only the finished rimgovernor.exe: a rebuild in flight would
	// otherwise leave the old binary running (#1132).
	for deadline := time.Now().Add(5 * time.Minute); a.isBusy(jobController) && time.Now().Before(deadline); {
		a.setController(ctrlStopped, "Waiting for the controller rebuild")
		time.Sleep(250 * time.Millisecond)
	}
	if why := a.ready(); why != "" {
		a.setController(ctrlStopped, why)
		return
	}
	a.mu.Lock()
	if a.ctrl != ctrlStopped {
		a.mu.Unlock()
		return
	}
	s := a.settings
	a.ctrl, a.message = ctrlStarting, "Starting the controller"
	a.mu.Unlock()
	if err := a.start(s); err != nil {
		a.logf("play: %v", err)
		a.setController(ctrlStopped, err.Error())
	}
}

func (a *app) start(s Settings) error {
	record, _ := readRecord(a.recordPath())
	port := firstPort
	owners, err := listeners(port)
	if err != nil {
		return fmt.Errorf("could not check port %d: %w", port, err)
	}
	stop, err := PortOwners(owners, a.repo, record.PID)
	for tries := 0; err != nil && tries < 20; tries++ {
		// Another checkout's controller holds the port: it and its game
		// copy are not ours to stop, so run beside it on the next port.
		a.logf("%v; trying port %d", err, port+1)
		port++
		if owners, err = listeners(port); err != nil {
			return fmt.Errorf("could not check port %d: %w", port, err)
		}
		stop, err = PortOwners(owners, a.repo, record.PID)
	}
	if err != nil {
		return err
	}
	a.mu.Lock()
	a.port = port
	a.mu.Unlock()
	for _, pid := range stop {
		a.logf("stopping the earlier controller on port %d (pid %d)", port, pid)
		kill(pid)
	}
	config := filepath.Dir(a.configPath())
	data, err := os.ReadFile(a.configPath())
	if err != nil {
		return fmt.Errorf("the game launch configuration is missing: %w", err)
	}
	game, err := ConfiguredGame(data)
	if err != nil {
		return err
	}
	goDir := filepath.Join(a.private, "go")
	now := time.Now()
	state, why := StatePath(goDir, s.ContinueState, now, LiveControl)
	a.logf("%s", why)
	paths := Paths{
		Profile: filepath.Join(a.layout.Root, "profile"),
		Config:  config,
		Game:    game,
		State:   state,
		Assets:  filepath.Join(a.dashboardDir(), "dist"),
	}
	args, err := ServeArgs(s, paths, port)
	if err != nil {
		return err
	}
	stamp := now.Format("20060102-150405")
	outPath, errPath := filepath.Join(goDir, "controller-"+stamp+".out.log"), filepath.Join(goDir, "controller-"+stamp+".err.log")
	stdout, err := os.Create(outPath)
	if err != nil {
		return err
	}
	defer stdout.Close()
	stderr, err := os.Create(errPath)
	if err != nil {
		return err
	}
	defer stderr.Close()
	cmd := exec.Command(a.controllerExe(), args...)
	cmd.Dir, cmd.Stdout, cmd.Stderr = a.repo, stdout, stderr
	// Its own hidden console (CREATE_NO_WINDOW) and process group: the
	// controller outlives the launcher, and the game it starts directly
	// outlives both.
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: 0x08000000 | syscall.CREATE_NEW_PROCESS_GROUP}
	a.logf("rimgovernor.exe %s", strings.Join(args, " "))
	if err := cmd.Start(); err != nil {
		return err
	}
	if err := writeRecord(a.recordPath(), Record{PID: cmd.Process.Pid, Exe: a.controllerExe(), Port: port}); err != nil {
		a.logf("record the controller: %v", err)
	}
	exited := make(chan struct{})
	a.mu.Lock()
	a.cmd = cmd
	a.mu.Unlock()
	go func() {
		err := cmd.Wait()
		close(exited)
		a.mu.Lock()
		if a.cmd == cmd {
			a.cmd = nil
			a.ctrl, a.message = ctrlStopped, "The controller exited unexpectedly. Details has the log path."
		}
		a.mu.Unlock()
		a.logf("controller exited (%v); log: %s", err, errPath)
	}()
	url := a.url(port)
	for deadline := time.Now().Add(60 * time.Second); time.Now().Before(deadline); time.Sleep(250 * time.Millisecond) {
		select {
		case <-exited:
			return fmt.Errorf("the controller exited while starting; see %s", errPath)
		default:
		}
		if healthy(url) {
			a.setController(ctrlRunning, "")
			if strings.HasPrefix(why, "continuing") {
				go a.reload(url, state, filepath.Join(paths.Profile, "Saves"))
			}
			return nil
		}
	}
	return fmt.Errorf("the controller did not answer within 60 s; see %s", errPath)
}

// reload puts the colony the continued state DB was running back in play
// (#1143): its newest save, found by the control record's colony id.
func (a *app) reload(url, state, saves string) {
	colony, err := ControlColony(state)
	if err != nil {
		a.logf("reload: read the control record: %v", err)
		return
	}
	save, err := LatestColonySave(saves, colony)
	if err != nil || save == "" {
		a.logf("reload: no save of colony %q in %s (%v)", colony, saves, err)
		return
	}
	a.logf("reload: loading %s", save)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	if err := ReloadSave(ctx, url, save); err != nil {
		a.logf("reload: %v", err)
		return
	}
	a.logf("reload: %s loaded", save)
}

// stop ends the controller this launcher started (never the game).
func (a *app) stop() {
	a.mu.Lock()
	cmd := a.cmd
	a.cmd = nil
	a.port = 0
	a.mu.Unlock()
	if cmd != nil {
		kill(cmd.Process.Pid)
	} else if r, ok := readRecord(a.recordPath()); ok {
		// A rebuild may have renamed the running image aside.
		if path := filepath.Clean(processPath(r.PID)); strings.EqualFold(path, r.Exe) || strings.EqualFold(path, r.Exe+".old") {
			kill(r.PID)
		}
	}
	os.Remove(a.recordPath())
	a.setController(ctrlStopped, "")
	a.logf("controller stopped")
}

func (a *app) restart() {
	a.stop()
	a.play()
}

func (a *app) closeGame() {
	pids, err := setup.StopGames(context.Background(), a.layout.GameCopy)
	switch {
	case err != nil:
		a.logf("close game: %v", err)
	case len(pids) > 0:
		a.logf("closed RimWorld (pid %v)", pids)
	}
	a.poll()
}

func (a *app) openDashboard() error {
	a.mu.Lock()
	url := a.url(a.activePort())
	a.mu.Unlock()
	return windows.ShellExecute(0, windows.StringToUTF16Ptr("open"), windows.StringToUTF16Ptr(url), nil, nil, windows.SW_SHOWNORMAL)
}

// recordedAlive reports whether the recorded controller still runs from
// its recorded exe (or the image a rebuild renamed aside) on port.
func (a *app) recordedAlive(port int) bool {
	r, ok := readRecord(a.recordPath())
	if !ok || r.Port != port {
		return false
	}
	path := filepath.Clean(processPath(r.PID))
	return strings.EqualFold(path, r.Exe) || strings.EqualFold(path, r.Exe+".old")
}

// activePort is where the controller listens: the port Play chose, else
// the recorded controller's, else firstPort. Callers hold a.mu.
func (a *app) activePort() int {
	if a.port != 0 {
		return a.port
	}
	if r, ok := readRecord(a.recordPath()); ok {
		return r.Port
	}
	return firstPort
}

// firstPort is where Play starts looking; a port held by another
// checkout's controller moves it up. Players never pick a port.
const firstPort = 8787

