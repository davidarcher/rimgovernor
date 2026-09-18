package nativeaccept

import (
	"context"
	"fmt"
	"time"
)

// NeedDef names a RimWorld NeedDef (Food, Rest, Joy, ...) that OpenSession
// leaves live when it freezes the rest.
type NeedDef string

const (
	NeedFood NeedDef = "Food"
	NeedRest NeedDef = "Rest"
	NeedJoy  NeedDef = "Joy"
	// LiveNeeds as the keep list leaves every need live and skips the
	// freeze, for a harness whose colony must behave as it always has
	// (the animals/containment case's builder never finishes the pen marker
	// when frozen). The report's frozen_needs is then nil.
	LiveNeeds NeedDef = "*"
)

// Start says how OpenSession gets a loaded game: the debug quick start
// (DebugStart), a prepared save (Save) or a start followed by a fixture op
// (Fixture). A Start describes itself on the report under "start".
type Start interface {
	// saves are the save names the start loads, for UseSaveExpansions.
	saves() []string
	// fixtureOps are the test ops the start calls, for Config.FixtureOps.
	fixtureOps() []string
	// load leaves the game loaded (not necessarily paused) and returns the
	// report row for "start".
	load(ctx context.Context, s *Session, quiet QuietMode) (map[string]any, error)
}

// The debug quick start on the small map; the zero value is
// DefaultDebugStart (the environment's size and coverage).
func (d DebugStart) saves() []string      { return nil }
func (d DebugStart) fixtureOps() []string { return nil }

func (d DebugStart) load(ctx context.Context, s *Session, quiet QuietMode) (map[string]any, error) {
	d = d.withDefaults()
	quietReply, err := StartDebugGameSized(ctx, s.Harness, s.Names, quiet, d)
	if err != nil {
		return nil, err
	}
	s.Report["quiet"] = quietReply != nil
	row := map[string]any{"kind": "debug", "mapSize": d.MapSize, "planetCoverage": d.PlanetCoverage}
	if d.Biomes != "" {
		row["biomes"] = d.Biomes
	}
	return row, nil
}

// Save loads a save from the profile (rimworld/load_game_ready). The save's
// own expansions are enabled through UseSaveExpansions. The storyteller is
// quieted per the mode the way a debug start is.
type Save struct {
	Name string
	// Timeout bounds the load; zero is 90s.
	Timeout time.Duration
}

func (v Save) saves() []string      { return []string{v.Name} }
func (v Save) fixtureOps() []string { return nil }

func (v Save) load(ctx context.Context, s *Session, quiet QuietMode) (map[string]any, error) {
	if v.Name == "" {
		return nil, fmt.Errorf("save start: empty save name")
	}
	if err := loadSave(ctx, s.Harness, v.Name, v.Timeout); err != nil {
		return nil, err
	}
	apply, err := quietDecision(s.Names, quiet)
	if err != nil {
		return nil, err
	}
	quietReply, err := applyQuiet(ctx, s.Harness, apply)
	if err != nil {
		return nil, err
	}
	s.Report["quiet"] = quietReply != nil
	return map[string]any{"kind": "save", "save": v.Name}, nil
}

// caller is the slice of Harness the start steps need, so they are
// unit-testable without a game.
type caller interface {
	Call(ctx context.Context, label, tool string, arguments any) (map[string]any, error)
}

// loadSave is the load_game_ready call every save-driven harness issues.
func loadSave(ctx context.Context, c caller, name string, timeout time.Duration) error {
	if timeout <= 0 {
		timeout = 90 * time.Second
	}
	if _, err := c.Call(ctx, "load-save", "rimworld/load_game_ready", map[string]any{
		"saveName": name, "readiness": "visual", "timeoutMs": timeout.Milliseconds(), "ignoreModCompatibility": false,
	}); err != nil {
		return fmt.Errorf("load save %s: %w", name, err)
	}
	return nil
}

