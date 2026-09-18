// headless-soak (the
// former headlesssoakaccept) is a diagnostic for "GABS's native tool
// catalog emptied mid-run" (games_call_tool failing with
// "availableTotal: 0"). It was written to chase a suspected
// headless-RimWorld instability while building foodstorageaccept; the
// captured GABS debug log showed the real cause was external -- another
// worktree's session running `taskkill /IM RimWorldWin64.exe` as "stray
// process" cleanup, which kills every session's game (GABS logs
// "unexpected GABP disconnect ... forcibly closed by the remote host"
// followed by "pid N not found"). Keep it for the next time a game
// vanishes: gabs-stderr.log says whether the process died or the bridge
// merely disconnected.
//
// It starts a fresh debug game, then drives the clock with one of several
// call patterns (ModeEnv) while recording a timeline of GABS-side
// games_status (status/toolCount, never touching the game), the RimWorld
// process's memory, and the game tick. GABS's own stderr is captured at
// debug level so the moment and reason the game connection drops is
// visible.

package lifecycle

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// ModeEnv and DurationEnv override the registered soak's call pattern and
// length for a diagnostic chase.
const (
	ModeEnv     = "RIMGOVERNOR_ACCEPT_SOAK_MODE"
	DurationEnv = "RIMGOVERNOR_ACCEPT_SOAK_DURATION"
)

func init() {
	cases.Register(cases.Case{
		Name:   "lifecycle/headless-soak",
		Scope:  "Diagnostic soak: fresh debug game driven by the soak mode (poll by default) while recording GABS status, process memory and tick; captures GABS debug stderr.",
		Start:  cases.Owned{},
		Reason: "the diagnostic opens its own GABS to capture its stderr at debug level and stops the game it soaked",
		NoKeep: true,
		Budget: 6 * time.Minute,
		Run:    runSoak,
	})
}

// soakConfig is the soak's shape; DurationEnv and ModeEnv override the
// registered defaults for a diagnostic run.
type soakConfig struct {
	mode, speed, gabsLog  string
	ultra                 bool
	duration, poll, chunk time.Duration
	stepTicks             int
}

// soakDefaults is the registered soak: two minutes of Superfast polling
// with GABS at debug level, inside a minute-scale budget. A diagnostic
// chase sets DurationEnv (e.g. 12m) and ModeEnv (poll | idle |
// pausedpoll | playfor | stepticks).
func soakDefaults() (soakConfig, error) {
	cfg := soakConfig{mode: "poll", speed: "Superfast", gabsLog: "debug", ultra: true,
		duration: 2 * time.Minute, poll: 15 * time.Second, chunk: 60 * time.Second, stepTicks: 2500}
	if mode := os.Getenv(ModeEnv); mode != "" {
		cfg.mode = mode
	}
	if raw := os.Getenv(DurationEnv); raw != "" {
		d, err := time.ParseDuration(raw)
		if err != nil {
			return cfg, fmt.Errorf("%s: %w", DurationEnv, err)
		}
		cfg.duration = d
	}
	return cfg, nil
}

type timeline struct {
	w     *bufio.Writer
	f     *os.File
	start time.Time
}

func openTimeline(path string) (*timeline, error) {
	f, err := os.Create(path)
	if err != nil {
		return nil, err
	}
	return &timeline{w: bufio.NewWriter(f), f: f, start: time.Now()}, nil
}

func (t *timeline) row(kind string, fields map[string]any) {
	fields["kind"] = kind
	fields["at"] = time.Now().Format(time.RFC3339Nano)
	fields["elapsed_s"] = int(time.Since(t.start).Seconds())
	data, _ := json.Marshal(fields)
	t.w.Write(data)
	t.w.WriteByte('\n')
	t.w.Flush()
	fmt.Fprintln(os.Stderr, string(data))
}

func (t *timeline) close() { t.w.Flush(); t.f.Close() }

// processSample reads RimWorldWin64.exe memory from tasklist; it is diagnostic
// only and never fails the run.
func processSample() map[string]any {
	out, err := exec.Command("tasklist", "/FI", "IMAGENAME eq RimWorldWin64.exe", "/FO", "CSV", "/NH").Output()
	if err != nil {
		return map[string]any{"error": err.Error()}
	}
	lines := strings.Split(strings.TrimSpace(string(out)), "\n")
	var rows []string
	for _, l := range lines {
		if strings.HasPrefix(l, "\"RimWorldWin64.exe\"") {
			rows = append(rows, strings.TrimSpace(l))
		}
	}
	return map[string]any{"count": len(rows), "rows": rows}
}

