// Command loadaccept provides the native-acceptance run for G01.09's async
// native load slice: full disposable-worker lifecycle, a setup checkpoint via
// the already-landed rimgovernor/lifecycle_save, then a rimgovernor/
// lifecycle_load of that save polled through rimgovernor/lifecycle_read_load
// to LoadCompleted with map_ready true and a freshly issued load token, plus
// a rejection case (ReadLoad for an unknown request id). It never exercises
// reconnect-after-disconnect or competing-viewer arbitration, which remain
// separate, unimplemented capability. VISUAL readiness is not distinguished
// from MAP in this slice and is not asserted here.
package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-load-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-load-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Trusted native rimgovernor/lifecycle_load: a setup checkpoint save, an async load of that save polled via rimgovernor/lifecycle_read_load to LoadCompleted with map_ready and a fresh load token, and an unknown-request-id rejection. No reconnect/competing-viewer capability exercised; VISUAL readiness not asserted.", !*rendered)
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
	game, err := cfg.GameSection()
	if err != nil {
		return err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return err
	}
	report["package_files"] = files
	gabsExecutable, err := na.GABSExecutable(root, cfg.Configuration)
	if err != nil {
		return err
	}
	client, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 60*time.Second)
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
	report["discovery"] = names
	if !na.Contains(names, "rimgovernor/lifecycle_load") {
		return fmt.Errorf("missing rimgovernor/lifecycle_load in discovery")
	}
	if !na.Contains(names, "rimgovernor/lifecycle_read_load") {
		return fmt.Errorf("missing rimgovernor/lifecycle_read_load in discovery")
	}
	if _, err := h.Call(ctx, "new-game", "rimworld/start_debug_game_ready", map[string]any{
		"readiness": "visual", "pauseIfNeeded": true, "timeoutMs": 120000,
	}); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityBefore, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loaded, err := na.Outcome(identityBefore, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(loaded["paused"]); !paused {
		return fmt.Errorf("fresh debug game did not start paused")
	}
	loadedContext, _ := loaded["context"].(map[string]any)
	identity, _ := loadedContext["identity"].(map[string]any)
	originalColonyID := na.AsString(identity["colonyId"])
	originalLoadToken := na.AsString(identity["loadToken"])
	if originalColonyID == "" || originalLoadToken == "" {
		return fmt.Errorf("missing colony id or load token before setup save")
	}

	// Setup: a trusted checkpoint save this run then loads back.
	setupSaveName := fmt.Sprintf("loadaccept-checkpoint-%d", time.Now().UnixNano())
	setupReply, err := h.Wire(ctx, "setup-save", "lifecycle_save", map[string]any{
		"player": map[string]any{
			"identity":        identity,
			"playerDirection": 1,
			"requestId":       fmt.Sprintf("loadaccept-setup-%d", time.Now().UnixNano()),
		},
		"saveName": setupSaveName,
	})
	if err != nil {
		return fmt.Errorf("setup-save: %w", err)
	}
	if _, _, err := na.Outcome(setupReply, "completed"); err != nil {
		return fmt.Errorf("setup-save: expected a completed save: %w", err)
	}
	report["setup_save"] = setupSaveName

	// Case 1: happy path load. Start the load; native may complete synchronously
	// (unlikely, but the reply shape allows it) or return LoadPending, in which
	// case poll ReadLoad until a definite outcome.
	loadRequestID := fmt.Sprintf("loadaccept-happy-%d", time.Now().UnixNano())
	loadReply, err := h.Wire(ctx, "load-start", "lifecycle_load", map[string]any{
		"requestId": loadRequestID,
		"saveName":  setupSaveName,
		"readiness": "map",
		"expectedPlayer": map[string]any{
			"identity":        identity,
			"playerDirection": 1,
			"requestId":       loadRequestID,
		},
		"playerDirection": 1,
	})
	if err != nil {
		return fmt.Errorf("load-start: %w", err)
	}
	completed, err := pollLoad(ctx, h, loadReply, loadRequestID, "load-poll")
	if err != nil {
		return fmt.Errorf("load-happy: %w", err)
	}
	loadedIdentity, _ := na.AsMap(completed["loaded"])
	loadedContextAfter, _ := na.AsMap(loadedIdentity["context"])
	afterIdentity, _ := na.AsMap(loadedContextAfter["identity"])
	if na.AsString(afterIdentity["colonyId"]) != originalColonyID {
		return fmt.Errorf("load-happy: loaded colony id does not match the saved colony")
	}
	if na.AsString(afterIdentity["loadToken"]) == originalLoadToken || na.AsString(afterIdentity["loadToken"]) == "" {
		return fmt.Errorf("load-happy: loaded map did not receive a fresh load token")
	}
	if na.AsString(completed["saveName"]) != setupSaveName {
		return fmt.Errorf("load-happy: completed save name mismatch")
	}
	if na.AsString(completed["requestId"]) != loadRequestID {
		return fmt.Errorf("load-happy: completed request id mismatch")
	}
	report["case_happy"] = map[string]any{"saveName": setupSaveName, "colonyId": originalColonyID, "newLoadToken": afterIdentity["loadToken"]}

	// Case 2: ReadLoad for an unknown/expired request id must be refused, not
	// silently treated as pending forever or completed.
	unknownReply, err := h.Wire(ctx, "read-load-unknown", "lifecycle_read_load", map[string]any{
		"requestId": fmt.Sprintf("loadaccept-unknown-%d", time.Now().UnixNano()),
	})
	if err != nil {
		return fmt.Errorf("read-load-unknown: %w", err)
	}
	if code, ok := na.FailureCode(unknownReply); !ok || code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("read-load-unknown: expected FAILURE_CODE_NOT_FOUND, got %q", code)
	}
	report["case_unknown_request_id"] = true

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), headless); err != nil {
		return err
	}
	return nil
}

// pollLoad drives rimgovernor/lifecycle_read_load until a completed outcome,
// a native failure, or a bounded number of attempts elapses. A pending or
// superseded outcome is otherwise a definite non-completion this test must
// fail on rather than loop past.
func pollLoad(ctx context.Context, h *na.Harness, first map[string]any, requestID, label string) (map[string]any, error) {
	reply := first
	for attempt := 0; attempt < 300; attempt++ {
		if _, completed, err := na.Outcome(reply, "completed"); err == nil {
			return completed, nil
		}
		if _, superseded, err := na.Outcome(reply, "superseded"); err == nil {
			return nil, fmt.Errorf("load was superseded: %v", superseded["detail"])
		}
		if code, ok := na.FailureCode(reply); ok {
			return nil, fmt.Errorf("load failed: %s", code)
		}
		if _, _, err := na.Outcome(reply, "pending"); err != nil {
			return nil, fmt.Errorf("unexpected load reply shape: %v", reply)
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(200 * time.Millisecond):
		}
		next, err := h.Wire(ctx, fmt.Sprintf("%s-%d", label, attempt), "lifecycle_read_load", map[string]any{"requestId": requestID})
		if err != nil {
			return nil, err
		}
		reply = next
	}
	return nil, fmt.Errorf("load did not complete within the polling budget")
}