// Fixture runs a test fixture op (test/<name>_prepare) once the game from
// On is loaded and paused; nil On is the debug start. The op must be
// discoverable, must reply success and, when it reports an identity, must
// report the loaded game's; the reply is Session.Prepared and the report's
// "prepared".
type Fixture struct {
	Op   string
	Args map[string]any
	On   Start
}

func (f Fixture) base() Start {
	if f.On == nil {
		return DebugStart{}
	}
	return f.On
}

func (f Fixture) saves() []string { return f.base().saves() }

func (f Fixture) fixtureOps() []string { return append(f.base().fixtureOps(), f.Op) }

func (f Fixture) load(ctx context.Context, s *Session, quiet QuietMode) (map[string]any, error) {
	row, err := f.base().load(ctx, s, quiet)
	if err != nil {
		return nil, err
	}
	row["fixture"] = f.Op
	return row, nil
}

// prepare runs the fixture op against the paused, identified game.
func (f Fixture) prepare(ctx context.Context, c caller, names []string, identity map[string]any) (map[string]any, error) {
	if f.Op == "" {
		return nil, fmt.Errorf("fixture start: empty op")
	}
	if !Contains(names, f.Op) {
		return nil, fmt.Errorf("missing %s in discovery; rebuild the native mod with its fixture%s", f.Op, FixtureBuildHint(f.Op))
	}
	args := f.Args
	if args == nil {
		args = map[string]any{}
	}
	prepared, err := c.Call(ctx, "prepare", f.Op, args)
	if err != nil {
		return nil, err
	}
	if success, _ := AsBool(prepared["success"]); !success {
		return nil, fmt.Errorf("%s refused: %#v", f.Op, prepared)
	}
	if _, reports := prepared["loadToken"]; reports && !MatchesIdentity(prepared, identity) {
		return nil, fmt.Errorf("%s identity does not match the loaded game: %#v", f.Op, prepared)
	}
	return prepared, nil
}

// Session is one bridge-only harness's hold on a loaded, paused, quiet
// game with its needs frozen: what every harness's run preamble used to
// build by hand. Serve-driven harnesses hand the GABP slot to the service
// with Release and take it back with Reattach.
type Session struct {
	Config  *Config
	Game    *Game
	Harness *Harness
	// GABS is the GABS executable a service launch needs.
	GABS string
	// Names is the discovered tool catalog.
	Names []string
	// Identity is the loaded game's colony/load/map identity.
	Identity map[string]any
	// Prepared is the fixture op's reply for a Fixture start, else nil.
	Prepared map[string]any
	Report   Report
	// Boot is how long OpenSession took, from Prepare to the frozen needs.
	Boot time.Duration
}

// OpenSession runs the bridge-only preamble: the stale-package check and
// profile preparation (PrepareConfig; a Save start's expansions first),
// OpenGame (a kept process is reused), discovery, the start (a cached debug
// start, a save load and, for a Fixture, its op), pause, the frozen needs
// except keep (LiveNeeds skips the freeze), and the initial identity. It
// records package_files,
// discovery, start, quiet, prepared, frozen_needs and boot_ms on report;
// Close adds game_reuse. A failure after the game opened closes it before
// returning.
func OpenSession(ctx context.Context, cfg *Config, report Report, start Start, quiet QuietMode, keep ...NeedDef) (*Session, error) {
	began := time.Now()
	if start == nil {
		start = DebugStart{}
	}
	if report == nil {
		report = Report{}
	}
	if cfg.Configuration == "" {
		if cfg.FixtureOps == nil {
			cfg.FixtureOps = start.fixtureOps()
		}
		if saves := start.saves(); len(saves) > 0 && cfg.Expansions == nil {
			if err := cfg.UseSaveExpansions(saves...); err != nil {
				return nil, err
			}
		}
		if err := cfg.PrepareConfig(); err != nil {
			return nil, fmt.Errorf("prepare profile: %w", err)
		}
	}
	game, err := cfg.GameSection()
	if err != nil {
		return nil, err
	}
	files, err := PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return nil, err
	}
	report["package_files"] = files
	gabs, err := GABSExecutable(cfg.Root, cfg.Configuration)
	if err != nil {
		return nil, err
	}
	held, err := OpenGame(ctx, cfg)
	if err != nil {
		return nil, err
	}
	s := &Session{Config: cfg, Game: held, Harness: NewHarness(held.Client, cfg.Output), GABS: gabs, Report: report}
	if err := s.open(ctx, start, quiet, keep); err != nil {
		held.Close(report)
		return nil, err
	}
	s.Boot = time.Since(began)
	report["boot_ms"] = s.Boot.Milliseconds()
	return s, nil
}

