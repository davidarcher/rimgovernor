// Package lifecycle holds the process-lifecycle cases: each restarts,
// unloads, faults or retires the game on purpose, so every one declares
// NoKeep and the runner stops the process afterwards instead of handing
// it on. A suite schedules them last on a worker.
package lifecycle

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// hook is the required authority hook lifecycle/runtime-fault removes.
const hook = "Pawn_DraftController.Drafted"

func init() {
	// shutdown (the former shutdownaccept, #88): an orderly end of the game
	// revokes native authority as REVOCATION_REASON_SHUTDOWN, which a
	// controller can tell apart from the lease lapse (DISCONNECT, #35 M1)
	// a vanished bot leaves behind. The case grants Auto, ends the game
	// the way the player's quit-to-menu does (the test/shutdown_unload
	// fixture op), and reads authority for the ended game's identity once
	// no game is loaded: native must report Inactive(SHUTDOWN) at
	// generation+1 with the identity and final tick it ended on. A fresh
	// game afterwards proves the process survived and that the retained
	// state never leaks onto another identity.
	cases.Register(cases.Case{
		Name:   "lifecycle/shutdown",
		Scope:  "Unloading the game with Auto granted revokes native authority as REVOCATION_REASON_SHUTDOWN at generation+1; authority_read_status for the ended game's identity reports that retained state with its final tick once no game is loaded; a fresh game afterwards reports the old identity stale and its own authority fresh. Process exit (Root.Shutdown) is hooked the same way but is not wire-observable, so it is not exercised.",
		Start:  cases.DebugStart{},
		Reason: "the case unloads the runner's game and starts a second one; the process ends on a game the next case did not open",
		NoKeep: true,
		Budget: 8 * time.Minute,
		Run:    runShutdown,
	})
	// runtime-fault (the former runtimefaultaccept, #35 M3): a required
	// authority invalidation hook that goes missing at runtime (a partial
	// startup install, or another mod unpatching it) is reported by
	// home/runtime_health, invalidates authority as HOOKS_UNAVAILABLE,
	// and is reinstalled by the next admission without a game restart.
	// Requires a build with -Fixture RuntimeFaultFixture for
	// test/runtime_fault_unpatch.
	cases.Register(cases.Case{
		Name:   "lifecycle/runtime-fault",
		Scope:  "A required authority hook removed at runtime: the fixture observes it missing, authority goes Inactive(HOOKS_UNAVAILABLE) at generation+1 and the hook is reinstalled by the game's own update poll, a SetMode(Auto) at that generation is granted, and home/runtime_health is whole again. No journal fault injection.",
		Start:  cases.DebugStart{},
		Reason: "the fixture unpatches a Harmony hook: process-scoped static state no later case should inherit",
		NoKeep: true,
		Budget: 8 * time.Minute,
		Run:    runRuntimeFault,
	})
}

