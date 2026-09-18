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
	"fmt"
	"sort"
	"sync"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

// Start is how the runner brings the game to the case's starting state.
// Exactly one of DebugStart, Save and Fixture.
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

// Fixture starts the debug colony, then calls the test fixture Op with
// Args; the reply is the session's Prepared.
type Fixture struct {
	Op   string
	Args map[string]any
}

func (d DebugStart) start() {}
func (Save) start()         {}
func (Fixture) start()      {}

func (d DebugStart) Describe() map[string]any {
	return map[string]any{"kind": "debug", "mapSize": d.Size.MapSize, "planetCoverage": d.Size.PlanetCoverage}
}
func (s Save) Describe() map[string]any { return map[string]any{"kind": "save", "name": s.Name} }
func (f Fixture) Describe() map[string]any {
	return map[string]any{"kind": "fixture", "op": f.Op, "args": f.Args}
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

// Session is what a case's Run receives: the open, prepared game (lane B,
// #137, owns the implementation). The runner adapts today's na.OpenGame to
// it until then.
type Session interface {
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
}

// Case is one registered acceptance case.
type Case struct {
	// Name is "<area>/<case>", unique across the registry.
	Name string
	// Scope is the report's one-line description of what passing proves.
	Scope string
	// Start is how the game reaches the case's precondition.
	Start Start
	// Quiet defaults to na.QuietRequired; a Loud case declares it.
	Quiet na.QuietMode
	// Keep are the NeedDef names (Food, Rest, Joy...) left unfrozen;
	// everything else is frozen before Run.
	Keep []string
	// Serve is nil for a bridge-only case.
	Serve *ServeSpec
	// Budget fails the run when exceeded, distinct from the -timeout safety
	// net; zero takes the runner's default.
	Budget time.Duration
	// Letters are the interruption letters the case expects, as
	// {defName, label} pairs.
	Letters [][2]string
	// Run is the assertion.
	Run func(ctx context.Context, s Session) error
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
	return nil
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
