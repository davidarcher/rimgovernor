// Package cases is the acceptance case registry (issue #135): a Case
// declares what it needs (how the game starts, whether the storyteller is
// quiet, which needs stay unfrozen, whether a service runs, its budget) and
// the shared runner (cmd/acceptance) owns boot, keep/reuse, quiet and
// freeze defaults, timing and the report. A case's Run body is the
// assertion only; the preamble every per-harness binary repeated is gone.
//
// Cases live under cases/<area>/*.go and register from init(); the binary
// imports each area package for its side effect.
package cases

import (
	"context"
	"errors"
	"fmt"
	"regexp"
	"sort"
	"sync"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Start is how the runner brings the game to the case's starting state.
// Exactly one of DebugStart, Save, Fixture and Owned.
type Start interface {
	// Describe is the start's summary for the report.
	Describe() map[string]any
	start() // sealed: the runner switches over the three kinds
}

// DebugStart starts RimWorld's debug colony on Size, or on the small
// default (na.DefaultDebugStart) when Size is zero.
type DebugStart struct {
	Size na.DebugStart
}

// Save loads profile/Saves/<Name>.rws. The runner activates the save's
// expansions (Config.UseSaveExpansions).
type Save struct {
	Name string
}

// Fixture brings the game up through On (nil: the debug colony), then
// calls the test fixture Op with Args; the reply is the session's Prepared.
type Fixture struct {
	Op   string
	Args map[string]any
	On   Start
}

// Owned is the start of a case that drives the process lifecycle itself
// (a reusable-game or soak case that launches, restarts or retires the
// game as its assertion): the runner prepares the profile and hands the
// case a Session with Config only (no game open, no Harness, Names or
// Identity), and the case leaves the root's process stopped. Owned
// implies NoKeep. Saves names the saves the case loads, so the profile
// keeps the expansions they record active (as a Save start does).
type Owned struct {
	Saves []string
}

func (d DebugStart) start() {}
func (Save) start()         {}
func (Fixture) start()      {}
func (Owned) start()        {}

func (d DebugStart) Describe() map[string]any {
	return map[string]any{"kind": "debug", "mapSize": d.Size.MapSize, "planetCoverage": d.Size.PlanetCoverage}
}
func (s Save) Describe() map[string]any { return map[string]any{"kind": "save", "name": s.Name} }
func (f Fixture) Describe() map[string]any {
	row := map[string]any{"kind": "fixture", "op": f.Op, "args": f.Args}
	if f.On != nil {
		row["on"] = f.On.Describe()
	}
	return row
}
func (o Owned) Describe() map[string]any {
	out := map[string]any{"kind": "owned"}
	if len(o.Saves) > 0 {
		out["saves"] = o.Saves
	}
	return out
}

// ServeSpec declares the `rimgovernor serve` process a serve-driven case
// runs against (lane C, #138, owns the behaviour; only the shape is agreed
// here). Clock speed always comes from na.ClockSpeedArgs; the flight
// recorder and --listen 127.0.0.1:0 are always on.
type ServeSpec struct {
	// Binary is the prebuilt rimgovernor binary; empty builds it.
	Binary string
	// Save is the save the service loads; Resume starts it with --resume.
	Save   string
	Resume bool
	// Extra are further serve arguments.
	Extra []string
	// NativeTimeout and StepStall bound the service's native calls and the
	// waits bound to the handle; zero takes the shared defaults.
	NativeTimeout time.Duration
	StepStall     time.Duration
}

// Session is what a case's Run receives: the open, prepared game. The
// runner implements it over na.OpenSession (lane B, #137).
type Session interface {
	// Config is the run's resolved configuration (root, output, profile,
	// startup log); an Owned case opens its own game with it.
	Config() *na.Config
	// GABSPID is the PID of the GABS process the harness's session last
	// spawned (a reattach spawns a fresh one), 0 when none was recorded;
	// a transport-drop case kills it by PID, never the game.
	GABSPID() int
	// Harness records evidence under the run's output directory.
	Harness() *na.Harness
	// Names are the discovered native tool names.
	Names() []string
	// Identity is the loaded colony's identity (colonyId, loadToken, mapId...).
	Identity() map[string]any
	// Prepared is a Fixture start's reply, nil otherwise.
	Prepared() map[string]any
	// Report is the run's report; the case adds its own fields.
	Report() na.Report
	// Release closes the harness's bridge session without stopping the game
	// so a service can take the sole GABP slot.
	Release() error
	// Runtime is a scenario runtime over a controller clock the runner
	// acquires on first use, for the cases that advance the game
	// themselves.
	Runtime(ctx context.Context) (*na.ScenarioRuntime, error)
	// Advance is na.AdvanceGame on Runtime with the case's Letters passed
	// as the expected interruption letters.
	Advance(ctx context.Context, ticks uint64, opts ...na.AdvanceOption) (map[string]any, error)
}

// Case is one registered acceptance case.
type Case struct {
	// Name is "<area>/<case>", unique across the registry.
	Name string
	// Scope is the report's one-line description of what passing proves.
	Scope string
	// Start is how the game reaches the case's precondition.
	Start Start
	// Quiet defaults to na.QuietRequired; a Loud (or QuietIfAvailable)
	// case declares it and says why in Reason.
	Quiet na.QuietMode
	// Reason says why the case departs from the checklist's defaults: a
	// storyteller that is not quieted (item 4) or a DebugStart bigger
	// than the default (item 2). Lint requires it for either.
	Reason string
	// Keep are the NeedDef names (Food, Rest, Joy...) left unfrozen;
	// everything else is frozen before Run.
	Keep []string
	// Serve is nil for a bridge-only case.
	Serve *ServeSpec
	// Budget fails the run when exceeded, distinct from the -timeout safety
	// net. Every case declares one, at most MaxBudget (checklist item 6).
	Budget time.Duration
	// Letters are the interruption letters the case expects, as
	// {label, letterDef} pairs in AdvanceGame's order. Nil leaves
	// Session.Advance on AdvanceGame's lenient default (the acknowledged
	// informational letters); a non-nil slice, even empty, makes every
	// advance a strict window over exactly these letters
	// (na.WithExpectedLetters).
	Letters [][2]string
	// Run is the assertion.
	Run func(ctx context.Context, s Session) error
	// NoKeep stops the process after Run instead of leaving it at the main
	// menu for the next case (na.KeepGameEnv): the case restarted, faulted
	// or retired the game on purpose, or touched process-scoped static
	// state. A suite schedules NoKeep cases last on a worker.
	NoKeep bool
	// Rendered opens the windowed profile whatever -headless says: video
	// capture needs Find.Camera, which batch mode never has.
	Rendered bool
}

// Validate is the shape check Register applies.
func (c Case) Validate() error {
	if c.Name == "" {
		return fmt.Errorf("case has no name")
	}
	if c.Start == nil {
		return fmt.Errorf("case %s has no Start", c.Name)
	}
	if c.Run == nil {
		return fmt.Errorf("case %s has no Run", c.Name)
	}
	if _, owned := c.Start.(Owned); owned && !c.NoKeep {
		return fmt.Errorf("case %s owns its process lifecycle (Owned) and must declare NoKeep", c.Name)
	}
	return nil
}

// MaxBudget is the largest Budget a case may declare: past ~15 minutes
// the precondition is not staged well enough or the assertion covers too
// much (checklist item 6).
const MaxBudget = 15 * time.Minute

// nameShape is "<area>/<case>": lowercase words joined by hyphens on each
// side of one slash, so the output path and the report row are the name.
var nameShape = regexp.MustCompile(`^[a-z0-9]+(-[a-z0-9]+)*/[a-z0-9]+(-[a-z0-9]+)*$`)

// Lint holds c to the performance checklist in
// docs/developers/testing/choose-tests.md: what the runner cannot make
// true by construction it refuses to run. Every violation is reported,
// each naming the rule and the checklist item, so a new case fixes them
// in one round. The registry test in cmd/acceptance walks All() with it.
func (c Case) Lint() error {
	var errs []error
	fail := func(rule string, item int, title string) {
		errs = append(errs, fmt.Errorf("case %q: %s (checklist item %d, %q)", c.Name, rule, item, title))
	}
	if err := c.Validate(); err != nil {
		errs = append(errs, err)
	}
	if !nameShape.MatchString(c.Name) {
		errs = append(errs, fmt.Errorf("case %q: Name is not <area>/<case> (lowercase words and hyphens on each side of one slash)", c.Name))
	}
	switch {
	case c.Budget <= 0:
		fail("Budget is missing: every case declares the wall clock it needs", 6, "Budget in minutes and say so")
	case c.Budget > MaxBudget:
		fail(fmt.Sprintf("Budget %s exceeds %s: stage the precondition or split the assertion", c.Budget, MaxBudget), 6, "Budget in minutes and say so")
	}
	if c.Quiet != na.QuietRequired && c.Reason == "" {
		fail(fmt.Sprintf("Quiet is %s without a Reason: only an assertion about an interruption keeps the storyteller", c.Quiet), 4, "Quiet by default")
	}
	if d, ok := c.Start.(DebugStart); ok && c.Reason == "" {
		if d.Size.MapSize > na.DefaultMapSize || d.Size.PlanetCoverage > na.DefaultPlanetCoverage {
			fail(fmt.Sprintf("DebugStart %dx%d at %g planet coverage is bigger than the default %dx%d at %g without a Reason", d.Size.MapSize, d.Size.MapSize, d.Size.PlanetCoverage, na.DefaultMapSize, na.DefaultMapSize, na.DefaultPlanetCoverage), 2, "Small map, tiny planet")
		}
	}
	if c.Serve != nil {
		switch c.Start.(type) {
		case Save, Fixture:
		default:
			fail("Serve declared on a bare DebugStart: a serve-driven case opens on a committed save or a fixture op", 1, "Open on the precondition")
		}
	}
	return errors.Join(errs...)
}

var (
	registryMu sync.Mutex
	registry   = map[string]Case{}
)

// Register adds c to the registry; it panics on an invalid case or a
// duplicate name, since it runs from init().
func Register(c Case) {
	if err := c.Validate(); err != nil {
		panic(err)
	}
	registryMu.Lock()
	defer registryMu.Unlock()
	if _, dup := registry[c.Name]; dup {
		panic(fmt.Sprintf("case %s registered twice", c.Name))
	}
	registry[c.Name] = c
}

// All returns every registered case sorted by name.
func All() []Case {
	registryMu.Lock()
	defer registryMu.Unlock()
	all := make([]Case, 0, len(registry))
	for _, c := range registry {
		all = append(all, c)
	}
	sort.Slice(all, func(i, j int) bool { return all[i].Name < all[j].Name })
	return all
}

// Lookup returns the case registered as name.
func Lookup(name string) (Case, bool) {
	registryMu.Lock()
	defer registryMu.Unlock()
	c, ok := registry[name]
	return c, ok
}

// reset empties the registry; tests only.
func reset() {
	registryMu.Lock()
	defer registryMu.Unlock()
	registry = map[string]Case{}
}
