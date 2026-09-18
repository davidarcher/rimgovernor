package cases

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// DefaultTimeout is the per-case safety net when Options names none.
const DefaultTimeout = 20 * time.Minute

// ErrBudget is the failure a run over its Budget is reported with.
var ErrBudget = errors.New("run exceeded its budget")

// Options is the per-invocation configuration shared by every case of a run.
type Options struct {
	// Root is the private disposable worker root (e.g. .rimgovernor/bridge).
	Root string
	// Output is the run's output directory; each case writes under
	// Output/<area>/<case>.
	Output   string
	GameID   string
	Headless bool
	// Timeout is the per-case safety net; Budget overrides every case's own
	// Budget when set; Stall overrides the shared stall budget when set.
	Timeout time.Duration
	Budget  time.Duration
	Stall   time.Duration
}

// CaseOutput is where a case's evidence and result.json go.
func (o Options) CaseOutput(c Case) string {
	return filepath.Join(o.Output, filepath.FromSlash(c.Name))
}

// Execute runs one case end to end (prepare, open, start, quiet, freeze,
// Run, close) and writes its report; it returns the report and the process
// exit code Report.Finalize computed. A case that fails Lint is refused
// before the game opens; the run fails when Run took longer than the
// budget, and records under wait_stats how many of its waits stalled.
func Execute(ctx context.Context, c Case, opts Options) (na.Report, int) {
	output := opts.CaseOutput(c)
	report := na.NewReport(c.Scope, opts.Headless)
	report["case"] = c.Name
	started := time.Now()
	report["started_at"] = started.UTC().Format(time.RFC3339Nano)
	na.ResetWaitStats()
	code := func() int {
		finished := time.Now()
		report["finished_at"] = finished.UTC().Format(time.RFC3339Nano)
		report["wall_ms"] = finished.Sub(started).Milliseconds()
		stats := na.WaitStats()
		report["wait_stats"] = stats
		if stalled, _ := stats["stalled"].(int); stalled > 0 {
			report["stalled_waits"] = stalled
		}
		return report.Finalize(output)
	}
	if err := os.MkdirAll(output, 0755); err != nil {
		report["error"] = err.Error()
		return report, code()
	}
	if entries, _ := os.ReadDir(output); len(entries) > 0 {
		report["error"] = fmt.Sprintf("%s is not empty: every run needs a fresh output directory", output)
		return report, code()
	}
	if err := c.Lint(); err != nil {
		report["error"] = err.Error()
		return report, code()
	}
	budget := c.Budget
	if opts.Budget > 0 {
		budget = opts.Budget
	}
	report["budget_ms"] = budget.Milliseconds()
	if opts.Stall > 0 {
		// The shared waits and the future Session read the stall budget
		// from the environment; a flag override is a per-process setting.
		_ = os.Setenv(na.StallEnv, opts.Stall.String())
	}
	report["stall_ms"] = na.StallBudget().Milliseconds()
	timeout := opts.Timeout
	if timeout <= 0 {
		timeout = DefaultTimeout
	}
	runCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	err := execute(runCtx, c, opts, output, report)
	if err == nil && time.Since(started) > budget {
		err = fmt.Errorf("%w: %s > %s", ErrBudget, time.Since(started).Round(time.Millisecond), budget)
	}
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	return report, code()
}

