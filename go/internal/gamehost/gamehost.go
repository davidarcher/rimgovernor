// Package gamehost launches the game with its GABP endpoint and records that
// endpoint so a later controller can reattach to the same game process. The
// game is spawned detached, so it outlives the controller that started it, and StateDir/<gameID>/
// endpoint.json names the process by pid plus start time so a reused pid is
// never mistaken for the game.
package gamehost

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"
)

// SchemaVersion is the endpoint.json layout version.
const SchemaVersion = 1

// stopBound caps how long Stop waits for the killed process to exit when
// ctx carries no earlier deadline.
const stopBound = 30 * time.Second

// ErrNotRunning reports that no live game backs the recorded endpoint.
var ErrNotRunning = errors.New("gamehost: game not running")

// ErrAlreadyRunning is matched (errors.Is) by *AlreadyRunningError.
var ErrAlreadyRunning = errors.New("gamehost: game already running")

// AlreadyRunningError refuses a Launch while a live endpoint exists for the
// game; the caller decides between Attach and Stop.
type AlreadyRunningError struct {
	GameID string
	PID    int
}

func (e *AlreadyRunningError) Error() string {
	return fmt.Sprintf("gamehost: game %q already running (pid %d)", e.GameID, e.PID)
}

func (e *AlreadyRunningError) Is(target error) bool { return target == ErrAlreadyRunning }

// Spec describes one game launch. Env entries override the inherited
// environment; GABP_SERVER_PORT, GABP_TOKEN and GABS_GAME_ID (the name
// the GABP host reads for its welcome agentId) are always set
// by Launch.
type Spec struct {
	GameID     string
	Executable string
	WorkingDir string
	Args       []string
	Env        map[string]string
	StateDir   string
}

// Endpoint is the endpoint.json record.
type Endpoint struct {
	SchemaVersion int       `json:"schemaVersion"`
	GameID        string    `json:"gameId"`
	PID           int       `json:"pid"`
	PIDStartTime  int64     `json:"pidStartTime"`
	Port          int       `json:"port"`
	Token         string    `json:"token"`
	LaunchedAt    time.Time `json:"launchedAt"`
	Generation    int64     `json:"generation"`
}

// Game is a launched or attached game process.
type Game struct {
	stateDir string
	ep       Endpoint

	doneOnce sync.Once
	done     chan struct{}

	// reaped is closed once the launcher has reaped the child and written
	// exit.json; nil for an attached game, which has no child handle.
	reaped chan struct{}
}

// Addr is the loopback GABP address, host:port.
func (g *Game) Addr() string { return net.JoinHostPort("127.0.0.1", strconv.Itoa(g.ep.Port)) }

// Token is the GABP auth token.
func (g *Game) Token() string { return g.ep.Token }

// PID is the game process id.
func (g *Game) PID() int { return g.ep.PID }

// Generation counts launches of this game id under StateDir.
func (g *Game) Generation() int64 { return g.ep.Generation }

// Endpoint returns a copy of the recorded endpoint.
func (g *Game) Endpoint() Endpoint { return g.ep }

// Alive reports whether the recorded process is still running with the
// recorded start time.
func (g *Game) Alive() bool {
	start, err := processStartTime(g.ep.PID)
	return err == nil && start == g.ep.PIDStartTime
}

// Done is closed once the game process has exited.
func (g *Game) Done() <-chan struct{} {
	g.doneOnce.Do(func() {
		go func() {
			for g.Alive() {
				time.Sleep(250 * time.Millisecond)
			}
			close(g.done)
		}()
	})
	return g.done
}

// Stop kills the game's process tree, waits (bounded by ctx and stopBound)
// for it to exit and removes endpoint.json. Stopping a game that already
// exited only removes the file.
func (g *Game) Stop(ctx context.Context) error {
	if g.Alive() {
		if err := killTree(g.ep.PID); err != nil && g.Alive() {
			return fmt.Errorf("gamehost: kill pid %d: %w", g.ep.PID, err)
		}
		ctx, cancel := context.WithTimeout(ctx, stopBound)
		defer cancel()
		for g.Alive() {
			select {
			case <-ctx.Done():
				return fmt.Errorf("gamehost: pid %d did not exit: %w", g.ep.PID, ctx.Err())
			case <-time.After(50 * time.Millisecond):
			}
		}
	}
	if g.reaped != nil {
		// The launcher's reaper writes exit.json after the process dies;
		// wait for it so nothing writes under the state dir after Stop.
		select {
		case <-g.reaped:
		case <-time.After(stopBound):
			return fmt.Errorf("gamehost: pid %d not reaped", g.ep.PID)
		}
	}
	return g.removeEndpoint()
}

