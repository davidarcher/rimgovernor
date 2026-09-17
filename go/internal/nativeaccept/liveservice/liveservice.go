// Package liveservice runs the real Go player service against a loaded save
// for native acceptance harnesses that verify autopilot outcomes: it loads
// the save through a private bridge session, releases the sole GABP slot,
// launches `rimgovernor serve`, acquires player authority and keeps it
// granted, and hands the harness the service's HTTP API and durable store.
// The service can be stopped and started again on the same state path to
// exercise restart reconciliation, and the bridge session reopened once the
// service is down for native reads and the final games_stop.
package liveservice

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Config names the disposable root, the save and the service binary.
type Config struct {
	Root, Output, GameID string
	Headless             bool
	Binary               string
	Save                 string
	NativeTimeout        time.Duration
	ClockSpeed           string
	// Families is RIMGOVERNOR_ROUTINE_FAMILIES; empty runs the autonomous
	// default with every family on.
	Families string
	Prefix   string
	// Debug sets RIMGOVERNOR_CLOCK_DEBUG=1 so the service's stderr traces each
	// scheduler step and planner result.
	Debug bool
	// BeforeService, when set, runs against the loaded, paused save through
	// the private bridge session before it is released to the service: the
	// place for a harness's own in-game setup (a player edit the run then
	// has to live with). facts is the home/colony_facts reply.
	BeforeService func(ctx context.Context, h *na.Harness, identity, facts map[string]any) error
}

// Prepared is a loaded save whose bridge session has been released so the
// service can attach.
type Prepared struct {
	cfg       Config
	naCfg     *na.Config
	gabs      string
	Identity  map[string]any
	StatePath string
	starts    int
}

// Service is one running `rimgovernor serve` process with granted authority.
type Service struct {
	URL, Token string
	PID        int
	cmd        *exec.Cmd
	done       chan error
	stopped    bool
	keepAlive  *authorityKeepAlive
	stopKeep   context.CancelFunc
	keepWG     sync.WaitGroup
	httpClient *http.Client
	ctx        context.Context
	logs       []*os.File
}

// Prepare loads cfg.Save, dismisses the colony naming dialog, records the
// loaded identity and closes the bridge session without stopping the game.
func Prepare(ctx context.Context, cfg Config, report na.Report) (*Prepared, error) {
	if abs, err := filepath.Abs(cfg.Root); err == nil {
		cfg.Root = abs
	}
	if abs, err := filepath.Abs(cfg.Output); err == nil {
		cfg.Output = abs
	}
	if cfg.Prefix == "" {
		cfg.Prefix = "live-service"
	}
	if cfg.ClockSpeed == "" {
		cfg.ClockSpeed = "Normal"
	}
	if cfg.NativeTimeout == 0 {
		cfg.NativeTimeout = 15 * time.Second
	}
	naCfg := &na.Config{Root: cfg.Root, Output: cfg.Output, Headless: cfg.Headless, GameID: cfg.GameID}
	// The save carries its own expansion list; a Core-only profile would refuse it.
	if err := naCfg.UseSaveExpansions(cfg.Save); err != nil {
		return nil, fmt.Errorf("prepare profile: %w", err)
	}
	if err := naCfg.PrepareConfig(); err != nil {
		return nil, fmt.Errorf("prepare profile: %w", err)
	}
	game, err := naCfg.GameSection()
	if err != nil {
		return nil, err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return nil, err
	}
	report["package_files"] = files
	gabs, err := na.GABSExecutable(cfg.Root, naCfg.Configuration)
	if err != nil {
		return nil, err
	}
	p := &Prepared{cfg: cfg, naCfg: naCfg, gabs: gabs, StatePath: filepath.Join(cfg.Output, "service.sqlite")}
	client, h, err := p.Open(ctx)
	if err != nil {
		return nil, err
	}
	defer client.Close()
	if _, err := h.Call(ctx, "load-save", "rimworld/load_game_ready", map[string]any{
		"saveName": cfg.Save, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
	}); err != nil {
		return nil, err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}
	// ConfirmColonyNames is a priority-zero emergency that blocks every other
	// goal until the naming dialog is dismissed.
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return nil, err
	}
	if naming, ok := na.AsMap(facts["colonyNaming"]); ok && naming != nil {
		confirmed, err := h.Call(ctx, "confirm-colony-names", "home/confirm_colony_names", map[string]any{
			"windowId":       int(na.AsNumber(naming["windowId"])),
			"factionName":    na.AsString(naming["factionName"]),
			"settlementName": na.AsString(naming["settlementName"]),
			"dryRun":         false,
		})
		if err != nil {
			return nil, err
		}
		if success, _ := na.AsBool(confirmed["success"]); !success {
			return nil, fmt.Errorf("confirm_colony_names refused: %#v", confirmed)
		}
		report["confirmed_colony_names"] = confirmed
	} else {
		report["confirmed_colony_names"] = "no pending naming dialog"
	}
	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return nil, err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return nil, err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	p.Identity, _ = na.AsMap(loadedContext["identity"])
	if p.Identity == nil {
		return nil, fmt.Errorf("loaded identity missing: %#v", loaded)
	}
	report["identity"] = p.Identity
	if cfg.BeforeService != nil {
		if err := cfg.BeforeService(ctx, h, p.Identity, facts); err != nil {
			return nil, fmt.Errorf("before service: %w", err)
		}
	}
	return p, nil
}

