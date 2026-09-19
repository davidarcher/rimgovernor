package cases

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/postmortem"
)

// DefaultTimeout is the per-case safety net when Options names none.
const DefaultTimeout = 20 * time.Minute

// initialStallEnv is na.StallEnv as the process started, restored before a
// case that declares no Stall of its own.
var initialStallEnv = os.Getenv(na.StallEnv)

// timeoutMargin is how far past a case's Budget the safety net sits when
// the Budget alone would exceed it.
const timeoutMargin = 5 * time.Minute

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
	// Evidence is the evidence mode (na.EvidenceCapped, na.EvidenceFull);
	// empty leaves na.CurrentEvidenceMode's default.
	Evidence na.EvidenceMode
	// CheckpointEvery is the checkpoint ring's cadence in run phase (#249);
	// zero turns the ring, and resuming, off.
	CheckpointEvery time.Duration
	// Fresh discards the case's ring in this root and starts from scratch;
	// Rewind resumes that many entries earlier than the ring's next.
	Fresh  bool
	Rewind int
	// Restage discards the case's stage bundles in this root (#329) and
	// stages again; Fresh leaves them, since a stage is deterministic
	// setup, not the failed attempt.
	Restage bool
	// PostmortemOnly runs only the case's Postmortem phase over a staged
	// bundle (#275): From names the bundle (a ring label such as "t+7m" or
	// "failed", or a bundle directory), the ring's failed bundle when
	// empty. The ring is read, never changed.
	PostmortemOnly bool
	From           string
	// Dev is the `acceptance dev` iteration (#274): the case runs as a
	// resumed run over the bundle From names (a ring label or a bundle
	// directory; the ring's next entry, then its failed bundle, by
	// default), Run and Postmortem both, with the ring read but never
	// written, no stage captures and no series row. Attempt numbers the
	// iteration: output goes under Output/dev/<n>/<case> and the request
	// ids carry dev<n>.
	Dev bool
	// Seed pins the world seed of a debug or scenario start (#281): the
	// seed a result.json's world block recorded reproduces that run's
	// world. A case that starts from a save has no seed to pin and is
	// refused. Implies Fresh.
	Seed string
	// Repeat runs each case this many times on the kept process (#281)
	// and reports the pass rate and per-attempt seeds (cmd/acceptance
	// runRepeat); 0 or 1 is one run. Every attempt is fresh with the
	// checkpoint ring off, since a resumed attempt measures nothing.
	Repeat int
	// Attempt numbers the run under Repeat: 1 (or 0) writes the case's
	// usual output directory, later attempts write under
	// Output/repeat/<n>/<case> so each keeps its own evidence.
	Attempt int
	// Log receives the run's progress lines (the resume decision); nil
	// discards them.
	Log io.Writer
	// Series is the append-only metrics series every case's block is
	// appended to (na.SeriesPath(Output) by default); NoSeries leaves the
	// series alone.
	Series   string
	NoSeries bool
	// NoDoctor skips the runner's doctor preflight (a suite worker whose
	// parent already ran it on the shared root).
	NoDoctor bool
	// NoHeal makes the preflight refuse a stale or fixture-less install
	// instead of rebuilding and reinstalling the mod (#276): a landing run
	// never silently rebuilds.
	NoHeal bool
	// Healed lists what the preflight healed before this run (the doctor's
	// heal codes and "relaunched" when it stopped the root's kept game);
	// the report carries it under "healed" so a slow first run is
	// explained.
	Healed []string
}

// SeriesPath is where the run's series lives: Series, or the default
// beside Output.
func (o Options) SeriesPath() string {
	if o.Series != "" {
		return o.Series
	}
	return na.SeriesPath(o.Output)
}

// RunID names the run in the series: its output directory's base name.
func (o Options) RunID() string { return filepath.Base(o.Output) }

// CaseOutput is where a case's evidence and result.json go.
func (o Options) CaseOutput(c Case) string {
	if o.Dev {
		return filepath.Join(o.Output, "dev", fmt.Sprint(max(o.Attempt, 1)), filepath.FromSlash(c.Name))
	}
	if o.Attempt > 1 {
		return filepath.Join(o.Output, "repeat", fmt.Sprint(o.Attempt), filepath.FromSlash(c.Name))
	}
	return filepath.Join(o.Output, filepath.FromSlash(c.Name))
}