// removeEndpoint deletes endpoint.json only while it still names this
// process, so a successor's record is never removed.
func (g *Game) removeEndpoint() error {
	cur, err := readEndpoint(g.stateDir, g.ep.GameID)
	if err != nil || cur.PID != g.ep.PID || cur.PIDStartTime != g.ep.PIDStartTime {
		return nil
	}
	if err := os.Remove(endpointPath(g.stateDir, g.ep.GameID)); err != nil && !os.IsNotExist(err) {
		return err
	}
	return nil
}

func gameDir(stateDir, gameID string) string { return filepath.Join(stateDir, gameID) }

func endpointPath(stateDir, gameID string) string {
	return filepath.Join(gameDir(stateDir, gameID), "endpoint.json")
}

func readEndpoint(stateDir, gameID string) (Endpoint, error) {
	var ep Endpoint
	data, err := os.ReadFile(endpointPath(stateDir, gameID))
	if err != nil {
		return ep, err
	}
	if err := json.Unmarshal(data, &ep); err != nil {
		return ep, fmt.Errorf("gamehost: parse %s: %w", endpointPath(stateDir, gameID), err)
	}
	return ep, nil
}

// Attach reattaches to the game recorded under stateDir. It returns
// ErrNotRunning, and removes the stale record, when no endpoint exists or
// its pid is gone or now names a different process.
func Attach(stateDir, gameID string) (*Game, error) {
	ep, err := readEndpoint(stateDir, gameID)
	if err != nil {
		if os.IsNotExist(err) {
			return nil, ErrNotRunning
		}
		_ = os.Remove(endpointPath(stateDir, gameID))
		return nil, fmt.Errorf("%w: %v", ErrNotRunning, err)
	}
	g := &Game{stateDir: stateDir, ep: ep, done: make(chan struct{})}
	if ep.SchemaVersion != SchemaVersion || ep.GameID != gameID || !g.Alive() {
		_ = os.Remove(endpointPath(stateDir, gameID))
		return nil, ErrNotRunning
	}
	return g, nil
}

// Launch starts the game detached and records its endpoint. It refuses with
// *AlreadyRunningError while a live endpoint exists for spec.GameID.
func Launch(ctx context.Context, spec Spec) (*Game, error) {
	if spec.GameID == "" || spec.Executable == "" || spec.StateDir == "" {
		return nil, errors.New("gamehost: Spec needs GameID, Executable and StateDir")
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if g, err := Attach(spec.StateDir, spec.GameID); err == nil {
		return nil, &AlreadyRunningError{GameID: spec.GameID, PID: g.PID()}
	}
	dir := gameDir(spec.StateDir, spec.GameID)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	generation, err := nextGeneration(dir)
	if err != nil {
		return nil, err
	}
	port, err := freeLoopbackPort()
	if err != nil {
		return nil, err
	}
	token, err := newToken()
	if err != nil {
		return nil, err
	}
	logFile, err := os.Create(filepath.Join(dir, "game.log"))
	if err != nil {
		return nil, err
	}
	defer logFile.Close()

	env := childEnv(os.Environ(), spec.Env, map[string]string{
		"GABP_SERVER_PORT": strconv.Itoa(port),
		"GABP_TOKEN":       token,
		"GABS_GAME_ID":     spec.GameID,
	})
	cmd := exec.Command(spec.Executable, spec.Args...)
	cmd.Dir = spec.WorkingDir
	cmd.Env = env
	cmd.Stdout = logFile
	cmd.Stderr = logFile
	cmd, err = startDetached(cmd)
	if err != nil {
		return nil, fmt.Errorf("gamehost: start %s: %w", spec.Executable, err)
	}
	pid := cmd.Process.Pid
	// Reap in the background so a child that dies early is not left a
	// zombie; the endpoint names the process by pid, not by this handle.
	// The exit code is recorded for LastExit: a player closing the game
	// exits 0, a crash or a kill does not.
	reaped := make(chan struct{})
	go func() {
		defer close(reaped)
		_ = cmd.Wait()
		code := -1
		if cmd.ProcessState != nil {
			code = cmd.ProcessState.ExitCode()
		}
		_ = writeJSONAtomic(exitPath(spec.StateDir, spec.GameID), exitRecord{Generation: generation, Code: code})
	}()

	start, err := processStartTime(pid)
	if err != nil {
		_ = killTree(pid)
		return nil, fmt.Errorf("gamehost: read start time of pid %d: %w", pid, err)
	}
	ep := Endpoint{
		SchemaVersion: SchemaVersion,
		GameID:        spec.GameID,
		PID:           pid,
		PIDStartTime:  start,
		Port:          port,
		Token:         token,
		LaunchedAt:    time.Now().UTC(),
		Generation:    generation,
	}
	if err := writeJSONAtomic(endpointPath(spec.StateDir, spec.GameID), ep); err != nil {
		_ = killTree(pid)
		return nil, err
	}
	return &Game{stateDir: spec.StateDir, ep: ep, done: make(chan struct{}), reaped: reaped}, nil
}

// nextGeneration bumps the per-game launch counter kept beside the endpoint
// (the endpoint itself is removed on Stop, so it cannot carry the count).
func nextGeneration(dir string) (int64, error) {
	path := filepath.Join(dir, "generation")
	var n int64
	if data, err := os.ReadFile(path); err == nil {
		n, _ = strconv.ParseInt(strings.TrimSpace(string(data)), 10, 64)
	} else if !os.IsNotExist(err) {
		return 0, err
	}
	n++
	if err := writeFileAtomic(path, []byte(strconv.FormatInt(n, 10))); err != nil {
		return 0, err
	}
	return n, nil
}

func freeLoopbackPort() (int, error) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return 0, fmt.Errorf("gamehost: pick port: %w", err)
	}
	defer l.Close()
	return l.Addr().(*net.TCPAddr).Port, nil
}

