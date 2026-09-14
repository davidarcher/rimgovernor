// Command checkpointaccept provides the missing native-acceptance run for
// G01.09's trusted native save-checkpoint slice: full disposable-worker
// lifecycle plus three rimgovernor/lifecycle_save outcomes against the real
// native tool -- a paused happy-path completed save with identity/tick/
// direction/pause verification, an unpaused refusal, and a deliberately wrong
// expected_tick uncertain outcome -- plus two rimgovernor/lifecycle_read_save
// outcomes: replaying the happy-path save's exact recorded result by
// request_id, and refusing an unknown request_id. It never exercises
// reconnect-after-disconnect or competing-viewer arbitration, which remain
// separate, unimplemented capability.
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
	output := flag.String("output", "", "fresh output directory (default <root>/native-checkpoint-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	timeout := flag.Duration("timeout", 600*time.Second, "overall run timeout")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-checkpoint-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	report := na.NewReport("Trusted native rimgovernor/lifecycle_save checkpoint: paused happy-path completed save with identity/tick/direction/pause verification, unpaused refusal, and wrong-expected-tick uncertain outcome; plus rimgovernor/lifecycle_read_save replaying the happy-path outcome by request_id and refusing an unknown request_id. No reconnect/competing-viewer capability exercised.", !*rendered)
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
	if !na.Contains(names, "rimgovernor/lifecycle_save") {
		return fmt.Errorf("missing rimgovernor/lifecycle_save in discovery")
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
	tick := na.AsNumber(loadedContext["tick"])

	// Case 1: paused happy path. A fresh save name with a matching expected tick
	// must complete, echoing exactly the identity/tick/direction/pause/name/
	// request id the request asked for.
	happyRequestID := fmt.Sprintf("checkpointaccept-happy-%d", time.Now().UnixNano())
	happySaveName := fmt.Sprintf("checkpointaccept-happy-%d", time.Now().UnixNano())
	happyReply, err := h.Wire(ctx, "save-happy", "lifecycle_save", map[string]any{
		"player": map[string]any{
			"identity":        identity,
			"playerDirection": 1,
			"requestId":       happyRequestID,
		},
		"saveName":     happySaveName,
		"expectedTick": tick,
	})
	if err != nil {
		return fmt.Errorf("save-happy: %w", err)
	}
	_, completed, err := na.Outcome(happyReply, "completed")
	if err != nil {
		return fmt.Errorf("save-happy: expected a completed save: %w", err)
	}
	completedContext, _ := na.AsMap(completed["context"])
	if !na.DeepEqual(completedContext["identity"], identity) {
		return fmt.Errorf("save-happy: completed identity does not match the request")
	}
	if na.AsNumber(completedContext["tick"]) != tick {
		return fmt.Errorf("save-happy: completed tick %v does not match expected %v", completedContext["tick"], tick)
	}
	if paused, _ := na.AsBool(completed["paused"]); !paused {
		return fmt.Errorf("save-happy: completed save did not report paused")
	}
	if na.AsString(completed["saveName"]) != happySaveName {
		return fmt.Errorf("save-happy: completed save name mismatch")
	}
	if na.AsString(completed["requestId"]) != happyRequestID {
		return fmt.Errorf("save-happy: completed request id mismatch")
	}
	if na.AsNumber(completed["playerDirection"]) != 1 {
		return fmt.Errorf("save-happy: completed player direction mismatch")
	}
	report["case_happy"] = map[string]any{"saveName": happySaveName, "tick": tick}

	// Case 2: attempt a save while NOT paused. Identity is reused unchanged
	// (colony/load/map do not move while ticking) and expected_tick is
	// intentionally omitted, so the refusal exercises the actual reason under
	// test -- native not paused -- rather than a stale-tick race.
	if _, err := h.Call(ctx, "unpause", "rimworld/set_time_speed", map[string]any{"speed": "Normal", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	unpausedReply, err := h.Wire(ctx, "save-unpaused", "lifecycle_save", map[string]any{
		"player": map[string]any{
			"identity":        identity,
			"playerDirection": 1,
			"requestId":       fmt.Sprintf("checkpointaccept-unpaused-%d", time.Now().UnixNano()),
		},
		"saveName": fmt.Sprintf("checkpointaccept-unpaused-%d", time.Now().UnixNano()),
	})
	if err != nil {
		return fmt.Errorf("save-unpaused: %w", err)
	}
	if code, ok := na.FailureCode(unpausedReply); !ok || code != "FAILURE_CODE_INVALID_REQUEST" {
		return fmt.Errorf("save-unpaused: expected FAILURE_CODE_INVALID_REQUEST, got %q", code)
	}
	report["case_unpaused"] = true

	// Case 3: re-pause, then attempt a save with a deliberately wrong
	// expected_tick. Identity is re-read fresh (tick advanced while unpaused
	// above) so only the deliberate tick mismatch triggers the refusal.
	if _, err := h.Call(ctx, "repause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	identityForUncertain, err := h.Wire(ctx, "identity-before-uncertain", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, loadedForUncertain, err := na.Outcome(identityForUncertain, "loaded")
	if err != nil {
		return err
	}
	if paused, _ := na.AsBool(loadedForUncertain["paused"]); !paused {
		return fmt.Errorf("game did not report paused before the uncertain-tick case")
	}
	uncertainContext, _ := loadedForUncertain["context"].(map[string]any)
	uncertainIdentity, _ := uncertainContext["identity"].(map[string]any)
	wrongTick := na.AsNumber(uncertainContext["tick"]) + 1000
	uncertainRequestID := fmt.Sprintf("checkpointaccept-uncertain-%d", time.Now().UnixNano())
	uncertainSaveName := fmt.Sprintf("checkpointaccept-uncertain-%d", time.Now().UnixNano())
	uncertainReply, err := h.Wire(ctx, "save-uncertain", "lifecycle_save", map[string]any{
		"player": map[string]any{
			"identity":        uncertainIdentity,
			"playerDirection": 1,
			"requestId":       uncertainRequestID,
		},
		"saveName":     uncertainSaveName,
		"expectedTick": wrongTick,
	})
	if err != nil {
		return fmt.Errorf("save-uncertain: %w", err)
	}
	_, uncertain, err := na.Outcome(uncertainReply, "uncertain")
	if err != nil {
		return fmt.Errorf("save-uncertain: expected an uncertain outcome, not a completed save: %w", err)
	}
	if na.AsString(uncertain["requestId"]) != uncertainRequestID {
		return fmt.Errorf("save-uncertain: uncertain request id mismatch")
	}
	if na.AsString(uncertain["saveName"]) != uncertainSaveName {
		return fmt.Errorf("save-uncertain: uncertain save name mismatch")
	}
	if na.AsString(uncertain["detail"]) == "" {
		return fmt.Errorf("save-uncertain: missing detail")
	}
	report["case_uncertain"] = true

	// Case 4: rimgovernor/lifecycle_read_save replays the happy-path save's
	// exact recorded outcome by request_id -- proving a caller who lost the
	// original reply can recover it without retrying the save itself.
	if !na.Contains(names, "rimgovernor/lifecycle_read_save") {
		return fmt.Errorf("missing rimgovernor/lifecycle_read_save in discovery")
	}
	readSaveReply, err := h.Wire(ctx, "read-save-happy", "lifecycle_read_save", map[string]any{
		"requestId": happyRequestID,
	})
	if err != nil {
		return fmt.Errorf("read-save-happy: %w", err)
	}
	_, readCompleted, err := na.Outcome(readSaveReply, "completed")
	if err != nil {
		return fmt.Errorf("read-save-happy: expected a completed save: %w", err)
	}
	if na.AsString(readCompleted["requestId"]) != happyRequestID {
		return fmt.Errorf("read-save-happy: request id mismatch")
	}
	if na.AsString(readCompleted["saveName"]) != happySaveName {
		return fmt.Errorf("read-save-happy: save name mismatch")
	}
	readCompletedContext, _ := na.AsMap(readCompleted["context"])
	if !na.DeepEqual(readCompletedContext["identity"], identity) {
		return fmt.Errorf("read-save-happy: replayed identity does not match the original save")
	}
	report["case_read_save_happy"] = true

	// Case 5: ReadSave for an unknown request id is refused, never treated as
	// pending or completed.
	unknownReply, err := h.Wire(ctx, "read-save-unknown", "lifecycle_read_save", map[string]any{
		"requestId": fmt.Sprintf("checkpointaccept-unknown-%d", time.Now().UnixNano()),
	})
	if err != nil {
		return fmt.Errorf("read-save-unknown: %w", err)
	}
	if code, ok := na.FailureCode(unknownReply); !ok || code != "FAILURE_CODE_NOT_FOUND" {
		return fmt.Errorf("read-save-unknown: expected FAILURE_CODE_NOT_FOUND, got %q", code)
	}
	report["case_read_save_unknown"] = true

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), headless); err != nil {
		return err
	}
	return nil
}