// Execute runs one case end to end (prepare, open, start, quiet, freeze,
// Run, close) and writes its report; it returns the report and the process
// exit code Report.Finalize computed. A case that fails Lint is refused
// before the game opens; Finalize fails the run when it took longer than
// the budget (budget_ms) and stamps its timing and metrics block; the
// report records under wait_stats how many of its waits stalled and under
// game_log the slice of the game's own log the case wrote, copied to
// <output>/game.log (na.GameLogCapture). The block is appended to the
// run's series (Options.SeriesPath) and the report lists under "drift"
// the metrics past their rule against the series' trailing median
// (na.Drift); neither changes the verdict.
func Execute(ctx context.Context, c Case, opts Options) (na.Report, int) {
	output := opts.CaseOutput(c)
	if opts.Dev {
		// An iteration over a pinned bundle is not a measurement of the case.
		opts.NoSeries = true
	}
	headless := opts.Headless && !c.Rendered
	report := na.NewReport(c.Scope, headless)
	report["case"] = c.Name
	if len(opts.Healed) > 0 {
		report["healed"] = opts.Healed
	}
	na.ResetWaitStats()
	na.ResetTickStats()
	gameLog := na.OpenGameLog((&na.Config{Root: opts.Root, Headless: headless}).StartupLogPath())
	// The log copy lands only in an output directory this run owns.
	opened := false
	code := func() int {
		report["wait_stats"] = na.WaitStats()
		if opened {
			gameLog.Close(output, report)
		}
		exit := report.Finalize(output)
		if !opts.NoSeries {
			na.RecordSeries(opts.SeriesPath(), c.Name, opts.RunID(), report)
			report.Write(output)
		}
		return exit
	}
	if err := os.MkdirAll(output, 0755); err != nil {
		report["error"] = err.Error()
		return report, code()
	}
	if entries, _ := os.ReadDir(output); len(entries) > 0 {
		report["error"] = fmt.Sprintf("%s is not empty: every run needs a fresh output directory", output)
		return report, code()
	}
	opened = true
	if err := c.Lint(); err != nil {
		report["error"] = err.Error()
		return report, code()
	}
	budget := c.Budget
	if opts.Budget > 0 {
		budget = opts.Budget
	}
	report.SetBudget(budget)
	// The shared waits and the future Session read the stall budget from
	// the environment; a flag override or the case's own Stall is set for
	// this case and the process's original value put back for the next.
	stall := opts.Stall
	if stall <= 0 {
		stall = c.Stall
	}
	if stall > 0 {
		_ = os.Setenv(na.StallEnv, stall.String())
	} else {
		_ = os.Setenv(na.StallEnv, initialStallEnv)
	}
	report["stall_ms"] = na.StallBudget().Milliseconds()
	if err := na.SetEvidenceMode(opts.Evidence); err != nil {
		report["error"] = err.Error()
		return report, code()
	}
	report["evidence"] = string(na.CurrentEvidenceMode())
	timeout := opts.Timeout
	if timeout <= 0 {
		timeout = DefaultTimeout
	}
	if timeout < budget+timeoutMargin {
		// A case whose budget outruns the safety net still fails on its
		// budget, not on a context cut a few minutes short of it.
		timeout = budget + timeoutMargin
	}
	runCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	err := execute(runCtx, c, opts, output, report)
	if err != nil {
		report["error"] = err.Error()
		diagnose(ctx, output, report)
	} else {
		report["passed"] = true
	}
	return report, code()
}

// diagnose writes the postmortem digest of a failed case (#278) onto the
// report ("diagnosis", which Finalize emits first) and to
// output/diagnosis.txt, read from the evidence the run left behind. The
// digest is collected under the caller's context, not the run's, which a
// timeout may already have cut.
func diagnose(ctx context.Context, output string, report na.Report) {
	digest := postmortem.Collect(ctx, output, report)
	report["diagnosis"] = digest
	_ = os.WriteFile(filepath.Join(output, "diagnosis.txt"), []byte(digest.Text()), 0644)
}