func newToken() (string, error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return hex.EncodeToString(b), nil
}

// childEnv is parent minus inherited GABS_*/GABP_*, then overrides, then
// managed. Names compare case-insensitively on Windows.
func childEnv(parent []string, overrides, managed map[string]string) []string {
	norm := func(k string) string {
		if runtime.GOOS == "windows" {
			return strings.ToUpper(k)
		}
		return k
	}
	var keys []string
	vals := map[string]string{}
	names := map[string]string{}
	set := func(k, v string) {
		n := norm(k)
		if _, ok := vals[n]; !ok {
			keys = append(keys, n)
		}
		vals[n] = v
		names[n] = k
	}
	for _, kv := range parent {
		k, v, ok := strings.Cut(kv, "=")
		if !ok || k == "" {
			continue
		}
		u := strings.ToUpper(k)
		if strings.HasPrefix(u, "GABS_") || strings.HasPrefix(u, "GABP_") {
			continue
		}
		set(k, v)
	}
	for k, v := range overrides {
		set(k, v)
	}
	for k, v := range managed {
		set(k, v)
	}
	if runtime.GOOS == "windows" {
		if _, ok := vals["SYSTEMROOT"]; !ok {
			set("SystemRoot", `C:\Windows`)
		}
	}
	env := make([]string, 0, len(keys))
	for _, n := range keys {
		env = append(env, names[n]+"="+vals[n])
	}
	return env
}

func writeJSONAtomic(path string, v any) error {
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	return writeFileAtomic(path, append(data, '\n'))
}

func writeFileAtomic(path string, data []byte) error {
	tmp, err := os.CreateTemp(filepath.Dir(path), filepath.Base(path)+".tmp*")
	if err != nil {
		return err
	}
	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		os.Remove(tmp.Name())
		return err
	}
	if err := tmp.Close(); err != nil {
		os.Remove(tmp.Name())
		return err
	}
	if err := os.Rename(tmp.Name(), path); err != nil {
		os.Remove(tmp.Name())
		return err
	}
	return nil
}

// exitRecord is how the launching process saw a launch end.
type exitRecord struct {
	Generation int64 `json:"generation"`
	Code       int   `json:"code"`
}

func exitPath(stateDir, gameID string) string {
	return filepath.Join(gameDir(stateDir, gameID), "exit.json")
}

// LastExit reports the exit code of the most recent launch of gameID. ok is
// false while that launch still runs, and when a process that has since
// gone launched it (only the launcher can read a child's exit code).
func LastExit(stateDir, gameID string) (code int, ok bool) {
	dir := gameDir(stateDir, gameID)
	data, err := os.ReadFile(filepath.Join(dir, "generation"))
	if err != nil {
		return 0, false
	}
	latest, err := strconv.ParseInt(strings.TrimSpace(string(data)), 10, 64)
	if err != nil {
		return 0, false
	}
	var rec exitRecord
	data, err = os.ReadFile(exitPath(stateDir, gameID))
	if err != nil || json.Unmarshal(data, &rec) != nil || rec.Generation != latest {
		return 0, false
	}
	return rec.Code, true
}