func gabsStatus(ctx context.Context, client *bridge.Client) map[string]any {
	res, err := client.GameStatus(ctx)
	if err != nil {
		return map[string]any{"error": err.Error()}
	}
	var s map[string]any
	_ = json.Unmarshal(res.Structured, &s)
	keep := map[string]any{}
	for _, k := range []string{"status", "toolCount", "connected", "gabpConnected", "running", "pid", "ownership", "runtime", "attention", "processState", "bridge"} {
		if v, ok := s[k]; ok {
			keep[k] = v
		}
	}
	return keep
}

// runSoak owns the process (an Owned start): it opens GABS itself so its
// stderr is captured at debug level, starts the game, drives the clock and
// stops the game at the end.
func runSoak(ctx context.Context, s cases.Session) error {
	naCfg, report := s.Config(), s.Report()
	cfg, err := soakDefaults()
	if err != nil {
		return err
	}
	report["mode"] = cfg.mode
	report["duration_ms"] = cfg.duration.Milliseconds()
	output := naCfg.Output
	gabsExecutable, err := na.GABSExecutable(naCfg.Root, naCfg.Configuration)
	if err != nil {
		return err
	}
	stderrFile, err := os.Create(filepath.Join(output, "gabs-stderr.log"))
	if err != nil {
		return err
	}
	defer stderrFile.Close()
	client, err := bridge.Open(ctx, bridge.ProcessConfig{
		Executable: gabsExecutable, ConfigDir: naCfg.Configuration, GameID: naCfg.GameID,
		Timeout: 60 * time.Second, LogLevel: cfg.gabsLog, Stderr: stderrFile,
	})
	if err != nil {
		return fmt.Errorf("open GABS session: %w", err)
	}
	started, err := client.GamesStart(ctx)
	if err != nil {
		_ = client.Close()
		return fmt.Errorf("games_start: %w", err)
	}
	if _, err := client.ConnectWithPoll(ctx, started); err != nil {
		_ = client.Close()
		return fmt.Errorf("connect: %w", err)
	}
	defer func() {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer stopCancel()
		if stopped, err := client.GamesStop(stopCtx); err == nil {
			report["stop"] = string(stopped.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
		_ = client.Close()
	}()
	h := na.NewHarness(client, output)
	tl, err := openTimeline(filepath.Join(output, "timeline.jsonl"))
	if err != nil {
		return err
	}
	defer tl.close()

	// Record the undocumented tool schemas up front so they are on disk even
	// if the run dies.
	schemas := map[string]any{}
	for _, name := range []string{"rimworld/play_for", "rimworld/step_game_ticks", "rimworld/set_time_speed", "rimbridge/list_logs", "rimbridge/get_bridge_status", "home/status"} {
		res, err := client.Describe(ctx, name)
		if err != nil {
			schemas[name] = map[string]any{"error": err.Error()}
			continue
		}
		schemas[name] = json.RawMessage(res.Structured)
	}
	if data, err := json.MarshalIndent(schemas, "", "  "); err == nil {
		_ = os.WriteFile(filepath.Join(output, "tool-schemas.json"), data, 0644)
	}

	tl.row("gabs-status", map[string]any{"gabs": gabsStatus(ctx, client), "proc": processSample()})
	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietIfAvailable); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	tl.row("game-ready", map[string]any{"gabs": gabsStatus(ctx, client), "proc": processSample()})

	tick := func(label string) (float64, error) {
		status, err := h.Call(ctx, label, "home/status", map[string]any{"colonists": false, "threats": false})
		if err != nil {
			return 0, err
		}
		t, _ := na.AsMap(status["time"])
		return na.AsNumber(t["ticksGame"]), nil
	}
	sample := func(label string, nativeErr error, extra map[string]any) {
		fields := map[string]any{"label": label, "gabs": gabsStatus(ctx, client), "proc": processSample()}
		if nativeErr != nil {
			fields["native_error"] = nativeErr.Error()
		}
		for k, v := range extra {
			fields[k] = v
		}
		tl.row("sample", fields)
	}
	// onFailure gathers everything observable once the catalog has gone.
	onFailure := func(label string, cause error) error {
		tl.row("failure", map[string]any{"label": label, "error": cause.Error()})
		names, nerr := client.NativeNames(ctx, "", "")
		post := map[string]any{"gabs": gabsStatus(ctx, client), "proc": processSample()}
		if nerr != nil {
			post["tool_names_error"] = nerr.Error()
		} else {
			post["tool_names"] = json.RawMessage(names.Structured)
		}
		if out, err := exec.Command("netstat", "-ano", "-p", "tcp").Output(); err == nil {
			var keep []string
			for _, l := range strings.Split(string(out), "\n") {
				if strings.Contains(l, "127.0.0.1") {
					keep = append(keep, strings.TrimSpace(l))
				}
			}
			post["netstat_loopback"] = keep
		}
		// Does GABS think it can reconnect? Try once after a short wait.
		time.Sleep(5 * time.Second)
		if res, err := client.ConnectGame(ctx); err != nil {
			post["reconnect_error"] = err.Error()
			if len(res.Envelope) > 0 {
				post["reconnect_envelope"] = json.RawMessage(res.Envelope)
			}
		} else {
			post["reconnect"] = json.RawMessage(res.Structured)
		}
		post["gabs_after_reconnect"] = gabsStatus(ctx, client)
		if _, err := tick("post-failure-tick"); err != nil {
			post["post_failure_tick_error"] = err.Error()
		} else {
			post["post_failure_tick_ok"] = true
		}
		tl.row("post-failure", post)
		report["failure"] = cause.Error()
		report["failure_label"] = label
		return fmt.Errorf("%s: %w", label, cause)
	}

	start, err := tick("tick-start")
	if err != nil {
		return err
	}
	report["tick_start"] = start
	deadline := time.Now().Add(cfg.duration)
	var last float64 = start
	i := 0
	switch cfg.mode {
	case "poll", "idle", "pausedpoll":
		if cfg.mode != "pausedpoll" {
			if _, err := h.Call(ctx, "resume", "rimworld/set_time_speed", map[string]any{"speed": cfg.speed, "ultraSpeedBoost": cfg.ultra}); err != nil {
				return err
			}
		}
		for time.Now().Before(deadline) {
			time.Sleep(cfg.poll)
			i++
			label := fmt.Sprintf("poll-%03d", i)
			if cfg.mode == "idle" {
				sample(label, nil, nil)
				continue
			}
			t, err := tick(label)
			if err != nil {
				sample(label, err, nil)
				return onFailure(label, err)
			}
			last = t
			sample(label, nil, map[string]any{"tick": t})
		}
		if cfg.mode == "idle" {
			t, err := tick("idle-final-tick")
			if err != nil {
				return onFailure("idle-final-tick", err)
			}
			last = t
		}
	case "playfor":
		for time.Now().Before(deadline) {
			i++
			label := fmt.Sprintf("playfor-%03d", i)
			args := map[string]any{"speed": cfg.speed, "durationMs": cfg.chunk.Milliseconds()}
			res, err := h.Call(ctx, label, "rimworld/play_for", args)
			if err != nil {
				sample(label, err, nil)
				return onFailure(label, err)
			}
			t, terr := tick(label + "-tick")
			if terr != nil {
				sample(label, terr, nil)
				return onFailure(label+"-tick", terr)
			}
			last = t
			sample(label, nil, map[string]any{"tick": t, "play_for_keys": keysOf(res)})
		}
	case "stepticks":
		for time.Now().Before(deadline) {
			i++
			label := fmt.Sprintf("step-%03d", i)
			if _, err := h.Call(ctx, label, "rimworld/step_game_ticks", map[string]any{"ticks": cfg.stepTicks}); err != nil {
				sample(label, err, nil)
				return onFailure(label, err)
			}
			t, terr := tick(label + "-tick")
			if terr != nil {
				sample(label, terr, nil)
				return onFailure(label+"-tick", terr)
			}
			last = t
			sample(label, nil, map[string]any{"tick": t})
		}
	default:
		return fmt.Errorf("unknown mode %q", cfg.mode)
	}
	if _, err := h.Call(ctx, "re-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return onFailure("re-pause", err)
	}
	if logs, err := h.Call(ctx, "logs", "rimbridge/list_logs", map[string]any{}); err == nil {
		report["log_keys"] = keysOf(logs)
	}
	report["tick_end"] = last
	report["ticks_advanced"] = last - start
	report["samples"] = i
	return nil
}

func keysOf(m map[string]any) []string {
	var out []string
	for k := range m {
		out = append(out, k)
	}
	return out
}