func execute(ctx context.Context, c Case, opts Options, output string, report na.Report) error {
	if c.Serve != nil {
		return fmt.Errorf("case %s declares Serve: the serve lifecycle is not wired into the runner yet (#138)", c.Name)
	}
	cfg := &na.Config{Root: opts.Root, Output: output, Headless: opts.Headless, GameID: opts.GameID}
	if save, ok := c.Start.(Save); ok {
		if err := cfg.UseSaveExpansions(save.Name); err != nil {
			return err
		}
	}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	report["start"] = resolvedStart(c.Start).Describe()
	report["quiet"] = c.Quiet.String()
	game, err := na.OpenGame(ctx, cfg)
	if err != nil {
		return err
	}
	defer game.Close(report)
	report["boot_ms"] = game.Open.Milliseconds()
	h := na.NewHarness(game.Client, output)
	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	report["discovery"] = names
	s := &session{c: c, game: game, harness: h, names: names, report: report}
	switch start := c.Start.(type) {
	case DebugStart:
		if _, err := na.StartDebugGameSized(ctx, h, names, c.Quiet, resolvedStart(start).(DebugStart).Size); err != nil {
			return err
		}
	case Fixture:
		if _, err := na.StartDebugGame(ctx, h, names, c.Quiet); err != nil {
			return err
		}
	case Save:
		if _, err := h.Call(ctx, "load-save", "rimworld/load_game_ready", map[string]any{
			"saveName": start.Name, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
		}); err != nil {
			return err
		}
	default:
		return fmt.Errorf("case %s: unknown Start %T", c.Name, c.Start)
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if fixture, ok := c.Start.(Fixture); ok {
		prepared, err := h.Call(ctx, "prepare", fixture.Op, fixture.Args)
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(prepared["success"]); !success {
			return fmt.Errorf("%s refused: %#v", fixture.Op, prepared)
		}
		report["prepared"] = prepared
		s.prepared = prepared
	}
	frozen, err := na.FreezeNeeds(ctx, h, names, c.Keep...)
	if err != nil {
		return err
	}
	report["frozen_needs"] = frozen
	if s.identity, err = readIdentity(ctx, h); err != nil {
		return err
	}
	report["identity"] = s.identity
	return c.Run(ctx, s)
}

// resolvedStart fills a DebugStart's zero Size with the default (or the
// environment's override) so the report names the start actually used.
func resolvedStart(start Start) Start {
	if d, ok := start.(DebugStart); ok && d.Size == (na.DebugStart{}) {
		return DebugStart{Size: na.DefaultDebugStart()}
	}
	return start
}

// readIdentity is the loaded colony's identity through the wire contract.
func readIdentity(ctx context.Context, h *na.Harness) (map[string]any, error) {
	reply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return nil, err
	}
	_, loaded, err := na.Outcome(reply, "loaded")
	if err != nil {
		return nil, err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, ok := na.AsMap(loadedContext["identity"])
	if !ok {
		return nil, fmt.Errorf("lifecycle_read_identity: no identity in %#v", loaded)
	}
	return identity, nil
}

// session is the runner's Session over today's na.Game.
type session struct {
	c        Case
	game     *na.Game
	harness  *na.Harness
	names    []string
	identity map[string]any
	prepared map[string]any
	report   na.Report
	runtime  *na.ScenarioRuntime
}

func (s *session) Harness() *na.Harness     { return s.harness }
func (s *session) Names() []string          { return s.names }
func (s *session) Identity() map[string]any { return s.identity }
func (s *session) Prepared() map[string]any { return s.prepared }
func (s *session) Report() na.Report        { return s.report }
func (s *session) Release() error           { return s.game.Release() }

// Runtime is the case's scenario runtime over a controller clock the
// runner acquires on first use; the discovered tools let AdvanceGame
// dismiss the letters it acknowledges (checklist item 4).
func (s *session) Runtime(ctx context.Context) (*na.ScenarioRuntime, error) {
	if s.runtime != nil {
		return s.runtime, nil
	}
	clock := &na.ScenarioClock{Wire: s.harness.WireFunc(), Identity: s.identity, Owner: na.Controller, Report: s.report}
	if _, err := clock.Acquire(ctx, "acquire"); err != nil {
		return nil, err
	}
	s.runtime = &na.ScenarioRuntime{Query: s.harness.Call, Clock: clock, Report: s.report, Tools: s.names}
	return s.runtime, nil
}

// Advance is na.AdvanceGame on Runtime with the case's Letters as the
// expected interruption letters when it declared any; the case's own
// options follow and may override them.
func (s *session) Advance(ctx context.Context, ticks uint64, opts ...na.AdvanceOption) (map[string]any, error) {
	rt, err := s.Runtime(ctx)
	if err != nil {
		return nil, err
	}
	if s.c.Letters != nil {
		opts = append([]na.AdvanceOption{na.WithExpectedLetters(s.c.Letters...)}, opts...)
	}
	return na.AdvanceGame(ctx, rt, ticks, opts...)
}
