package cases

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync/atomic"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// DefaultTimeout is the per-case safety net when Options names none.
const DefaultTimeout = 20 * time.Minute

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
	// Rimgovernor is the prebuilt rimgovernor binary (absolute path) a case
	// that launches `rimgovernor serve` runs; a bridge-only case ignores it.
	Rimgovernor string
}

// CaseOutput is where a case's evidence and result.json go.
func (o Options) CaseOutput(c Case) string {
	return filepath.Join(o.Output, filepath.FromSlash(c.Name))
}

// Execute runs one case end to end (prepare, open, start, quiet, freeze,
// Run, close) and writes its report; it returns the report and the process
// exit code Report.Finalize computed. A case that fails Lint is refused
// before the game opens; Finalize fails the run when it took longer than
// the budget (budget_ms) and stamps its timing; the report records under
// wait_stats how many of its waits stalled.
func Execute(ctx context.Context, c Case, opts Options) (na.Report, int) {
	output := opts.CaseOutput(c)
	report := na.NewReport(c.Scope, opts.Headless && !c.Rendered)
	report["case"] = c.Name
	na.ResetWaitStats()
	na.ResetTickStats()
	code := func() int {
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
	report.SetBudget(budget)
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
	s := &session{c: c, report: report, binary: opts.Rimgovernor}
	cfg := &na.Config{Root: opts.Root, Output: output, Headless: opts.Headless && !c.Rendered, GameID: opts.GameID,
		Spawned: func(pid int) { s.gabsPID.Store(int64(pid)) }}
	s.config = cfg
	report["keep"] = !c.NoKeep && na.KeepGame()
	if owned, ok := c.Start.(Owned); ok {
		// The case launches, drives and stops the process itself on this
		// profile (the expansions of the saves it names), so a game an
		// earlier case kept is stopped first: its profile may differ, and
		// a kept process cannot change expansions.
		if err := cfg.UseSaveExpansions(owned.Saves...); err != nil {
			return err
		}
		if err := cfg.PrepareConfig(); err != nil {
			return fmt.Errorf("prepare profile: %w", err)
		}
		report["start"] = owned.Describe()
		report["quiet_mode"] = c.Quiet.String()
		if err := na.StopGame(ctx, opts.Root, opts.GameID); err != nil {
			return fmt.Errorf("stop kept game before owned start: %w", err)
		}
		return c.Run(ctx, s)
	}
	if c.Rendered && opts.Headless {
		// A rendered case cannot run on the headless process the root
		// keeps; end it so the windowed profile launches its own.
		if err := na.StopGame(ctx, opts.Root, opts.GameID); err != nil {
			return fmt.Errorf("stop kept headless game before rendered start: %w", err)
		}
	}
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
	if c.NoKeep {
		// The case ends or replaces the process; nothing to keep.
		opened.Game.Keep = false
	}
	defer opened.Close()
	defer s.stopServices()
	report["quiet_mode"] = c.Quiet.String()
	s.Session = opened
	return c.Run(ctx, s)
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

// session is the runner's Session over the lifecycle library's, plus the
// services a case launched so close can stop them before the game closes.
type session struct {
	// Session is nil for an Owned case, which opens its own game on Config.
	*na.Session
	c        Case
	config   *na.Config
	report   na.Report
	binary   string
	services []*na.ServiceProcess
	runtime  *na.ScenarioRuntime
	gabsPID  atomic.Int64
}

func (s *session) Config() *na.Config       { return s.config }
func (s *session) GABSPID() int             { return int(s.gabsPID.Load()) }
func (s *session) Harness() *na.Harness     { return s.Session.Harness }
func (s *session) Names() []string          { return s.Session.Names }
func (s *session) Identity() map[string]any { return s.Session.Identity }
func (s *session) Prepared() map[string]any { return s.Session.Prepared }
func (s *session) Report() na.Report        { return s.report }
func (s *session) Release() error {
	if s.Session == nil {
		return errors.New("no game open: an Owned case holds its own session")
	}
	return s.Session.Release()
}

func (s *session) Launch(ctx context.Context, launch na.ServiceLaunch) (*na.ServiceProcess, error) {
	if launch.Binary == "" {
		launch.Binary = s.binary
	}
	if launch.Binary == "" {
		return nil, errors.New("the case launches rimgovernor serve: run it with -rimgovernor <absolute path to a prebuilt binary>")
	}
	if err := s.Release(); err != nil {
		return nil, err
	}
	service, err := na.LaunchService(ctx, s.config, s.Session.GABS, launch, s.report)
	if err != nil {
		return nil, err
	}
	s.services = append(s.services, service)
	return service, nil
}

func (s *session) Serve(ctx context.Context, spec na.ServeSpec) (*na.ServiceProcess, error) {
	if spec.Binary == "" {
		spec.Binary = s.binary
	}
	if spec.Binary == "" {
		return nil, errors.New("the case launches rimgovernor serve: run it with -rimgovernor <absolute path to a prebuilt binary>")
	}
	if s.Session == nil {
		return nil, errors.New("no game open: an Owned case holds its own session")
	}
	service, err := na.Serve(ctx, s.config, s.Session.Game, s.Session.Identity, spec, s.report)
	if err != nil {
		return nil, err
	}
	s.services = append(s.services, service)
	return service, nil
}

// stopServices stops any service still running (Stop is idempotent)
// before the session closes; na.Session.Close reattaches a released
// session on its own.
func (s *session) stopServices() {
	for _, service := range s.services {
		service.Stop()
	}
}

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
