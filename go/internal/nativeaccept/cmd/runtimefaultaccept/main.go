// Command runtimefaultaccept is the native-acceptance run for #35 M3: a
// required authority invalidation hook that goes missing at runtime (a
// partial startup install, or another mod unpatching it) is reported by
// home/runtime_health, invalidates authority as HOOKS_UNAVAILABLE, and is
// reinstalled by the next admission without a game restart. Requires a
// build with -Fixture RuntimeFaultFixture for test/runtime_fault_unpatch.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

const hook = "Pawn_DraftController.Drafted"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-runtime-fault-acceptance)")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-runtime-fault-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("A required authority hook removed at runtime: the fixture observes it missing, authority goes Inactive(HOOKS_UNAVAILABLE) at generation+1 and the hook is reinstalled by the game's own update poll, a SetMode(Auto) at that generation is granted, and home/runtime_health is whole again. No journal fault injection.", !*rendered)
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
	client, err := na.OpenBridgeSession(ctx, gabsExecutable, cfg.Configuration, gameID, 90*time.Second)
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
	for _, tool := range []string{"rimgovernor/authority_read_status", "rimgovernor/authority_control", "home/runtime_health", "test/runtime_fault_unpatch"} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery (fixture build required)", tool)
		}
	}
	if _, err := na.StartDebugGame(ctx, h, names, na.QuietRequired); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])

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

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
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
	return generation, state, nil
}

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
	context, _ := na.AsMap(granted["context"])
	return uint64(na.AsNumber(context["nativeGeneration"])), nil
}
