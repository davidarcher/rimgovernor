package nativeaccept

import (
	"context"
	"fmt"
	"os"
	"strconv"
)

// QuietStorytellerTool is the test fixture op that silences the storyteller
// (scripts/fixtures/QuietStorytellerFixture.cs); every fixture build carries it.
const QuietStorytellerTool = "test/quiet_storyteller"

// QuietWorldTool marks the loaded game quiet-world (#272; a second tool in
// scripts/fixtures/QuietStorytellerFixture.cs, so every fixture build
// carries it).
const QuietWorldTool = "test/quiet_world"

// LabStartTool wipes the loaded map to a blank Soil lab with fixture
// colonists (#730); LabSpawnTool spawns one building, item or pawn on it
// (#743). Both are in scripts/fixtures/DebugStartFixture.cs, so every
// fixture build carries them.
const (
	LabStartTool = "test/lab_start"
	LabSpawnTool = "test/lab_spawn"
	LabCrewTool  = "test/lab_crew"
	LabStockTool = "test/lab_stock"
)

// LabThing is one LabSpawn: Def names a PawnKindDef (a generated pawn) or a
// ThingDef (a building, or an item stack of Count). Stuff empty takes the
// def's default; Unowned spawns it factionless instead of the player's.
// Gender ("male" or "female") and Age (years) fix a pawn's sex and age; zero
// values leave them generated.
type LabThing struct {
	Def      string
	Stuff    string
	X, Z     int
	Rotation int
	Count    int
	Unowned  bool
	Gender   string
	Age      float64
}

// LabSpawn spawns t through LabSpawnTool and returns the thing's load id
// (the id observation reads key on) and the op's reply.
func LabSpawn(ctx context.Context, h *Harness, t LabThing) (string, map[string]any, error) {
	args := map[string]any{"def": t.Def, "x": t.X, "z": t.Z, "rotation": t.Rotation}
	if t.Stuff != "" {
		args["stuff"] = t.Stuff
	}
	if t.Count > 0 {
		args["count"] = t.Count
	}
	if t.Unowned {
		args["faction"] = "none"
	}
	if t.Gender != "" {
		args["gender"] = t.Gender
	}
	if t.Age > 0 {
		args["age"] = t.Age
	}
	reply, err := h.Call(ctx, "lab-spawn-"+t.Def, LabSpawnTool, args)
	if err != nil {
		return "", nil, fmt.Errorf("%s %s: %w", LabSpawnTool, t.Def, err)
	}
	id := AsString(reply["id"])
	if ok, _ := AsBool(reply["success"]); !ok || id == "" {
		return "", reply, fmt.Errorf("%s %s refused: %#v", LabSpawnTool, t.Def, reply)
	}
	return id, reply, nil
}

// The small start (issue #91): most assertions fit a 200x200 map, and a 5%
// planet is what RimWorld's own quick test uses. MapSizeEnv and
// PlanetCoverageEnv override the defaults for a whole run.
const (
	DefaultMapSize        = 200
	MinMapSize            = 100
	MaxMapSize            = 400
	DefaultPlanetCoverage = 0.05
	MapSizeEnv            = "RIMGOVERNOR_ACCEPT_MAP_SIZE"
	PlanetCoverageEnv     = "RIMGOVERNOR_ACCEPT_PLANET_COVERAGE"
)

// DebugStart is the map size and planet coverage a start is generated with,
// and optionally the biomes it settles: Biomes is a comma-separated list of
// BiomeDef names in preference order, and the start lands on a random valid
// settlement tile of the first one the generated planet offers (issue #172:
// a case whose assertion needs a food-bearing map cannot leave the biome to
// the roll, least of all under the cached start, which pins one roll per
// root). Seed pins the world seed (#281: `acceptance run -seed` reproduces a
// recorded run; the tile and starting pawns follow it natively); empty draws
// one, which the report's world block records. A pinned seed caches under its
// own save. Flat settles a flat tile without rivers, roads or tile mutators
// when the planet offers one (#272: nothing to path around or bridge in a
// construction-heavy case); it caches under its own save. All of it is the
// production new-colony op's spec (#2028).
type DebugStart struct {
	MapSize        int
	PlanetCoverage float64
	Biomes         string
	Seed           string
	Flat           bool
}

// withDefaults fills a zero size and coverage from DefaultDebugStart,
// keeping the biome preference.
func (d DebugStart) withDefaults() DebugStart {
	if d.MapSize == 0 && d.PlanetCoverage == 0 {
		def := DefaultDebugStart()
		d.MapSize, d.PlanetCoverage = def.MapSize, def.PlanetCoverage
	}
	return d
}