func (s *Session) open(ctx context.Context, start Start, quiet QuietMode, keep []NeedDef) error {
	names, err := s.Harness.Discovery(ctx)
	if err != nil {
		return err
	}
	s.Names = names
	s.Report["discovery"] = names
	row, err := start.load(ctx, s, quiet)
	if err != nil {
		return err
	}
	s.Report["start"] = row
	if err := s.Pause(ctx); err != nil {
		return err
	}
	if err := s.RefreshIdentity(ctx, "identity"); err != nil {
		return err
	}
	if fixture, ok := start.(Fixture); ok {
		prepared, err := fixture.prepare(ctx, s.Harness, names, s.Identity)
		if err != nil {
			return err
		}
		s.Prepared = prepared
		s.Report["prepared"] = prepared
	}
	// A QuietIfAvailable harness also runs against a production build,
	// which carries no freeze tool; needs stay live there and the report
	// has no frozen_needs.
	if quiet == QuietIfAvailable && !Contains(names, FreezeNeedsTool) {
		return nil
	}
	return RecordFrozenNeeds(ctx, s.Harness, names, s.Report, keep...)
}

// Reopen runs the start again over the running game through the current
// Harness (a reattached one after a service): a world change that keeps
// the durable journal, with the fixture op, pause, frozen needs and the
// identity repeated as open did them, and Identity replaced by the reloaded
// game's (a kept load token is an error). The report's start, prepared and
// frozen_needs rows are those of the reopened world; identity_reloaded
// records the new identity.
func (s *Session) Reopen(ctx context.Context, start Start, quiet QuietMode, keep ...NeedDef) error {
	before := AsString(s.Identity["loadToken"])
	if err := s.open(ctx, start, quiet, keep); err != nil {
		return err
	}
	if AsString(s.Identity["loadToken"]) == before {
		return fmt.Errorf("reopen kept load token %v", before)
	}
	s.Report["identity_reloaded"] = s.Identity
	return nil
}

// Pause pauses the game.
func (s *Session) Pause(ctx context.Context) error {
	_, err := s.Harness.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false})
	return err
}

// RefreshIdentity re-reads the loaded identity into Identity, recording
// the read under label.
func (s *Session) RefreshIdentity(ctx context.Context, label string) error {
	identity, err := ReadIdentity(ctx, s.Harness, label)
	if err != nil {
		return err
	}
	s.Identity = identity
	return nil
}

// ReadIdentity reads lifecycle_read_identity and returns the loaded
// game's identity map (colonyId, loadToken, mapId); the main menu is an
// error.
func ReadIdentity(ctx context.Context, h *Harness, label string) (map[string]any, error) {
	reply, err := h.Wire(ctx, label, "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return nil, err
	}
	_, loaded, err := Outcome(reply, "loaded")
	if err != nil {
		return nil, err
	}
	loadedContext, _ := AsMap(loaded["context"])
	identity, _ := AsMap(loadedContext["identity"])
	if identity == nil {
		return nil, fmt.Errorf("%s: loaded game reports no identity", label)
	}
	return identity, nil
}

