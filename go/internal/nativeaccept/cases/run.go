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
	keep := make([]na.NeedDef, len(c.Keep))
	for i, need := range c.Keep {
		keep[i] = na.NeedDef(need)
	}
	// na.OpenSession is the shared preamble (#137): stale-package check,
	// profile, a kept process, discovery, the start, pause, the fixture op,
	// frozen needs and the identity, each on the report.
	opened, err := na.OpenSession(ctx, cfg, report, nativeStart(c.Start), c.Quiet, keep...)
	if err != nil {
		return err
	}
	defer opened.Close()
	report["quiet_mode"] = c.Quiet.String()
	return c.Run(ctx, &session{Session: opened, c: c})
}

// nativeStart is the case's Start as the lifecycle library's.
func nativeStart(start Start) na.Start {
	switch start := start.(type) {
	case DebugStart:
		return start.Size
	case Save:
		return na.Save{Name: start.Name}
	case Fixture:
		var on na.Start
		if start.On != nil {
			on = nativeStart(start.On)
		}
		return na.Fixture{Op: start.Op, Args: start.Args, On: on}
	}
	panic(fmt.Sprintf("unknown Start %T", start))
}

// session is the runner's Session over the lifecycle library's.
type session struct {
	*na.Session
	c       Case
	runtime *na.ScenarioRuntime
}

func (s *session) Config() *na.Config       { return s.Session.Config }
func (s *session) Harness() *na.Harness     { return s.Session.Harness }
func (s *session) Names() []string          { return s.Session.Names }
func (s *session) Identity() map[string]any { return s.Session.Identity }
func (s *session) Prepared() map[string]any { return s.Session.Prepared }
func (s *session) Report() na.Report        { return s.Session.Report }
func (s *session) Release() error           { return s.Session.Release() }

// Runtime is the case's scenario runtime over a controller clock the
// runner acquires on first use; the discovered tools let AdvanceGame
// dismiss the letters it acknowledges (checklist item 4).
func (s *session) Runtime(ctx context.Context) (*na.ScenarioRuntime, error) {
	if s.runtime != nil {
		return s.runtime, nil
	}
	clock := &na.ScenarioClock{Wire: s.Harness().WireFunc(), Identity: s.Identity(), Owner: na.Controller, Report: s.Report()}
	if _, err := clock.Acquire(ctx, "acquire"); err != nil {
		return nil, err
	}
	s.runtime = &na.ScenarioRuntime{Query: s.Harness().Call, Clock: clock, Report: s.Report(), Tools: s.Names()}
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
