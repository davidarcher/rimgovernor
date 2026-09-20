package cases

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"slices"
	"sort"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Staged run-phase bundles (issue #329): a case whose Run body plays the
// colony into its precondition (a shell sited and roofed, research and a
// bench built, rooms on the baseline) declares those stages (Case.Stages)
// and wraps each staging block in Session.Stage. The first run captures a
// bundle (save, store, clock journal, sidecar) after each block into
// <root>/stages/<area>/<case>/<name>/; the next run opens on the newest
// stage whose fingerprint and staging code still match and skips the
// blocks it covers. A stage is deterministic setup, not the failed
// attempt: -fresh keeps it, -restage discards it, and StagesEnv=0 turns
// the cache off for a harness whose staging is itself under test.

// StagesEnv set to 0 disables stage bundles: every block runs and nothing
// is captured.
const StagesEnv = "RIMGOVERNOR_ACCEPT_STAGES"

// stagesEnabled reads StagesEnv.
func stagesEnabled() bool {
	v := strings.TrimSpace(os.Getenv(StagesEnv))
	return v != "0" && !strings.EqualFold(v, "false")
}

// StagesDir is where c's stage bundles live under the run's root.
func (o Options) StagesDir(c Case) string {
	return filepath.Join(o.Root, "stages", filepath.FromSlash(c.Name))
}

// casesDir is this package's source directory (the area packages are its
// subdirectories), "" when the binary carries no source path.
func casesDir() string {
	_, file, _, ok := runtime.Caller(0)
	if !ok {
		return ""
	}
	return filepath.Dir(file)
}

// StageKey is the hash a stage bundle of c is keyed on: sha256 over the
// Go sources of the case's area package plus its sorted Stages. A change
// to the staging code invalidates the bundle; a change to shared helpers
// does not (-restage covers that); neither the git revision nor the
// rimgovernor binary is in it. It is "" (and staging off, with the
// reason) when the area's sources are not beside the binary's.
func StageKey(c Case) (string, error) {
	dir := casesDir()
	if dir == "" {
		return "", errors.New("the binary carries no source path for the case packages")
	}
	area := c.Name[:strings.Index(c.Name, "/")]
	matches, err := filepath.Glob(filepath.Join(dir, area, "*.go"))
	if err != nil {
		return "", err
	}
	if len(matches) == 0 {
		return "", fmt.Errorf("no Go sources under %s", filepath.Join(dir, area))
	}
	sort.Strings(matches)
	h := sha256.New()
	for _, path := range matches {
		data, err := os.ReadFile(path)
		if err != nil {
			return "", err
		}
		fmt.Fprintf(h, "%s\n", filepath.Base(path))
		h.Write(data)
		h.Write([]byte{0})
	}
	stages := append([]string(nil), c.Stages...)
	sort.Strings(stages)
	fmt.Fprintf(h, "stages=%s\n", strings.Join(stages, ","))
	return hex.EncodeToString(h.Sum(nil)), nil
}

// StageStateKey is the checkpoint state key (na.SetCheckpointState) under
// which the ring records the last stage a run completed, so a resume from
// a later ring bundle skips the stages it already passed.
const StageStateKey = "stage_completed"

// staging is what planStage decided for a run: the stage bundles to open on
// (index -1 when none) and the key this run captures under ("" when
// staging is off, with the reason on the report).
type staging struct {
	key   string
	hit   int
	entry na.Checkpoint
	off   string
}

func (s staging) staged() bool { return s.hit >= 0 }

// planStage reads c's stage bundles and picks the last declared stage whose
// bundle exists and whose fingerprint and stage key match the current
// tree, printing its decision on log. -restage discards the bundles
// first; a pending ring resume (resumed) wins over any stage, since it is
// later on the same timeline.
func planStage(c Case, opts Options, resumed resumption, log io.Writer) (staging, error) {
	plan := staging{hit: -1}
	if len(c.Stages) == 0 {
		return plan, nil
	}
	dir := opts.StagesDir(c)
	if opts.Restage {
		if err := os.RemoveAll(dir); err != nil {
			return plan, err
		}
	}
	if !stagesEnabled() {
		plan.off = StagesEnv + "=0"
		return plan, nil
	}
	key, err := StageKey(c)
	if err != nil {
		plan.off = "cannot key the staging code: " + err.Error()
		fmt.Fprintf(log, "stages of %s off: %s\n", c.Name, plan.off)
		return plan, nil
	}
	plan.key = key
	if resumed.resuming() {
		// The ring bundle is later on the same timeline; the stages it
		// records as done are skipped, the rest run and capture.
		if done := na.AsString(resumed.entry.State[StageStateKey]); done != "" {
			for i, name := range c.Stages {
				if name == done {
					plan.hit = i
				}
			}
		}
		return plan, nil
	}
	if opts.Restage {
		return plan, nil
	}
	current, err := fingerprint(c, opts.configDir(c))
	if errors.Is(err, os.ErrNotExist) {
		// A root nothing has prepared yet (a suite worker the bundles were
		// carried into, #527) still names the installed game in its base
		// config; CachedStage planned from the same fallback.
		current, err = fingerprint(c, filepath.Join(opts.Root, "config"))
	}
	if err != nil {
		fmt.Fprintf(log, "stages of %s off: cannot fingerprint this root (%v)\n", c.Name, err)
		return plan, nil
	}
	for i := len(c.Stages) - 1; i >= 0; i-- {
		name := c.Stages[i]
		entry, err := na.ReadCheckpoint(filepath.Join(dir, name))
		if err != nil {
			if !os.IsNotExist(err) {
				fmt.Fprintf(log, "stage %s of %s: %v; staging again\n", name, c.Name, err)
			}
			continue
		}
		if reason := stageMismatch(entry, name, current, key); reason != "" {
			fmt.Fprintf(log, "discarding stage %s of %s: %s\n", name, c.Name, reason)
			_ = os.RemoveAll(entry.Path)
			continue
		}
		fmt.Fprintf(log, "opening %s on stage %s (captured %s); -restage stages again\n", c.Name, name, entry.At)
		plan.hit, plan.entry = i, entry
		return plan, nil
	}
	return plan, nil
}

// stageMismatch says why a bundle read from the stage directory name
// cannot open a run under the current fingerprint and stage key, or "".
func stageMismatch(entry na.Checkpoint, name string, current na.Fingerprint, key string) string {
	switch reason := current.Mismatch(entry); {
	case reason != "":
		return reason
	case entry.StageKey != key:
		return "the case's staging code changed"
	case entry.Stage != name:
		return "the bundle is not a stage bundle"
	}
	return ""
}

// CachedStage is the newest declared stage of c whose bundle under
// opts.Root a run would open on (the fingerprint and stage key match),
// "" when none: what a suite scheduling stages (#527) plans from. It
// reads only; the run itself discards what it finds stale.
func CachedStage(c Case, opts Options) string {
	if len(c.Stages) == 0 || !stagesEnabled() {
		return ""
	}
	key, err := StageKey(c)
	if err != nil {
		return ""
	}
	current, err := fingerprint(c, opts.configDir(c))
	if errors.Is(err, os.ErrNotExist) {
		current, err = fingerprint(c, filepath.Join(opts.Root, "config"))
	}
	if err != nil {
		return ""
	}
	for i := len(c.Stages) - 1; i >= 0; i-- {
		name := c.Stages[i]
		entry, err := na.ReadCheckpoint(filepath.Join(opts.StagesDir(c), name))
		if err != nil || stageMismatch(entry, name, current, key) != "" {
			continue
		}
		return name
	}
	return ""
}

// RestoredState is the case state key as the bundle this run opened on
// recorded it (a ring resume, else a stage hit; nil on a fresh run): what
// a staging block wrote through na.SetCheckpointState for the code after
// Stage, which on a hit never ran the block. The value is JSON
// round-tripped (numbers float64, slices []any), so read it through the
// tolerant accessors.
func RestoredState(s Session, key string) any {
	if entry, ok := s.Resumed(); ok {
		if v, ok := entry.State[key]; ok {
			return v
		}
	}
	if entry, ok := s.Staged(); ok {
		return entry.State[key]
	}
	return nil
}

// Stage runs fn, the staging block for the declared stage name, unless the
// run opened on a bundle of that stage or a later one, in which case fn is
// skipped: the code after Stage cannot tell the two apart. On a miss fn
// must return with the harness holding the GABP slot (every service it
// launched stopped; a released slot with no service is reattached) and
// the game is paused for the capture, which is the state a hit continues
// from. Stages run in declared order.
func (s *session) Stage(ctx context.Context, name string, fn func(ctx context.Context) error) error {
	index := -1
	for i, declared := range s.c.Stages {
		if declared == name {
			index = i
		}
	}
	if index < 0 {
		return fmt.Errorf("stage %q is not declared by %s (Stages: %v)", name, s.c.Name, s.c.Stages)
	}
	if index != s.stageNext {
		return fmt.Errorf("stage %q out of order: %s declares %v and the next stage is %q", name, s.c.Name, s.c.Stages, s.c.Stages[s.stageNext])
	}
	s.stageNext++
	began := time.Now()
	row := map[string]any{"name": name}
	s.stageRows = append(s.stageRows, row)
	if index <= s.stagePlan.hit {
		row["outcome"], row["path"], row["wall_ms"] = "hit", filepath.Join(s.stagesDir, name), int64(0)
		s.tripStage(name)
		return s.endThrough(name, "hit")
	}
	if err := fn(ctx); err != nil {
		row["outcome"], row["wall_ms"] = "failed", time.Since(began).Milliseconds()
		return err
	}
	if s.Session == nil {
		return fmt.Errorf("stage %q: no game open", name)
	}
	if p := na.LatestService(); p != nil {
		return fmt.Errorf("stage %q: a service still holds the game (%s); stop it before the staging block returns", name, p.Label())
	}
	for _, p := range s.services {
		if p.Running() {
			return fmt.Errorf("stage %q: a service still holds the game (%s); stop it before the staging block returns", name, p.Label())
		}
	}
	if s.Session.Game.Released() {
		if _, err := s.Reattach(ctx); err != nil {
			return fmt.Errorf("stage %q: reattach after the staging block: %w", name, err)
		}
	}
	// The ring's later bundles record the stage as done, so a resume from
	// one (which replays the body from the top) skips it as a hit would.
	na.SetCheckpointState(StageStateKey, name)
	defer s.tripStage(name)
	if s.stages == nil {
		row["outcome"], row["wall_ms"] = "uncached", time.Since(began).Milliseconds()
		if name == s.through {
			return fmt.Errorf("stage %q: -through needs the bundle cached, but staging is off (%s)", name, s.stagePlan.off)
		}
		return nil
	}
	// The bundle's offset is the run's, so a hit sets the ring's Base and
	// its labels stay monotonic; the case state travels with it.
	s.stages.Base = time.Since(s.runStarted)
	if s.ring != nil {
		s.stages.Base = s.ring.Offset()
		s.stages.State = s.ring.StateSnapshot()
	}
	entry, err := s.stages.Capture(ctx, name)
	if err != nil {
		return fmt.Errorf("stage %q: capture: %w", name, err)
	}
	row["outcome"], row["path"], row["wall_ms"], row["capture_ms"] = "captured", entry.Path, time.Since(began).Milliseconds(), entry.WallMs
	return s.endThrough(name, "captured")
}

// throughError is the cause a run ended after its -through stage cancels
// the Run body with; stagedThrough reads it back.
type throughError struct {
	Stage, Outcome string
}

func (e *throughError) Error() string { return "staged through " + e.Stage + " (" + e.Outcome + ")" }

// stagedThrough is the -through stage that ended ctx, nil when the
// context ended for any other reason (or not at all).
func stagedThrough(ctx context.Context) *throughError {
	var te *throughError
	if errors.As(context.Cause(ctx), &te) {
		return te
	}
	return nil
}

// endThrough ends the run after the named stage when it is the run's
// -through stage (#527): the Run body is cut through its context and the
// report says which stage the run ended on and whether its bundle was
// taken by this run or found cached.
func (s *session) endThrough(name, outcome string) error {
	if name != s.through {
		return nil
	}
	s.report["staged_through"] = map[string]any{"stage": name, "outcome": outcome, "path": filepath.Join(s.stagesDir, name)}
	err := &throughError{Stage: name, Outcome: outcome}
	if s.cutRun != nil {
		s.cutRun(err)
	}
	return err
}

// checkThrough validates opts.Through against c before the game opens: a
// declared stage of a plain run with staging on.
func checkThrough(c Case, opts Options) error {
	if opts.Through == "" {
		return nil
	}
	if opts.PostmortemOnly || opts.Dev || opts.Repeat > 1 || !opts.Break.IsZero() {
		return errors.New("-through takes a plain run: none of -postmortem-only, -repeat, -break, or dev")
	}
	if !slices.Contains(c.Stages, opts.Through) {
		if len(c.Stages) == 0 {
			return fmt.Errorf("-through %s: case %s declares no Stages", opts.Through, c.Name)
		}
		return fmt.Errorf("-through %s: case %s declares the stages %v", opts.Through, c.Name, c.Stages)
	}
	if !stagesEnabled() {
		return fmt.Errorf("-through %s: stage bundles are off (%s=0), so nothing would be cached", opts.Through, StagesEnv)
	}
	return nil
}

// tripStage fires the run's stage breakpoint (#280) once the named stage
// is done, whether its block ran or a bundle covered it.
func (s *session) tripStage(name string) {
	if s.ring != nil && s.ring.Break.Stage == name {
		s.ring.Trip("stage " + name + " done")
	}
}
