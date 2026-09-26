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
	"strings"
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
// default (na.DefaultDebugStart) when Size is zero; Size.Biomes pins the
// start to a biome the assertion needs (fixture builds only).
type DebugStart struct {
	Size na.DebugStart
}

// FlatDebugStart is the flat default-size debug start (na.DebugStart.Flat,
// #272): nothing to path around or bridge. A construction, haul or storage
// case whose assertion never watches the wild map or the terrain declares
// it beside QuietWorld (#333); farm, husbandry, hunting and terrain cases
// stay on the plain start.
func FlatDebugStart() DebugStart { return DebugStart{Size: na.DebugStart{Flat: true}} }

// Save loads profile/Saves/<Name>.rws. The runner activates the save's
// expansions (Config.UseSaveExpansions).
type Save struct {
	Name string
	// From, when set, is a committed directory holding Name.rws (and any
	// sidecar files) that the runner copies into <root>/profile/Saves when
	// the root lacks the save, before the profile is prepared: a
	// checkpointed precondition a fresh root can resume from.
	From string
}

// Fixture brings the game up through On (nil: the debug colony), then
// calls the test fixture Op with Args; the reply is the session's Prepared.
type Fixture struct {
	Op   string
	Args map[string]any
	On   Start
	// ArgsFrom computes more arguments from the loaded game just before
	// the op runs (StarterSiteArgs: the controller's own hut site, #700).
	ArgsFrom func(context.Context, *na.Harness) (map[string]any, error)
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

// Scenario starts a programmatic scenario from the main menu through the
// ScenarioStartFixture (na.ScenarioStart): how a save variant is generated.
type Scenario struct {
	Spec na.ScenarioStart
}

func (d DebugStart) start() {}
func (Save) start()         {}
func (Fixture) start()      {}
func (Owned) start()        {}
func (Scenario) start()     {}

func (d DebugStart) Describe() map[string]any {
	row := map[string]any{"kind": "debug", "mapSize": d.Size.MapSize, "planetCoverage": d.Size.PlanetCoverage}
	if d.Size.Biomes != "" {
		row["biomes"] = d.Size.Biomes
	}
	if d.Size.Seed != "" {
		row["seed"] = d.Size.Seed
	}
	if d.Size.Flat {
		row["flat"] = true
	}
	return row
}
func (s Save) Describe() map[string]any { return map[string]any{"kind": "save", "name": s.Name} }
func (f Fixture) Describe() map[string]any {
	row := map[string]any{"kind": "fixture", "op": f.Op, "args": f.Args}
	if f.On != nil {
		row["on"] = f.On.Describe()
	}
	return row
}
func (s Scenario) Describe() map[string]any {
	return map[string]any{"kind": "scenario", "scenario": s.Spec.Scenario, "seed": s.Spec.Seed, "count": s.Spec.Count, "biome": s.Spec.Biome}
}
func (o Owned) Describe() map[string]any {
	out := map[string]any{"kind": "owned"}
	if len(o.Saves) > 0 {
		out["saves"] = o.Saves
	}
	return out
}

// ServeSpec declares the `rimgovernor serve` process a serve-driven case
// runs against: na.ServeSpec (Session.Serve launches it; an empty Binary
// takes the run's -rimgovernor). Clock speed always comes from
// na.ClockSpeedArgs; the flight recorder and --listen 127.0.0.1:0 are
// always on.
type ServeSpec = na.ServeSpec

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
	// RequestID is base on a fresh run and base suffixed with the run's
	// id on a resumed one (#307): the restored store already holds the
	// fresh run's submissions, and a replay under the resumed world's
	// identity is a different request the store answers 409 conflict.
	// Stable within a run, so a relaunch on the same journal still
	// replays idempotently. Use it for every deterministic requestId a
	// Run body submits.
	RequestID(base string) string
	// Stage runs fn, the staging block of the declared stage name (#329),
	// unless the run opened on a bundle of that stage or a later one, in
	// which case fn is skipped and the code after Stage cannot tell the
	// two apart. fn returns with every service it launched stopped (a
	// released slot with no service is reattached); the game is paused
	// for the capture, the state a hit continues from. Stages run in
	// declared order; an undeclared or out-of-order name fails the run.
	Stage(ctx context.Context, name string, fn func(ctx context.Context) error) error
	// Resumed is the checkpoint entry this run resumed from (#249), ok
	// false on a fresh run. Its State is what the case recorded through
	// na.SetCheckpointState before the capture: a Run body that stages
	// its own fixture reads it to skip the prep the save carries (#316).
	Resumed() (entry na.Checkpoint, ok bool)
	// Staged is the stage bundle this run opened on (#329), ok false when
	// it opened fresh or on a ring checkpoint. Its State is what the
	// staging run recorded through na.SetCheckpointState before the
	// capture: a staging block's outcome the code after Stage needs (a
	// pre-service baseline count, the cells a fixture left unbuilt).
	Staged() (entry na.Checkpoint, ok bool)
	// Prior is the failed run's result.json (JSON-typed: slices are []any,
	// numbers float64) under -postmortem-only (#275), nil on any other run
	// or when the ring did not record it: the timeline and the report
	// fields the watch left, for a Postmortem that reads them.
	Prior() map[string]any
	// Report is the run's report; the case adds its own fields.
	Report() na.Report
	// Release closes the harness's bridge session without stopping the game
	// so a service can take the sole GABP slot.
	Release() error
	// Reattach takes the slot back once the service has stopped; the
	// returned Harness replaces Harness().
	Reattach(ctx context.Context) (*na.Harness, error)
	// Spec is the case's declared Serve spec (Binary resolved to the run's
	// -rimgovernor), or the zero spec for a bridge-only case.
	Spec() ServeSpec
	// Rimgovernor is the run's -rimgovernor binary (absolute path), empty
	// when none was passed; an Owned case that launches controllers over
	// its own game (na.LaunchService) takes it from here.
	Rimgovernor() string
	// Reload takes the slot back (Reattach) and runs the case's Start again
	// over the running game: a world change that keeps the durable journal
	// (routine goals of the old world invalidate), with the fixture op,
	// frozen needs and Identity repeated for the reloaded world. The
	// returned Harness replaces Harness(); a Serve afterwards opens on the
	// new identity.
	Reload(ctx context.Context) (*na.Harness, error)
	// Launch releases the slot and starts rimgovernor serve for a case that
	// manages the attach and authority steps itself (na.LaunchService); an
	// empty Binary takes the run's -rimgovernor. The runner stops whatever
	// is still running when Run returns.
	Launch(ctx context.Context, launch na.ServiceLaunch) (*na.ServiceProcess, error)
	// Serve is the full serve lifecycle over the session's game (na.Serve):
	// release, launch, attach to the session's identity. An empty Binary
	// takes the run's -rimgovernor.
	Serve(ctx context.Context, spec na.ServeSpec) (*na.ServiceProcess, error)
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
	// Expansions explicitly selects a DLC profile for a programmatic fixture.
	// Such a case owns a fresh process; the profile writer adds knownExpansions.
	Expansions []string
	// Production requires a fixture-free native package. Remote executors
	// must switch private package layouts between production and fixture rows.
	Production bool
	// Name is "<area>/<case>", unique across the registry.
	Name string
	// Scope is the report's one-line description of what passing proves.
	Scope string
	// Start is how the game reaches the case's precondition.
	Start Start
	// RequiredOps lists fixture operations called by Run or service hooks.
	RequiredOps []string
	// Quiet defaults to na.QuietRequired; a Loud (or QuietIfAvailable)
	// case declares it and says why in Reason.
	Quiet na.QuietMode
	// Reason says why the case departs from the checklist's defaults: a
	// storyteller that is not quieted (item 4) or a DebugStart bigger
	// than the default (item 2) or a Budget past MaxBudget (item 6). Lint
	// requires it for any of the three.
	Reason string
	// QuietWorld marks the game quiet-world (na.Config.QuietWorld, #272):
	// under test acceleration, wild plants and animals outside the home
	// area (and any growing zone) stop ticking and the wild spawners stop.
	// A case whose assertion watches the wild map (farm, husbandry,
	// hunting) leaves it off.
	QuietWorld bool
	// Keep are the NeedDef names (Food, Rest, Joy...) left unfrozen;
	// everything else is frozen before Run.
	Keep []string
	// Serve is nil for a bridge-only case.
	Serve *ServeSpec
	// Service marks a case whose Run hosts rimgovernor serve itself
	// through Session.Launch or Session.Serve (a Serve spec implies it):
	// the suite schedules it after the bridge-only cases (#119) and passes
	// its -rimgovernor to the run.
	Service bool
	// Budget fails the run when exceeded, distinct from the -timeout safety
	// net. Every case declares one, at most MaxBudget (checklist item 6).
	Budget time.Duration
	// Stall replaces na.DefaultStall for the case's waits: a case whose
	// passing runs hold a signature longer than the default (#353). Zero
	// is the shared budget; the runner's -stall overrides both.
	Stall time.Duration
	// Letters are the interruption letters the case expects, as
	// {label, letterDef} pairs in AdvanceGame's order. Nil leaves
	// Session.Advance on AdvanceGame's lenient default (the acknowledged
	// informational letters); a non-nil slice, even empty, makes every
	// advance a strict window over exactly these letters
	// (na.WithExpectedLetters).
	Letters [][2]string
	// Stages names, in order, the staging blocks the Run body wraps in
	// Session.Stage (#329): the runner caches a bundle after each and the
	// next run opens on the newest one that still matches. Every name is
	// unique and non-empty; an Owned case declares none.
	Stages []string
	// Run is the assertion, or its scenario when Postmortem is set.
	Run func(ctx context.Context, s Session) error
	// Postmortem, when set, is the case's read-and-assert phase (#275): the
	// runner calls it after Run returns nil, with every service the case
	// launched stopped and the harness reattached, so the durable store
	// (<output>/service.sqlite) and the live game can be compared. It is
	// what `acceptance run -postmortem-only` runs alone over the case's
	// failed bundle (or a -from entry) reloaded on the kept process: no
	// Start fixture, no Run. Anything Run learned that the phase needs
	// travels through na.SetCheckpointState (Session.Resumed's State) or
	// Session.Prior, never a shared variable alone.
	Postmortem func(ctx context.Context, s Session) error
	// NoKeep stops the process after Run instead of leaving it at the main
	// menu for the next case (na.KeepGameEnv): the case restarted, faulted
	// or retired the game on purpose, or touched process-scoped static
	// state. A suite schedules NoKeep cases last on a worker.
	NoKeep bool
	// Rendered opens the windowed profile whatever -headless says: video
	// capture needs Find.Camera, which batch mode never has. Remote plans skip
	// these GPU-dependent cases on hosted Windows runners.
	Rendered bool
	// NoCheckpoint opts the case out of the runner's checkpoint ring
	// (#249): no periodic bundles, no failed bundle, no resume. The
	// speedmatrix and tickbudget areas are out regardless.
	NoCheckpoint bool
	// Matrix puts the case in the matrix tier (#273): a throughput or
	// scheduler measurement (speedmatrix, tickbudget) or a DLC-save case,
	// run on demand and whenever the clock scheduler or the native tick
	// path changes, never by the land or full tier.
	Matrix bool
}