func runShutdown(ctx context.Context, s cases.Session) error {
	report, h, names := s.Report(), s.Harness(), s.Names()
	for _, tool := range []string{"rimgovernor/authority_read_status", "rimgovernor/authority_control", "rimgovernor/lifecycle_read_identity"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}
	if !na.Contains(names, na.UnloadTool) {
		return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture ShutdownFixture", na.UnloadTool)
	}
	// The runner opened the game paused; this is the identity the unload
	// ends.
	identity := s.Identity()
	// Setup: grant Auto at the fresh game's generation. Authority is
	// initialized by the game's own update polling; a read that lands before
	// that reports it unavailable, and the first generation is always 1.
	generation, state, err := authority(ctx, h, "status-fresh", identity)
	if err != nil {
		return err
	}
	if _, unavailable := state["unavailable"]; unavailable {
		generation = 1
	}
	granted, err := grant(ctx, h, "grant", identity, generation)
	if err != nil {
		return err
	}
	if granted != generation+1 {
		return fmt.Errorf("grant: expected generation %d, got %d", generation+1, granted)
	}
	report["setup"] = map[string]any{"generationBefore": generation, "generationGranted": granted}

	// Case 1: end the game with Auto still granted. The unload is queued as a
	// long event, so the read is polled until no game is loaded and the
	// retained state answers for the ended identity.
	unloaded, err := h.Call(ctx, "unload", na.UnloadTool, map[string]any{})
	if err != nil {
		return fmt.Errorf("unload: %w", err)
	}
	if ok, _ := na.AsBool(unloaded["success"]); !ok {
		return fmt.Errorf("unload refused: %v", unloaded)
	}
	unloadTick := int64(na.AsNumber(unloaded["tick"]))
	afterGeneration, inactive, tick, err := waitShutdown(ctx, h, identity, "status-after-unload")
	if err != nil {
		return err
	}
	if na.AsString(inactive["reason"]) != "REVOCATION_REASON_SHUTDOWN" {
		return fmt.Errorf("status-after-unload: expected REVOCATION_REASON_SHUTDOWN, got %v", inactive)
	}
	if afterGeneration != granted+1 {
		return fmt.Errorf("status-after-unload: expected generation %d, got %d", granted+1, afterGeneration)
	}
	if tick < unloadTick {
		return fmt.Errorf("status-after-unload: retained tick %d precedes the unload tick %d", tick, unloadTick)
	}
	report["case_unload_shutdown"] = map[string]any{"inactive": inactive, "generation": afterGeneration, "tick": tick, "unloadTick": unloadTick}

	// Case 2: the process survived the unload and a fresh game does not
	// inherit the retained state: the old identity is stale against the new
	// game, and the new game's own authority starts unowned.
	next, err := newGame(ctx, h, "new-game-after")
	if err != nil {
		return err
	}
	if na.DeepEqual(next, identity) {
		return fmt.Errorf("new-game-after: expected a fresh identity, got the ended game's %v", next)
	}
	staleReply, err := h.Wire(ctx, "status-old-identity", "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return fmt.Errorf("status-old-identity: %w", err)
	}
	if code, failed := na.FailureCode(staleReply); !failed || code != "FAILURE_CODE_STALE_IDENTITY" {
		return fmt.Errorf("status-old-identity: expected FAILURE_CODE_STALE_IDENTITY, got %v", staleReply)
	}
	nextGeneration, nextState, err := authority(ctx, h, "status-next", next)
	if err != nil {
		return err
	}
	if _, active := nextState["active"]; active {
		return fmt.Errorf("status-next: fresh game inherited active authority: %v", nextState)
	}
	if _, inactive := nextState["inactive"]; inactive && nextGeneration != 1 {
		return fmt.Errorf("status-next: fresh game did not start at generation 1: %v at %d", nextState, nextGeneration)
	}
	report["case_fresh_game"] = map[string]any{"state": nextState, "generation": nextGeneration}

	return cases.CheckStartupLog(s)
}