// DefaultDebugStart is the small start, or the environment's override.
func DefaultDebugStart() DebugStart {
	d := DebugStart{MapSize: DefaultMapSize, PlanetCoverage: DefaultPlanetCoverage}
	if v := os.Getenv(MapSizeEnv); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			d.MapSize = n
		}
	}
	if v := os.Getenv(PlanetCoverageEnv); v != "" {
		if f, err := strconv.ParseFloat(v, 64); err == nil {
			d.PlanetCoverage = f
		}
	}
	return d
}

// Validate applies the op's own bounds.
func (d DebugStart) Validate() error {
	if d.MapSize < MinMapSize || d.MapSize > MaxMapSize {
		return fmt.Errorf("map size %d outside %d..%d", d.MapSize, MinMapSize, MaxMapSize)
	}
	if d.PlanetCoverage < 0.05 || d.PlanetCoverage > 1 {
		return fmt.Errorf("planet coverage %g outside 0.05..1", d.PlanetCoverage)
	}
	return nil
}

// QuietMode says what StartDebugGame does about the storyteller once the
// debug colony exists (issue #92).
type QuietMode int

const (
	// QuietRequired: the harness depends on a fixture build, so the quiet op
	// must be discoverable and is applied; its absence is a stale-mod error.
	QuietRequired QuietMode = iota
	// QuietIfAvailable: the harness also runs against a production build;
	// the quiet op is applied when discoverable and skipped otherwise.
	QuietIfAvailable
	// Loud: an interruption harness (raids, manhunters, hostile holds) keeps
	// the ordinary storyteller.
	Loud
)

func (m QuietMode) String() string {
	switch m {
	case QuietRequired:
		return "required"
	case QuietIfAvailable:
		return "if-available"
	case Loud:
		return "loud"
	}
	return fmt.Sprintf("QuietMode(%d)", int(m))
}

// quietDecision resolves whether to apply the quiet op given the discovered
// tool names and the harness's mode.
func quietDecision(names []string, mode QuietMode) (bool, error) {
	available := Contains(names, QuietStorytellerTool)
	switch mode {
	case Loud:
		return false, nil
	case QuietIfAvailable:
		return available, nil
	case QuietRequired:
		if !available {
			return false, fmt.Errorf("%s not in discovery: rebuild the native mod with any -Fixture flag (every fixture build includes QuietStorytellerFixture)", QuietStorytellerTool)
		}
		return true, nil
	}
	return false, fmt.Errorf("unknown quiet mode %d", int(mode))
}

// StartDebugGame starts the acceptance debug colony through the production
// new-colony op (a Crashlanded start of DebugColonists colonists, Rough,
// paused, names confirmed), on the RimGovernorQuiet storyteller (Loud keeps
// Cassandra) and then, where the mode asks for it, the fixture's quiet op for
// the residuals the storyteller def does not cover. names are the discovered
// tool names; nil fetches them. The returned map is the quiet op's reply, or
// nil when it was not applied.
func StartDebugGame(ctx context.Context, h *Harness, names []string, mode QuietMode) (map[string]any, error) {
	return StartDebugGameSized(ctx, h, names, mode, DefaultDebugStart())
}

// StartDebugGameSized is StartDebugGame with an explicit start for the
// harnesses that reason about surrounding terrain (excavation, defense
// layout, map scope) and need more map than the default.
func StartDebugGameSized(ctx context.Context, h *Harness, names []string, mode QuietMode, start DebugStart) (map[string]any, error) {
	if names == nil {
		var err error
		if names, err = h.Discovery(ctx); err != nil {
			return nil, err
		}
	}
	apply, err := quietDecision(names, mode)
	if err != nil {
		return nil, err
	}
	quietDef := mode != Loud
	cached := CachedStart() && startCache.root != ""
	name := cachedStartName(start, quietDef)
	startCache.seed, startCache.save = "", ""
	if path, have := cachedStartPath(name); cached && have {
		stale, err := cachedStartStale(path, name)
		if err != nil {
			return nil, err
		}
		if !stale {
			if err := loadCachedStart(ctx, h, name); err != nil {
				return nil, err
			}
			startCache.save = name
			return applyQuiet(ctx, h, apply)
		}
	}
	if err := generateDebugStart(ctx, h, start, quietDef, name); err != nil {
		return nil, err
	}
	if cached {
		// The op saved the colony as name in the running profile.
		if err := PersistSave(startCache.root, startCache.headless, name); err != nil {
			return nil, fmt.Errorf("cached start %s: %w", name, err)
		}
		startCache.save = name
	}
	return applyQuiet(ctx, h, apply)
}

// The colony the acceptance debug start asks the op for.
const (
	DebugScenario   = "Crashlanded"
	DebugColonists  = 3
	DebugDifficulty = "Rough"
)

