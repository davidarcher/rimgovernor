// Command shutdownaccept is the native-acceptance run for #88: an orderly end
// of the game revokes native authority as REVOCATION_REASON_SHUTDOWN, which a
// controller can tell apart from the lease lapse (DISCONNECT, #35 M1) a
// vanished bot leaves behind. The harness grants Auto, ends the game the way
// the player's quit-to-menu does (the test/shutdown_unload fixture op), and
// reads authority for the ended game's identity once no game is loaded:
// native must report Inactive(SHUTDOWN) at generation+1 with the identity
// and final tick it ended on. A fresh game afterwards proves the process
// survived and that the retained state never leaks onto another identity.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const unloadTool = "test/shutdown_unload"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-shutdown-acceptance)")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-shutdown-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Unloading the game with Auto granted revokes native authority as REVOCATION_REASON_SHUTDOWN at generation+1; authority_read_status for the ended game's identity reports that retained state with its final tick once no game is loaded; a fresh game afterwards reports the old identity stale and its own authority fresh. Process exit (Root.Shutdown) is hooked the same way but is not wire-observable, so it is not exercised.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, report na.Report) error {
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	if err := cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	client, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 90*time.Second)
	if err != nil {
		return err
	}
	defer func() {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer stopCancel()
		if stopped, err := client.GamesStop(stopCtx); err == nil {
			report["stop"] = string(stopped.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
		_ = client.Close()
	}()
	h := na.NewHarness(client, output)
	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	for _, tool := range []string{"rimgovernor/authority_read_status", "rimgovernor/authority_control", "rimgovernor/lifecycle_read_identity"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery", tool)
		}
	}
	if !na.Contains(names, unloadTool) {
		return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture ShutdownFixture", unloadTool)
	}

	identity, err := newGame(ctx, h, "new-game")
	if err != nil {
		return err
	}
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
	unloaded, err := h.Call(ctx, "unload", unloadTool, map[string]any{})
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

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
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