// newGame starts a paused debug colony and returns its identity.
func newGame(ctx context.Context, h *na.Harness, label string) (map[string]any, error) {
	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietIfAvailable); err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	if _, err := h.Call(ctx, label+"-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}
	identityReply, err := h.Wire(ctx, label+"-identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return nil, err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return nil, err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, ok := na.AsMap(loadedContext["identity"])
	if !ok {
		return nil, fmt.Errorf("%s: missing identity in %v", label, loaded)
	}
	return identity, nil
}

// authority reads the native generation and the state message (one of
// unavailable/inactive/active as its single key) for the identity.
func authority(ctx context.Context, h *na.Harness, label string, identity map[string]any) (uint64, map[string]any, error) {
	reply, err := h.Wire(ctx, label, "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return 0, nil, fmt.Errorf("%s: %w", label, err)
	}
	_, status, err := na.Outcome(reply, "status")
	if err != nil {
		return 0, nil, fmt.Errorf("%s: expected a status outcome: %w", label, err)
	}
	context, _ := na.AsMap(status["context"])
	generation := uint64(na.AsNumber(context["nativeGeneration"]))
	state := map[string]any{}
	for _, key := range []string{"unavailable", "inactive", "active"} {
		if value, ok := status[key]; ok {
			state[key] = value
		}
	}
	if len(state) != 1 {
		return 0, nil, fmt.Errorf("%s: expected exactly one authority state, got %v", label, status)
	}
	if _, unavailable := state["unavailable"]; !unavailable && generation == 0 {
		return 0, nil, fmt.Errorf("%s: missing native generation: %v", label, status)
	}
	return generation, state, nil
}

// grant issues SetMode(Auto) at the expected generation and returns the
// granted generation.
func grant(ctx context.Context, h *na.Harness, label string, identity map[string]any, expected uint64) (uint64, error) {
	reply, err := h.Wire(ctx, label, "authority_control", map[string]any{"setMode": map[string]any{
		"identity": identity, "expectedGeneration": fmt.Sprint(expected), "mode": "MODE_AUTO",
	}})
	if err != nil {
		return 0, fmt.Errorf("%s: %w", label, err)
	}
	_, granted, err := na.Outcome(reply, "granted")
	if err != nil {
		return 0, fmt.Errorf("%s: expected a granted outcome: %w", label, err)
	}
	authority, _ := na.AsMap(granted["authority"])
	if na.AsString(authority["mode"]) != "MODE_AUTO" {
		return 0, fmt.Errorf("%s: expected MODE_AUTO, got %v", label, granted)
	}
	context, _ := na.AsMap(granted["context"])
	return uint64(na.AsNumber(context["nativeGeneration"])), nil
}

// waitShutdown polls authority_read_status for the ended game's identity
// until it reports an inactive state, returning that state's generation,
// inactive message and context tick. The unload runs as a queued long
// event across a scene change, so until it completes a read may still see
// the live game as active or fail unavailable while the maps are being torn
// down; the last reply is reported if the deadline passes.
func waitShutdown(ctx context.Context, h *na.Harness, identity map[string]any, label string) (uint64, map[string]any, int64, error) {
	var generation uint64
	var inactive, last map[string]any
	var tick int64
	attempt := 0
	err := na.WaitProgress(ctx, na.Wait{Ceiling: 30 * time.Second, Interval: 250 * time.Millisecond}, func(ctx context.Context) (string, bool, error) {
		reply, err := h.Wire(ctx, fmt.Sprintf("%s-%d", label, attempt), "authority_read_status", map[string]any{"identity": identity})
		attempt++
		if err != nil {
			return "", false, fmt.Errorf("%s: %w", label, err)
		}
		last = reply
		if _, status, err := na.Outcome(reply, "status"); err == nil {
			var ok bool
			if inactive, ok = na.AsMap(status["inactive"]); ok {
				context, _ := na.AsMap(status["context"])
				if !na.DeepEqual(context["identity"], identity) {
					return "", false, fmt.Errorf("%s: retained context names %v, not the ended game's %v", label, context["identity"], identity)
				}
				generation, tick = uint64(na.AsNumber(context["nativeGeneration"])), int64(na.AsNumber(context["tick"]))
				return "", true, nil
			}
			if _, ok := status["active"]; !ok {
				return "", false, fmt.Errorf("%s: expected active-then-inactive, got %v", label, status)
			}
			return na.Signature("active"), false, nil
		} else if code, failed := na.FailureCode(reply); !failed || code != "FAILURE_CODE_UNAVAILABLE" {
			return "", false, fmt.Errorf("%s: expected a status outcome or a transient unavailable failure, got %v", label, reply)
		}
		return na.Signature("unavailable"), false, nil
	})
	if err != nil {
		return 0, nil, 0, fmt.Errorf("%s: the ended game never reported inactive authority; last reply %v: %w", label, last, err)
	}
	return generation, inactive, tick, nil
}

func runRuntimeFault(ctx context.Context, s cases.Session) error {
	report, h, identity, names := s.Report(), s.Harness(), s.Identity(), s.Names()
	for _, tool := range []string{"rimgovernor/authority_read_status", "rimgovernor/authority_control", "home/runtime_health", "test/runtime_fault_unpatch"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery (fixture build required)", tool)
		}
	}
	// Setup: whole hooks, Auto granted.
	whole, err := health(ctx, h, "health-whole")
	if err != nil {
		return err
	}
	if ready, _ := na.AsBool(whole["ready"]); !ready {
		return fmt.Errorf("health-whole: hooks not ready on a fresh game: %v", whole)
	}
	required := na.AsNumber(whole["required"])
	generation, state, err := authority(ctx, h, "status-fresh", identity)
	if err != nil {
		return err
	}
	if _, unavailable := state["unavailable"]; unavailable {
		generation = 1
	}
	granted, err := grant(ctx, h, "grant", identity, generation)
	if err != nil {
		return err
	}
	report["setup"] = map[string]any{"required": required, "generationGranted": granted}

	// Case 1: remove one required hook. The fixture reports the hook count it
	// observed on the game thread the instant after removal; the game's own
	// UpdatePlay poll then invalidates authority as HOOKS_UNAVAILABLE and
	// reinstalls the hook on that same poll, without any further call.
	unpatched, err := h.Call(ctx, "unpatch", "test/runtime_fault_unpatch", map[string]any{"hook": hook})
	if err != nil {
		return fmt.Errorf("unpatch: %w", err)
	}
	if removed, _ := na.AsBool(unpatched["removed"]); !removed || na.AsNumber(unpatched["health"]) != required-1 {
		return fmt.Errorf("unpatch: expected %v/%v hooks right after removal, got %v", required-1, required, unpatched)
	}
	deadline := time.Now().Add(15 * time.Second)
	var afterGeneration uint64
	for attempt := 0; ; attempt++ {
		afterGeneration, state, err = authority(ctx, h, fmt.Sprintf("status-broken-%d", attempt), identity)
		if err != nil {
			return err
		}
		if inactive, ok := na.AsMap(state["inactive"]); ok {
			if na.AsString(inactive["reason"]) != "REVOCATION_REASON_HOOKS_UNAVAILABLE" {
				return fmt.Errorf("status-broken: expected REVOCATION_REASON_HOOKS_UNAVAILABLE, got %v", inactive)
			}
			break
		}
		if _, ok := state["unavailable"]; ok {
			// Hooks not ready reads as unavailable on the status projection;
			// the generation still advanced. Accept either projection.
			break
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("status-broken: authority still %v after the hook was removed", state)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(250 * time.Millisecond):
		}
	}
	if afterGeneration != granted+1 {
		return fmt.Errorf("status-broken: expected generation %d, got %d", granted+1, afterGeneration)
	}
	report["case_hook_removed"] = map[string]any{"unpatched": unpatched, "state": state, "generation": afterGeneration}

	// Case 2: the next admission reinstalls the hook and is granted; no
	// restart, no fixture involvement.
	regranted, err := grant(ctx, h, "regrant", identity, afterGeneration)
	if err != nil {
		return err
	}
	if regranted != afterGeneration+1 {
		return fmt.Errorf("regrant: expected generation %d, got %d", afterGeneration+1, regranted)
	}
	repaired, err := health(ctx, h, "health-repaired")
	if err != nil {
		return err
	}
	if ready, _ := na.AsBool(repaired["ready"]); !ready || na.AsNumber(repaired["verified"]) != required {
		return fmt.Errorf("health-repaired: expected %v/%v hooks, got %v", required, required, repaired)
	}
	if installed, ok := hookInstalled(repaired, hook); !ok || !installed {
		return fmt.Errorf("health-repaired: expected %s reinstalled: %v", hook, repaired["hooks"])
	}
	report["case_reinstalled"] = map[string]any{"health": repaired, "generation": regranted}

	return cases.CheckStartupLog(s)
}

func health(ctx context.Context, h *na.Harness, label string) (map[string]any, error) {
	reply, err := h.Call(ctx, label, "home/runtime_health", map[string]any{})
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return nil, fmt.Errorf("%s: %v", label, reply)
	}
	return reply, nil
}

func hookInstalled(healthReply map[string]any, name string) (bool, bool) {
	for _, row := range na.AsSlice(healthReply["hooks"]) {
		m, _ := na.AsMap(row)
		if na.AsString(m["name"]) == name {
			installed, _ := na.AsBool(m["installed"])
			return installed, true
		}
	}
	return false, false
}