func execute(ctx context.Context, c Case, opts Options, output string, report na.Report) error {
	if opts.PostmortemOnly {
		return executePostmortem(ctx, c, opts, output, report)
	}
	if c.Serve != nil && opts.Rimgovernor == "" {
		return fmt.Errorf("case %s launches rimgovernor serve: run it with -rimgovernor <absolute path to a prebuilt binary>", c.Name)
	}
	if err := StageSaves(c.Start, opts.Root); err != nil {
		return err
	}
	log := opts.Log
	if log == nil {
		log = io.Discard
	}
	if opts.Seed != "" {
		// A reproduction runs the recorded world from scratch; a resumed
		// checkpoint would be some other run's world.
		opts.Fresh = true
	}
	var resumed resumption
	var err error
	if opts.Dev {
		resumed, err = planDev(c, opts, log)
		if err != nil {
			return err
		}
		// The bundle is the iteration's fixed starting point: nothing this
		// run does is recorded against it.
		opts.CheckpointEvery = 0
		report["dev"] = true
	} else {
		resumed, err = planResume(c, opts, log)
		if err != nil {
			return fmt.Errorf("checkpoint ring: %w", err)
		}
	}
	staged, err := planStage(c, opts, resumed, log)
	if err != nil {
		return fmt.Errorf("stage bundles: %w", err)
	}
	if opts.Dev && staged.off == "" {
		staged.off = "dev iteration"
	}
	s := &session{c: c, report: report, binary: opts.Rimgovernor, seed: opts.Seed, stagePlan: staged, stagesDir: opts.StagesDir(c)}
	if resumed.resuming() || staged.staged() {
		// The restored store holds the earlier run's submissions (#307).
		s.resumeSuffix = opts.RunID()
	}
	if opts.Dev {
		s.resumeSuffix = fmt.Sprintf("dev%d", max(opts.Attempt, 1))
	}
	if resumed.resuming() {
		s.resumed = &resumed.entry
	}
	cfg := &na.Config{Root: opts.Root, Output: output, Headless: opts.Headless && !c.Rendered, GameID: opts.GameID,
		QuietWorld: c.QuietWorld, Spawned: func(pid int) { s.gabsPID.Store(int64(pid)) }}
	s.config = cfg
	report["keep"] = !c.NoKeep && na.KeepGame()
	start, err := seededStart(c.Start, opts.Seed)
	if err != nil {
		return err
	}
	if resumed.resuming() {
		// The bundle's save replaces the case's Start (the fixture op
		// already ran before the capture), its store lands where the
		// service opens it, and its journal is read at a fresh launch.
		entry := resumed.entry
		save, err := na.StageCheckpoint(opts.Root, entry, filepath.Join(output, "service.sqlite"))
		if err != nil {
			return fmt.Errorf("stage checkpoint: %w", err)
		}
		start = na.Save{Name: save}
		if entry.Journal {
			cfg.RestoreJournal = entry.Path
		}
		if entry.Store && entry.ServiceProfile != "" {
			cfg.ServiceProfile = entry.ServiceProfile
		}
		rev := entry.SourceRevision
		if resumed.previous != nil {
			rev = resumed.previous.SourceRevision
		}
		report["resumed_from"] = map[string]any{"path": entry.Path, "label": entry.Label, "offset_ms": entry.OffsetMs, "tick": entry.Tick, "source_revision": rev, "save": save}
	} else if staged.staged() {
		// A stage bundle opens the way a resume does (#329): its save
		// replaces the Start, its store and journal are restored.
		entry := staged.entry
		save, err := na.StageCheckpoint(opts.Root, entry, filepath.Join(output, "service.sqlite"))
		if err != nil {
			return fmt.Errorf("stage bundle: %w", err)
		}
		start = na.Save{Name: save}
		if entry.Journal {
			cfg.RestoreJournal = entry.Path
		}
		if entry.Store && entry.ServiceProfile != "" {
			cfg.ServiceProfile = entry.ServiceProfile
		}
		report["staged_from"] = map[string]any{"path": entry.Path, "stage": entry.Stage, "offset_ms": entry.OffsetMs, "tick": entry.Tick, "source_revision": entry.SourceRevision, "captured_at": entry.At, "save": save}
	}
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
	// na.OpenSession is the shared preamble (#137): stale-package check,
	// profile, a kept process, discovery, the start, pause, the fixture op,
	// frozen needs and the identity, each on the report.
	opened, err := na.OpenSession(ctx, cfg, report, start, c.Quiet, c.keepNeeds()...)
	if err != nil {
		if resumed.resuming() && resumed.previous != nil {
			rewindRing(opts.RingDir(c), resumed, err, report)
		}
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
	if resumed.resuming() && resumed.entry.Prepared != nil {
		opened.Prepared = resumed.entry.Prepared
		report["prepared"] = resumed.entry.Prepared
	} else if staged.staged() && staged.entry.Prepared != nil {
		opened.Prepared = staged.entry.Prepared
		report["prepared"] = staged.entry.Prepared
	}
	ring := newRing(c, opts, s, cfg, output, resumed, staged, report)
	if ring != nil {
		ring.Activate()
	}
	s.ring, s.stages, s.runStarted = ring, newStages(c, opts, s, cfg, output, staged, report), time.Now()
	runErr := c.Run(ctx, s)
	if len(c.Stages) > 0 {
		report["stages"] = s.stageRows
	}
	if runErr == nil && c.Postmortem != nil {
		runErr = s.postmortem(ctx)
	}
	if runErr == nil {
		// The game's own startup log is part of every case's evidence: a
		// native load error there fails the case even when its assertion
		// held.
		runErr = CheckStartupLog(s)
	}
	closeRing(ring, resumed, runErr, report, func(ctx context.Context) error {
		s.stopServices()
		_, err := opened.Reattach(ctx)
		return err
	})
	return runErr
}

// executePostmortem is the -postmortem-only run (#275): the bundle From
// names (the ring's failed bundle by default) is staged and loaded on the
// kept process, its store copied to <output>/service.sqlite, and only the
// case's Postmortem runs over it. The ring is left as it was, so the next
// plain run still resumes from it, and the report carries postmortem_only
// so the landing gate refuses it like a resumed run.
func executePostmortem(ctx context.Context, c Case, opts Options, output string, report na.Report) error {
	if c.Postmortem == nil {
		return fmt.Errorf("case %s declares no Postmortem phase; -postmortem-only has nothing to run (split its Run into the scenario and the reads that assert on it)", c.Name)
	}
	entry, ring, err := postmortemBundle(c, opts)
	if err != nil {
		return err
	}
	fp, err := fingerprint(c, opts.configDir(c))
	if err != nil {
		return fmt.Errorf("fingerprint this root: %w", err)
	}
	if reason := fp.Mismatch(entry); reason != "" {
		return fmt.Errorf("bundle %s cannot be loaded under this tree: %s", entry.Path, reason)
	}
	s := &session{c: c, report: report, binary: opts.Rimgovernor, resumed: &entry, resumeSuffix: opts.RunID(), stagePlan: staging{hit: -1}}
	cfg := &na.Config{Root: opts.Root, Output: output, Headless: opts.Headless && !c.Rendered, GameID: opts.GameID,
		QuietWorld: c.QuietWorld, Spawned: func(pid int) { s.gabsPID.Store(int64(pid)) }}
	s.config = cfg
	report["keep"] = !c.NoKeep && na.KeepGame()
	report["checkpointing"] = "off (postmortem-only)"
	save, err := na.StageCheckpoint(opts.Root, entry, filepath.Join(output, "service.sqlite"))
	if err != nil {
		return fmt.Errorf("stage bundle: %w", err)
	}
	from := map[string]any{"path": entry.Path, "label": entry.Label, "offset_ms": entry.OffsetMs, "tick": entry.Tick, "source_revision": entry.SourceRevision, "save": save, "store": entry.Store}
	if ring != nil && ring.FailedOutput != "" {
		if prior, err := readResult(filepath.Join(ring.FailedOutput, "result.json")); err == nil {
			s.prior = prior
			from["prior_result"] = filepath.Join(ring.FailedOutput, "result.json")
		}
	}
	report["postmortem_only"] = true
	report["postmortem_from"] = from
	if log := opts.Log; log != nil {
		fmt.Fprintf(log, "postmortem-only: %s over %s (tick %d); the ring is untouched\n", c.Name, entry.Path, entry.Tick)
	}
	if c.Rendered && opts.Headless {
		if err := na.StopGame(ctx, opts.Root, opts.GameID); err != nil {
			return fmt.Errorf("stop kept headless game before rendered start: %w", err)
		}
	}
	opened, err := na.OpenSession(ctx, cfg, report, na.Save{Name: save}, c.Quiet, c.keepNeeds()...)
	if err != nil {
		return err
	}
	if c.NoKeep {
		opened.Game.Keep = false
	}
	defer opened.Close()
	defer s.stopServices()
	report["quiet_mode"] = c.Quiet.String()
	s.Session = opened
	if entry.Prepared != nil {
		opened.Prepared = entry.Prepared
		report["prepared"] = entry.Prepared
	}
	if err := c.Postmortem(ctx, s); err != nil {
		return err
	}
	return CheckStartupLog(s)
}

// planDev is the resumption of an `acceptance dev` iteration (#274): the
// bundle From names, or the ring's next entry (what a plain run would
// resume from) and failing that its failed bundle, checked against this
// root's fingerprint. The ring index is left out of the resumption so
// nothing rewinds or rewrites it.
func planDev(c Case, opts Options, log io.Writer) (resumption, error) {
	if reason := ringExcluded(c); reason != "" {
		return resumption{}, fmt.Errorf("case %s never checkpoints (%s): nothing to iterate from", c.Name, reason)
	}
	entry, _, err := namedBundle(c, opts, func(ring *na.Ring) (string, error) {
		switch {
		case ring != nil && ring.Next != "":
			if e, ok := ring.Entry(ring.Next); ok {
				return e.Path, nil
			}
		case ring != nil && ring.Failed != nil:
			return ring.Failed.Path, nil
		}
		return "", fmt.Errorf("no bundle of %s in %s: a plain run that fails leaves a ring, or name a bundle with -from", c.Name, opts.RingDir(c))
	})
	if err != nil {
		return resumption{}, err
	}
	fp, err := fingerprint(c, opts.configDir(c))
	if err != nil {
		return resumption{}, fmt.Errorf("fingerprint this root: %w", err)
	}
	if reason := fp.Mismatch(entry); reason != "" {
		return resumption{}, fmt.Errorf("bundle %s cannot be loaded under this tree: %s", entry.Path, reason)
	}
	rev := entry.SourceRevision
	if rev == "" {
		rev = "unknown"
	} else if len(rev) > 8 {
		rev = rev[:8]
	}
	fmt.Fprintf(log, "dev: %s from %s (tick %d, rev %s); the ring is untouched\n", c.Name, entry.Path, entry.Tick, rev)
	return resumption{entry: entry}, nil
}

// postmortemBundle resolves Options.From to a bundle of c: a directory
// holding a sidecar, a label in c's ring, or the ring's failed bundle. The
// ring index comes along when the bundle is one of its entries.
func postmortemBundle(c Case, opts Options) (na.Checkpoint, *na.Ring, error) {
	return namedBundle(c, opts, func(ring *na.Ring) (string, error) {
		if ring == nil || ring.Failed == nil {
			return "", fmt.Errorf("no failed bundle of %s in %s: a plain run that fails leaves one, or name a bundle with -from", c.Name, opts.RingDir(c))
		}
		return ring.Failed.Path, nil
	})
}

// namedBundle resolves Options.From to a bundle of c: a directory holding
// a sidecar, a label in c's ring, or with From empty the bundle fallback
// picks from the ring (nil when there is none). The ring index comes
// along when the bundle is one of its entries.
func namedBundle(c Case, opts Options, fallback func(*na.Ring) (string, error)) (na.Checkpoint, *na.Ring, error) {
	dir := opts.RingDir(c)
	ring, err := na.ReadRing(dir)
	if err != nil {
		return na.Checkpoint{}, nil, err
	}
	from := opts.From
	switch {
	case from == "":
		if from, err = fallback(ring); err != nil {
			return na.Checkpoint{}, nil, err
		}
	case filepath.IsAbs(from) || strings.ContainsAny(from, `/\`):
	default:
		from = filepath.Join(dir, from)
	}
	entry, err := na.ReadCheckpoint(from)
	if err != nil {
		if os.IsNotExist(err) {
			return na.Checkpoint{}, nil, fmt.Errorf("-from %s: no bundle there (`ls %s` lists the ring)", opts.From, dir)
		}
		return na.Checkpoint{}, nil, err
	}
	if entry.Case != "" && entry.Case != c.Name {
		return na.Checkpoint{}, nil, fmt.Errorf("-from %s is a bundle of %s, not %s", opts.From, entry.Case, c.Name)
	}
	if rel, err := filepath.Rel(dir, entry.Path); err != nil || strings.HasPrefix(rel, "..") {
		ring = nil
	}
	return entry, ring, nil
}

// readResult reads a result.json as JSON-typed values.
func readResult(path string) (map[string]any, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var result map[string]any
	if err := json.Unmarshal(data, &result); err != nil {
		return nil, err
	}
	return result, nil
}

// postmortem runs the case's Postmortem phase after Run: the services
// the case launched are stopped and the harness holds the game again.
func (s *session) postmortem(ctx context.Context) error {
	s.stopServices()
	if s.Session != nil && s.Session.Game.Released() {
		if _, err := s.Session.Reattach(ctx); err != nil {
			return fmt.Errorf("reattach for postmortem: %w", err)
		}
	}
	if err := s.c.Postmortem(ctx, s); err != nil {
		return fmt.Errorf("postmortem: %w", err)
	}
	return nil
}

// StageSaves copies every Save.From checkpoint the start names into
// <root>/profile/Saves when the root lacks the .rws (every file of that
// name, e.g. its .checkpoint.json sidecar, comes along).
func StageSaves(start Start, root string) error {
	switch v := start.(type) {
	case Fixture:
		if v.On != nil {
			return StageSaves(v.On, root)
		}
	case Save:
		if v.From == "" {
			return nil
		}
		target := filepath.Join(root, "profile", "Saves")
		if _, err := os.Stat(filepath.Join(target, v.Name+".rws")); err == nil {
			return nil
		}
		matches, _ := filepath.Glob(filepath.Join(v.From, v.Name+".*"))
		if len(matches) == 0 {
			return fmt.Errorf("save %s: nothing to stage from %s", v.Name, v.From)
		}
		if err := os.MkdirAll(target, 0755); err != nil {
			return err
		}
		for _, src := range matches {
			data, err := os.ReadFile(src)
			if err != nil {
				return err
			}
			if err := os.WriteFile(filepath.Join(target, filepath.Base(src)), data, 0644); err != nil {
				return err
			}
		}
	}
	return nil
}

// seededStart is the case's Start as the lifecycle library's with seed
// pinned on it (#281); "" leaves the start as declared. Only a debug or
// scenario start (bare or under a Fixture) generates a world to pin.
func seededStart(start Start, seed string) (na.Start, error) {
	if seed == "" {
		return nativeStart(start), nil
	}
	switch s := start.(type) {
	case DebugStart:
		s.Size.Seed = seed
		return nativeStart(s), nil
	case Scenario:
		s.Spec.Seed = seed
		return nativeStart(s), nil
	case Fixture:
		if s.On == nil {
			s.On = DebugStart{}
		}
		on, err := seededStart(s.On, seed)
		if err != nil {
			return nil, err
		}
		return na.Fixture{Op: s.Op, Args: s.Args, On: on}, nil
	}
	return nil, fmt.Errorf("-seed %s: the case starts from %v, which carries its own world; only a debug or scenario start takes a seed", seed, start.Describe())
}

// nativeStart is the case's Start as the lifecycle library's; nil for an
// Owned case, which opens its own game.
func nativeStart(start Start) na.Start {
	switch start := start.(type) {
	case Owned:
		return nil
	case DebugStart:
		return start.Size
	case Save:
		return na.Save{Name: start.Name}
	case Scenario:
		return start.Spec
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
	// resumeSuffix is what RequestID appends on a resumed run, "" fresh.
	resumeSuffix string
	// seed is the run's pinned world seed, "" when none.
	seed string
	// resumed is the checkpoint entry the run resumed from, nil fresh.
	resumed *na.Checkpoint
	// ring is the run's checkpoint ring, nil when it never checkpoints;
	// stages is the ring stage bundles are captured into (#329), nil when
	// staging is off; runStarted is when Run began, for a stage bundle's
	// offset without a ring.
	ring       *na.CheckpointRing
	stages     *na.CheckpointRing
	runStarted time.Time
	// stagePlan is what planStage decided, stagesDir where the case's
	// stage bundles live, stageNext the index of the next declared stage
	// Stage expects and stageRows the report's per-stage rows.
	stagePlan staging
	stagesDir string
	stageNext int
	stageRows []map[string]any
	// prior is the failed run's result.json under -postmortem-only.
	prior   map[string]any
	runtime *na.ScenarioRuntime
	gabsPID atomic.Int64
}

func (s *session) Config() *na.Config { return s.config }
func (s *session) Reload(ctx context.Context) (*na.Harness, error) {
	if s.Session == nil {
		return nil, errors.New("no game open: an Owned case holds its own session")
	}
	h, err := s.Reattach(ctx)
	if err != nil {
		return nil, err
	}
	start, err := seededStart(s.c.Start, s.seed)
	if err != nil {
		return nil, err
	}
	if err := s.Session.Reopen(ctx, start, s.c.Quiet, s.c.keepNeeds()...); err != nil {
		return nil, err
	}
	return h, nil
}
func (s *session) Spec() ServeSpec {
	if s.c.Serve == nil {
		return ServeSpec{}
	}
	spec := *s.c.Serve
	if spec.Binary == "" {
		spec.Binary = s.binary
	}
	return spec
}
func (s *session) Rimgovernor() string      { return s.binary }
func (s *session) GABSPID() int             { return int(s.gabsPID.Load()) }
func (s *session) Harness() *na.Harness     { return s.Session.Harness }
func (s *session) Names() []string          { return s.Session.Names }
func (s *session) Identity() map[string]any { return s.Session.Identity }
func (s *session) Prepared() map[string]any { return s.Session.Prepared }
func (s *session) Report() na.Report        { return s.report }
func (s *session) Resumed() (na.Checkpoint, bool) {
	if s.resumed == nil {
		return na.Checkpoint{}, false
	}
	return *s.resumed, true
}
func (s *session) Staged() (na.Checkpoint, bool) {
	if !s.stagePlan.staged() {
		return na.Checkpoint{}, false
	}
	return s.stagePlan.entry, true
}
func (s *session) Prior() map[string]any { return s.prior }
func (s *session) RequestID(base string) string {
	if s.resumeSuffix == "" {
		return base
	}
	return base + "-" + s.resumeSuffix
}
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
	// A resumed run's store already holds the earlier run's control
	// intents under the case's prefix, with that run's identity; the same
	// requestId with a new loadToken is a conflict (409). Suffix the prefix
	// as RequestID does so the service's resume is its own intent.
	if s.resumeSuffix != "" {
		prefix := spec.Prefix
		if prefix == "" {
			prefix = "serve"
		}
		spec.Prefix = s.RequestID(prefix)
	}
	// The colony-naming dialog a loaded save can still hold stops the clock
	// for good under the service; answer it before releasing the slot unless
	// the case is there to watch the service answer it. A case that released
	// the slot itself (a relaunch between service runs) answered it before
	// its first launch.
	if !spec.KeepColonyNaming && !s.Session.Game.Released() {
		if _, err := na.ConfirmColonyNames(ctx, s.Session.Harness, s.report); err != nil {
			return nil, err
		}
	}
	// The handle's own request ids (resume, acknowledge, the watch's
	// commands) carry the run suffix like the case's (#307): a resumed or
	// staged run's restored store already holds the earlier run's.
	if spec.Prefix == "" {
		spec.Prefix = "serve"
	}
	spec.Prefix = s.RequestID(spec.Prefix)
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

// CommittedSavesDir is the repository directory holding checkpointed
// preconditions (a Save.From source), relative to the repository root.
const CommittedSavesDir = "scripts/fixtures/saves"

// CommittedSaves is CommittedSavesDir as an absolute path, found from the
// working directory upwards (the runner runs from go/ or the repo root);
// the relative name when no ancestor holds it.
func CommittedSaves() string {
	dir, err := os.Getwd()
	if err != nil {
		return CommittedSavesDir
	}
	for {
		candidate := filepath.Join(dir, CommittedSavesDir)
		if _, err := os.Stat(candidate); err == nil {
			return candidate
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return CommittedSavesDir
		}
		dir = parent
	}
}