// Release hands the game's single GABP slot to a rimgovernor serve
// subprocess (Game.Release); Harness is unusable until Reattach.
func (s *Session) Release() error {
	if err := s.Game.Release(); err != nil {
		return fmt.Errorf("release bridge session: %w", err)
	}
	return nil
}

// Reattach takes the slot back once the service has stopped and replaces
// Harness with one on the new client.
func (s *Session) Reattach(ctx context.Context) (*Harness, error) {
	client, err := s.Game.Reattach(ctx)
	if err != nil {
		return nil, err
	}
	s.Harness = NewHarness(client, s.Config.Output)
	return s.Harness, nil
}

// Close ends the hold (Game.Close): the process is kept at the main menu
// or stopped, and game_reuse lands on Report.
func (s *Session) Close() { s.Game.Close(s.Report) }

// ScenarioStartTool configures the next new game's scenario, seed, pawn
// count and world settings (scripts/fixtures/ScenarioStartFixture.cs); only
// a ScenarioStartFixture build carries it.
const ScenarioStartTool = "test/configure_start"

// ScenarioStart starts a programmatic scenario from the main menu:
// ScenarioStartTool configures the scenario, then the debug start runs it
// (world and colony generation, so Timeout is longer than a cached debug
// start), the clock is paused and the colony naming dialog dismissed. It is
// how a save variant is generated (variantgen) rather than hand-played.
type ScenarioStart struct {
	Scenario         string
	Count            int
	Seed             string
	Biome            string
	Difficulty       string
	MinTemperature   float64
	MaxTemperature   float64
	WorldTemperature string
	Size             DebugStart
	// Timeout bounds start_debug_game_ready; zero is 180s.
	Timeout time.Duration
}

func (ScenarioStart) saves() []string      { return nil }
func (ScenarioStart) fixtureOps() []string { return nil }

func (v ScenarioStart) load(ctx context.Context, s *Session, quiet QuietMode) (map[string]any, error) {
	if !Contains(s.Names, ScenarioStartTool) {
		return nil, fmt.Errorf("missing %s in discovery -- install a ScenarioStartFixture-built mod "+
			"(scripts/build_native_mod.ps1 -Fixture ScenarioStartFixture ...), not the production mod", ScenarioStartTool)
	}
	configured, err := s.Harness.Call(ctx, "configure-start", ScenarioStartTool, map[string]any{
		"scenario": v.Scenario, "count": v.Count, "seed": v.Seed, "biome": v.Biome,
		"difficulty": v.Difficulty, "minTemperature": v.MinTemperature, "maxTemperature": v.MaxTemperature,
		"worldTemperature": v.WorldTemperature, "mapSize": v.Size.MapSize, "planetCoverage": v.Size.PlanetCoverage,
	})
	if err != nil {
		return nil, fmt.Errorf("configure-start: %w", err)
	}
	if success, _ := AsBool(configured["success"]); !success {
		return nil, fmt.Errorf("configure-start refused: %#v", configured)
	}
	s.Report["configured"] = configured
	timeout := v.Timeout
	if timeout <= 0 {
		timeout = 180 * time.Second
	}
	if _, err := s.Harness.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
		"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": timeout.Milliseconds(),
	}); err != nil {
		return nil, fmt.Errorf("start_debug_game_ready: %w", err)
	}
	if err := s.Pause(ctx); err != nil {
		return nil, err
	}
	if _, err := ConfirmColonyNames(ctx, s.Harness, s.Report); err != nil {
		return nil, err
	}
	apply, err := quietDecision(s.Names, quiet)
	if err != nil {
		return nil, err
	}
	quietReply, err := applyQuiet(ctx, s.Harness, apply)
	if err != nil {
		return nil, err
	}
	s.Report["quiet"] = quietReply != nil
	return map[string]any{"kind": "scenario", "scenario": v.Scenario, "seed": v.Seed, "count": v.Count, "biome": v.Biome,
		"mapSize": v.Size.MapSize, "planetCoverage": v.Size.PlanetCoverage}, nil
}