// scenarioStart is the new-colony spec of the debug start: seed is the pinned
// one or the one the caller drew, saveName the save the op writes.
func (d DebugStart) scenarioStart(seed, saveName string) ScenarioStart {
	return ScenarioStart{
		Scenario: DebugScenario, Count: DebugColonists, Seed: seed, Difficulty: DebugDifficulty,
		Biome: d.Biomes, Flat: d.Flat, Size: d, SaveName: saveName,
	}
}

// generateDebugStart runs the debug start through the new-colony op from the
// main menu, saving it as saveName, with the RimGovernorQuiet storyteller
// when quiet; startCache.seed records the seed it used. The op needs a seed,
// so an unpinned start draws one here and the record knows it even when no
// cached save is kept.
func generateDebugStart(ctx context.Context, h *Harness, start DebugStart, quiet bool, saveName string) error {
	if err := start.Validate(); err != nil {
		return err
	}
	seed := start.Seed
	if seed == "" {
		seed = RandomSeed()
	}
	startCache.seed = seed
	_, err := start.scenarioStart(seed, saveName).Run(ctx, h, quiet)
	return err
}

// ApplyQuietWorld sets the loaded game's quiet-world marker through
// QuietWorldTool and returns its reply (quietWorld, active, launched).
func ApplyQuietWorld(ctx context.Context, h *Harness) (map[string]any, error) {
	reply, err := h.Call(ctx, "quiet-world", QuietWorldTool, map[string]any{"action": "apply"})
	if err != nil {
		return nil, fmt.Errorf("%s: %w", QuietWorldTool, err)
	}
	if ok, _ := AsBool(reply["success"]); !ok {
		return nil, fmt.Errorf("%s refused: %#v", QuietWorldTool, reply)
	}
	return reply, nil
}

// applyQuiet applies the quiet storyteller when the mode asked for it.
func applyQuiet(ctx context.Context, h *Harness, apply bool) (map[string]any, error) {
	h.quiet = false
	if !apply {
		return nil, nil
	}
	quiet, err := h.Call(ctx, "quiet-storyteller", QuietStorytellerTool, map[string]any{"action": "apply"})
	if err != nil {
		return nil, fmt.Errorf("%s: %w", QuietStorytellerTool, err)
	}
	if ok, _ := AsBool(quiet["success"]); !ok {
		return nil, fmt.Errorf("%s refused: %#v", QuietStorytellerTool, quiet)
	}
	h.quiet = true
	return quiet, nil
}

// SuperTraits are the positive traits every superpawn carries so a crew
// works and walks fast: Jogger (SpeedOffset 2) and Industrious
// (Industriousness 2).
const SuperTraits = "SpeedOffset:2,Industriousness:2"

// StaffCrew makes the loaded colony a crew of superpawns (LabCrewTool): it
// tops the free colonists up to size with fixture-made adults, then makes
// each capable of every work type with every skill at 20 and SuperTraits. A
// colony already larger than size is left as it is.
func StaffCrew(ctx context.Context, h *Harness, size int) (colonists, added int, err error) {
	reply, err := h.Call(ctx, "lab-crew", LabCrewTool, map[string]any{"size": size, "level": 20, "traits": SuperTraits})
	if err != nil {
		return 0, 0, fmt.Errorf("%s: %w", LabCrewTool, err)
	}
	if ok, _ := AsBool(reply["success"]); !ok {
		return 0, 0, fmt.Errorf("%s refused: %#v", LabCrewTool, reply)
	}
	n := AsNumber(reply["colonists"])
	a := AsNumber(reply["added"])
	return int(n), int(a), nil
}

// Stock is Total units of one item def laid by LabStock.
type Stock struct {
	Def   string
	Total int
	// X, Z is the cell the stacks are laid nearest to.
	X, Z int
	// Stuff is the stuff of a stuffed item; empty takes the def's default.
	Stuff string
}

// LabStock stocks the loaded map with s (LabStockTool): full stacks and a
// remainder on the free cells nearest (X, Z), joining stacks that have room.
// It is the one way a case puts a resource on the map in quantity: never one
// LabSpawn per stack, which merges into what is already on the cell. It
// returns the units placed.
func LabStock(ctx context.Context, h *Harness, s Stock) (int, error) {
	args := map[string]any{"def": s.Def, "total": s.Total, "x": s.X, "z": s.Z}
	if s.Stuff != "" {
		args["stuff"] = s.Stuff
	}
	reply, err := h.Call(ctx, "lab-stock-"+s.Def, LabStockTool, args)
	if err != nil {
		return 0, fmt.Errorf("%s %s: %w", LabStockTool, s.Def, err)
	}
	if ok, _ := AsBool(reply["success"]); !ok {
		return 0, fmt.Errorf("%s %s refused: %#v", LabStockTool, s.Def, reply)
	}
	placed := int(AsNumber(reply["placed"]))
	if placed != s.Total {
		return placed, fmt.Errorf("%s %s placed %d of %d", LabStockTool, s.Def, placed, s.Total)
	}
	return placed, nil
}