// FixtureOps are the test ops the case's Start calls, outermost last:
// what the installed build must register for the case to start
// (Config.FixtureOps; the doctor's fixture coverage and a heal's rebuild
// read them, #276), followed by RequiredOps used by Run or service hooks.
func (c Case) FixtureOps() []string {
	var ops []string
	for start := c.Start; start != nil; {
		f, ok := start.(Fixture)
		if !ok {
			break
		}
		ops = append([]string{f.Op}, ops...)
		start = f.On
	}
	return append(ops, c.RequiredOps...)
}

// Validate is the shape check Register applies.
func (c Case) Validate() error {
	if len(c.Expansions) > 0 && !c.NoKeep {
		return fmt.Errorf("case %s with expansions must set NoKeep", c.Name)
	}
	for _, expansion := range c.Expansions {
		if _, err := na.ExpansionPackage(expansion); err != nil {
			return err
		}
	}
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
	if _, owned := c.Start.(Owned); owned && len(c.Stages) > 0 {
		return fmt.Errorf("case %s owns its process lifecycle (Owned) and cannot declare Stages", c.Name)
	}
	seen := map[string]bool{}
	for _, name := range c.Stages {
		if name == "" || strings.ContainsAny(name, "/\\") || seen[name] {
			return fmt.Errorf("case %s: stage %q must be a unique, non-empty name without path separators", c.Name, name)
		}
		seen[name] = true
	}
	return nil
}

// keepNeeds is Keep as need defs.
func (c Case) keepNeeds() []na.NeedDef {
	keep := make([]na.NeedDef, len(c.Keep))
	for i, need := range c.Keep {
		keep[i] = na.NeedDef(need)
	}
	return keep
}

// MaxBudget is the largest Budget a case may declare without a Reason:
// past ~15 minutes the precondition is not staged well enough or the
// assertion covers too much (checklist item 6).
const MaxBudget = 15 * time.Minute

// nameShape is "<area>/<case>": lowercase words joined by hyphens on each
// side of one slash (case names also accept underscores), so the output path
// and the report row are the name.
var nameShape = regexp.MustCompile(`^[a-z0-9]+(-[a-z0-9]+)*/[a-z0-9]+([-_][a-z0-9]+)*$`)

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
	case c.Budget > MaxBudget && c.Reason == "":
		fail(fmt.Sprintf("Budget %s exceeds %s: stage the precondition, split the assertion or give a Reason", c.Budget, MaxBudget), 6, "Budget in minutes and say so")
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
