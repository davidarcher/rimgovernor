package nativeaccept

import (
	"context"
	"fmt"
)

// QuietStorytellerTool is the test fixture op that silences the storyteller
// (scripts/fixtures/QuietStorytellerFixture.cs); every fixture build carries it.
const QuietStorytellerTool = "test/quiet_storyteller"

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
// at visual readiness, paused) and then, per mode, quiets the storyteller so
// the harness sees only its own events. names are the discovered tool names;
// nil fetches them. The returned map is the quiet op's reply, or nil when it
// was not applied.
func StartDebugGame(ctx context.Context, h *Harness, names []string, mode QuietMode) (map[string]any, error) {
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
