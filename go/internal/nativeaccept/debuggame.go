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

// DebugStartTool arms the next quick start's map size and planet coverage
// (scripts/fixtures/DebugStartFixture.cs); every fixture build carries it.
const DebugStartTool = "test/configure_debug_start"

// The small start (issue #91): most assertions fit a 200x200 map, and a 5%
// planet is what RimWorld's own quick test uses. MapSizeEnv and
// PlanetCoverageEnv override the defaults for a whole run.
const (
	DefaultMapSize        = 200
	MinMapSize            = 150
	MaxMapSize            = 400
	DefaultPlanetCoverage = 0.05
	MapSizeEnv            = "RIMGOVERNOR_ACCEPT_MAP_SIZE"
	PlanetCoverageEnv     = "RIMGOVERNOR_ACCEPT_PLANET_COVERAGE"
)

// DebugStart is the map size and planet coverage a start is generated with.
type DebugStart struct {
	MapSize        int
	PlanetCoverage float64
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

// Validate applies the fixture's own bounds.
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

// StartDebugGame starts RimWorld's debug colony (rimworld/start_debug_game_ready
// at visual readiness, paused) on the small start when the build carries
// DebugStartTool, and then, per mode, quiets the storyteller so the harness
// sees only its own events. names are the discovered tool names; nil
// fetches them. The returned map is the quiet op's reply, or nil when it was
// not applied.
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
	if Contains(names, DebugStartTool) {
		if err := start.Validate(); err != nil {
			return nil, err
		}
		armed, err := h.Call(ctx, "debug-start", DebugStartTool, map[string]any{"mapSize": start.MapSize, "planetCoverage": start.PlanetCoverage})
		if err != nil {
			return nil, fmt.Errorf("%s: %w", DebugStartTool, err)
		}
		if ok, _ := AsBool(armed["success"]); !ok {
			return nil, fmt.Errorf("%s refused: %#v", DebugStartTool, armed)
		}
	}
	if _, err := h.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
		"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": 120000,
	}); err != nil {
		return nil, err
	}
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
	return quiet, nil
}