// Open opens a private bridge session. Only one session can hold the GABP
// slot, so this is valid only while no service is running.
func (p *Prepared) Open(ctx context.Context) (*bridge.Client, *na.Harness, error) {
	deadline := time.Now().Add(30 * time.Second)
	for {
		c, err := na.OpenSession(ctx, p.gabs, p.naCfg.Configuration, p.cfg.GameID, 60*time.Second)
		if err == nil {
			return c, na.NewHarness(c, p.cfg.Output), nil
		}
		if time.Now().After(deadline) {
			return nil, nil, fmt.Errorf("open bridge session: %w", err)
		}
		select {
		case <-ctx.Done():
			return nil, nil, ctx.Err()
		case <-time.After(time.Second):
		}
	}
}

// SameIdentity reports whether v names the loaded colony, load and map.
func (p *Prepared) SameIdentity(v map[string]any) bool {
	return na.AsString(v["colonyId"]) == na.AsString(p.Identity["colonyId"]) &&
		na.AsString(v["loadToken"]) == na.AsString(p.Identity["loadToken"]) &&
		na.AsNumber(v["mapId"]) == na.AsNumber(p.Identity["mapId"])
}

// Start launches the service on the shared state path, waits for it to
// attach to the loaded save, anchors and acquires player authority and keeps
// it granted until Stop. Every start after the first is a restart against
// the same durable state.
func (p *Prepared) Start(ctx context.Context, report na.Report) (*Service, error) {
	p.starts++
	label := fmt.Sprintf("%s-%d", p.cfg.Prefix, p.starts)
	profileDir := filepath.Join(p.cfg.Output, "service-profile")
	serviceDir := filepath.Join(p.cfg.Output, fmt.Sprintf("service-%d", p.starts))
	for _, dir := range []string{profileDir, serviceDir} {
		if err := os.MkdirAll(dir, 0755); err != nil {
			return nil, err
		}
	}
	argv := []string{
		"serve", "--clock-speed", p.cfg.ClockSpeed,
		"--profile", profileDir,
		"--gabs", p.gabs,
		"--config", p.naCfg.Configuration,
		"--game", p.cfg.GameID,
		"--state", p.StatePath,
		"--listen", "127.0.0.1:0",
		"--refresh", "1s",
		"--timeout", p.cfg.NativeTimeout.String(),
	}
	report[label+"_argv"] = append([]string{p.cfg.Binary}, argv...)
	cmd := exec.CommandContext(ctx, p.cfg.Binary, argv...)
	cmd.Env = os.Environ()
	if p.cfg.Families != "" {
		cmd.Env = append(cmd.Env, "RIMGOVERNOR_ROUTINE_FAMILIES="+p.cfg.Families)
	}
	if p.cfg.Debug {
		cmd.Env = append(cmd.Env, "RIMGOVERNOR_CLOCK_DEBUG=1")
	}
	stdoutPipe, err := cmd.StdoutPipe()
	if err != nil {
		return nil, err
	}
	stderrFile, err := os.Create(filepath.Join(serviceDir, "stderr.log"))
	if err != nil {
		return nil, err
	}
	cmd.Stderr = stderrFile
	if err := cmd.Start(); err != nil {
		stderrFile.Close()
		return nil, fmt.Errorf("start rimgovernor serve: %w", err)
	}
	s := &Service{PID: cmd.Process.Pid, cmd: cmd, done: make(chan error, 1), httpClient: &http.Client{Timeout: 20 * time.Second}, ctx: ctx, logs: []*os.File{stderrFile}}
	go func() { s.done <- cmd.Wait() }()
	report[label+"_pid"] = s.PID
	reader := bufio.NewReader(stdoutPipe)
	firstLine, err := reader.ReadString('\n')
	if err != nil {
		s.Stop()
		return nil, fmt.Errorf("read service startup line: %w", err)
	}
	const startupPrefix = "RimGovernor Go player service: "
	firstLine = strings.TrimSpace(firstLine)
	if !strings.HasPrefix(firstLine, startupPrefix) {
		s.Stop()
		return nil, fmt.Errorf("unexpected service startup line: %q", firstLine)
	}
	s.URL = strings.TrimPrefix(firstLine, startupPrefix)
	report[label+"_url"] = s.URL
	stdoutLogFile, err := os.Create(filepath.Join(serviceDir, "stdout.log"))
	if err != nil {
		s.Stop()
		return nil, err
	}
	s.logs = append(s.logs, stdoutLogFile)
	go func() { _, _ = io.Copy(stdoutLogFile, reader) }()

	health, status, err := s.API("GET", "/api/health", nil)
	if err != nil {
		s.Stop()
		return nil, err
	}
	if status != 200 || na.AsString(health["service"]) != "rimgovernor" || na.AsString(health["backend"]) != "go" || int(na.AsNumber(health["pid"])) != s.PID {
		s.Stop()
		return nil, fmt.Errorf("unexpected /api/health: status=%d body=%#v", status, health)
	}
	session, status, err := s.API("GET", "/api/player/session", nil)
	if err != nil {
		s.Stop()
		return nil, err
	}
	if status != 200 || na.AsString(session["mode"]) != "explicit-player" || na.AsString(session["token"]) == "" {
		s.Stop()
		return nil, fmt.Errorf("unexpected /api/player/session: status=%d body=%#v", status, session)
	}
	s.Token = na.AsString(session["token"])
	deadline := time.Now().Add(90 * time.Second)
	for {
		state, status, err := s.API("GET", "/api/state", nil)
		if err != nil {
			s.Stop()
			return nil, err
		}
		if status != 200 {
			s.Stop()
			return nil, fmt.Errorf("unexpected /api/state status=%d body=%#v", status, state)
		}
		if connected, _ := na.AsBool(state["connected"]); connected {
			if svcIdentity, ok := na.AsMap(state["identity"]); ok && p.SameIdentity(svcIdentity) {
				report[label+"_attached"] = state
				break
			}
		}
		if time.Now().After(deadline) {
			s.Stop()
			return nil, fmt.Errorf("service did not attach to the loaded save's identity in time: %#v", state)
		}
		select {
		case <-ctx.Done():
			s.Stop()
			return nil, ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
	// Resume needs no anchor plan: authority is the world's own root plan,
	// created on first resume. Every start uses a fresh requestId because a
	// replayed one returns the earlier, already-lapsed record.
	acquired, status, err := s.API("POST", "/api/player/control/resume", map[string]any{
		"requestId": label + "-resume", "expected": p.Identity,
	})
	if err != nil {
		s.Stop()
		return nil, err
	}
	record, _ := na.AsMap(acquired["record"])
	if status != 200 || na.AsString(record["phase"]) != "running" {
		s.Stop()
		return nil, fmt.Errorf("resume was not running: status=%d %#v", status, acquired)
	}
	report[label+"_resumed"] = acquired
	s.keepAlive = &authorityKeepAlive{api: s.API, identity: p.Identity, prefix: label}
	keepCtx, stop := context.WithCancel(ctx)
	s.stopKeep = stop
	s.keepWG.Add(1)
	go func() { defer s.keepWG.Done(); s.keepAlive.run(keepCtx) }()
	return s, nil
}

// API calls the service's HTTP API with the player token.
func (s *Service) API(method, path string, body map[string]any) (map[string]any, int, error) {
	var reqBody io.Reader
	if body != nil {
		data, err := json.Marshal(body)
		if err != nil {
			return nil, 0, err
		}
		reqBody = bytes.NewReader(data)
	}
	req, err := http.NewRequestWithContext(s.ctx, method, s.URL+path, reqBody)
	if err != nil {
		return nil, 0, err
	}
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if s.Token != "" {
		req.Header.Set("X-RimGovernor-Player", s.Token)
	}
	req.Header.Set("Origin", s.URL)
	resp, err := s.httpClient.Do(req)
	if err != nil {
		return nil, 0, err
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, 0, err
	}
	var out map[string]any
	if len(data) > 0 {
		if err := json.Unmarshal(data, &out); err != nil {
			return nil, resp.StatusCode, fmt.Errorf("decode %s %s: %w", method, path, err)
		}
	}
	return out, resp.StatusCode, nil
}

// Stop ends authority keep-alive and kills the service process. It is safe
// to call more than once.
func (s *Service) Stop() map[string]any {
	if s.stopped {
		return nil
	}
	s.stopped = true
	var keep map[string]any
	if s.stopKeep != nil {
		s.stopKeep()
		s.keepWG.Wait()
		keep = s.keepAlive.snapshot()
	}
	if s.cmd.ProcessState == nil {
		_ = s.cmd.Process.Kill()
	}
	<-s.done
	for _, f := range s.logs {
		f.Close()
	}
	return keep
}

// OpenStore opens the service's durable journal for concurrent read-only
// verification; SQLite serves it alongside the running service.
func (p *Prepared) OpenStore(ctx context.Context) (*store.Store, error) {
	deadline := time.Now().Add(60 * time.Second)
	for {
		s, err := store.Open(ctx, p.StatePath)
		if err == nil {
			return s, nil
		}
		if time.Now().After(deadline) {
			return nil, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
}

// Finish stops the game through a fresh bridge session and checks the
// startup log. Call it with no service running.
func (p *Prepared) Finish(ctx context.Context, report na.Report) error {
	client, _, err := p.Open(ctx)
	if err != nil {
		return err
	}
	defer client.Close()
	stopCtx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	if stopped, err := client.GamesStop(stopCtx); err == nil {
		report["stop"] = string(stopped.Envelope)
	} else {
		report["stop_error"] = err.Error()
	}
	logData, err := os.ReadFile(p.naCfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), p.cfg.Headless)
}

// authorityKeepAlive re-acquires lapsed authority and acknowledges clock
// holds so a multi-minute observation window keeps the service automating.
type authorityKeepAlive struct {
	api      func(method, path string, body map[string]any) (map[string]any, int, error)
	identity map[string]any
	prefix   string

	mu                sync.Mutex
	attempts          int
	reacquired        int
	acknowledged      int
	acknowledgeFailed int
	lastError         string
}

func (k *authorityKeepAlive) snapshot() map[string]any {
	k.mu.Lock()
	defer k.mu.Unlock()
	out := map[string]any{
		"attempts": k.attempts, "reacquired": k.reacquired,
		"acknowledged": k.acknowledged, "acknowledge_failed": k.acknowledgeFailed,
	}
	if k.lastError != "" {
		out["last_error"] = k.lastError
	}
	return out
}

func (k *authorityKeepAlive) note(err string) {
	k.mu.Lock()
	k.lastError = err
	k.mu.Unlock()
}

func (k *authorityKeepAlive) run(ctx context.Context) {
	for {
		select {
		case <-ctx.Done():
			return
		case <-time.After(2 * time.Second):
		}
		state, status, err := k.api("GET", "/api/state", nil)
		if err != nil || status != 200 {
			continue
		}
		if na.AsString(state["mode"]) == "automate" {
			continue
		}
		if clk, clkStatus, clkErr := k.api("GET", "/api/player/clock", nil); clkErr == nil && clkStatus == 200 {
			if holds := na.AsSlice(clk["holds"]); len(holds) > 0 {
				ackBody := map[string]any{
					"requestId":        fmt.Sprintf("%s-ack-%d", k.prefix, time.Now().UnixNano()),
					"expectedRevision": na.AsString(clk["revision"]),
					"throughCursor":    na.AsString(clk["inboxCursor"]),
				}
				if _, ackStatus, ackErr := k.api("POST", "/api/player/clock/acknowledge", ackBody); ackErr != nil || ackStatus != 200 {
					k.mu.Lock()
					if ackErr != nil {
						k.lastError = "acknowledge: " + ackErr.Error()
					} else {
						k.lastError = fmt.Sprintf("acknowledge status=%d", ackStatus)
					}
					k.acknowledgeFailed++
					k.mu.Unlock()
				} else {
					k.mu.Lock()
					k.acknowledged++
					k.mu.Unlock()
				}
			}
		}
		k.mu.Lock()
		k.attempts++
		k.mu.Unlock()
		acquired, status, err := k.api("POST", "/api/player/control/resume", map[string]any{
			"requestId": fmt.Sprintf("%s-reacquire-%d", k.prefix, time.Now().UnixNano()),
			"expected":  k.identity,
		})
		if err != nil {
			k.note(err.Error())
			continue
		}
		record, _ := na.AsMap(acquired["record"])
		if status != 200 {
			k.note(fmt.Sprintf("reacquire status=%d body=%#v", status, acquired))
			continue
		}
		if na.AsString(record["phase"]) != "running" {
			k.note(fmt.Sprintf("reacquire not running: %#v", acquired))
			continue
		}
		k.mu.Lock()
		k.reacquired++
		k.lastError = ""
		k.mu.Unlock()
	}
}
