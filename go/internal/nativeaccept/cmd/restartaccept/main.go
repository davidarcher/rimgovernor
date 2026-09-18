// Command restartaccept is the kill-and-restart native acceptance for SIMP06:
// a controller launched with --resume runs the bot for the observed world
// with no HTTP write at all, is killed mid-play, and a second controller
// reopening the same SQLite state resumes autonomous play for the same
// world -- again without a dashboard step -- continuing the routine review
// past the revision the killed process left. Nothing here backs up or
// restores the database; the plan is re-derived from observation.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-restart-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	binary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	timeout := flag.Duration("timeout", 20*time.Minute, "overall run timeout")
	na.BudgetFlag((20 * time.Minute) / 2)
	flag.Parse()
	if *root == "" || *binary == "" || !filepath.IsAbs(*binary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-restart-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Kill-and-restart: serve --resume runs the bot for the observed world with no HTTP write, is killed, and a restarted controller on the same state resumes autonomous play for the same world and advances the routine review, again with no player step.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, *binary, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, binary string, report na.Report) error {
	if abs, err := filepath.Abs(root); err == nil {
		root = abs
	}
	if abs, err := filepath.Abs(output); err == nil {
		output = abs
	}
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
	client, err := na.OpenBridgeSession(ctx, gabsExecutable, cfg.Configuration, gameID, 120*time.Second)
	if err != nil {
		return err
	}
	h := na.NewHarness(client, output)
	// The harness closes its GABS session while a controller owns the game
	// slot and reopens one to stop the game; see cmd/poweraccept.
	sessionOpen := true
	var service *na.ServiceProcess
	stopped := false
	stopGame := func() {
		if stopped {
			return
		}
		stopped = true
		if service != nil {
			service.Stop()
		}
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 90*time.Second)
		defer stopCancel()
		if !sessionOpen {
			reopened, err := na.ReopenSession(stopCtx, gabsExecutable, cfg.Configuration, gameID)
			if err != nil {
				report["stop_error"] = "reopen session for games_stop: " + err.Error()
				return
			}
			client, sessionOpen = reopened, true
		}
		if s, err := client.GamesStop(stopCtx); err == nil {
			report["stop"] = string(s.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
		_ = client.Close()
	}
	defer stopGame()

	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietIfAvailable); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return err
	}
	if naming, ok := na.AsMap(facts["colonyNaming"]); ok && naming != nil {
		confirmed, err := h.Call(ctx, "confirm-colony-names", "home/confirm_colony_names", map[string]any{
			"windowId": int(na.AsNumber(naming["windowId"])), "factionName": na.AsString(naming["factionName"]),
			"settlementName": na.AsString(naming["settlementName"]), "dryRun": false,
		})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(confirmed["success"]); !success {
			return fmt.Errorf("confirm_colony_names refused: %#v", confirmed)
		}
		report["confirmed_colony_names"] = confirmed
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
	report["identity"] = identity
	if err := client.Close(); err != nil {
		return fmt.Errorf("close fixture-prep bridge session: %w", err)
	}
	sessionOpen = false

	// "work" is the lightest family that still produces a routine review with
	// a bound goal; the point is autonomy, not any particular planner.
	launch := na.ServiceLaunch{Binary: binary, Families: []string{"work"}, Extra: append(na.ClockSpeedArgs(), "--resume", "--flight-recorder", filepath.Join(output, "flight.jsonl"))}
	service, err = na.LaunchService(ctx, cfg, gabsExecutable, launch, report)
	if err != nil {
		return err
	}
	firstPID := service.PID
	if _, err := service.SessionToken(); err != nil {
		return err
	}
	attached, err := service.WaitAttached(identity, 90*time.Second)
	if err != nil {
		return err
	}
	report["first_attached"] = attached
	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	first, diagnostics, err := service.WaitRoutineReview(ctx, journal, 120*time.Second)
	report["first_diagnostics"] = diagnostics
	if err != nil {
		journal.Close()
		return fmt.Errorf("first controller never played autonomously: %w", err)
	}
	firstData, _ := json.Marshal(first)
	report["first_review"] = json.RawMessage(firstData)
	reviewed := map[string]any{"colonyId": string(first.Snapshot.Colony), "loadToken": string(first.Snapshot.Load), "mapId": float64(first.Snapshot.Map)}
	if !first.Enabled || !na.MatchesIdentity(reviewed, identity) {
		journal.Close()
		return fmt.Errorf("first review is not an enabled review of the observed world: %#v", first)
	}
	if err := journal.Close(); err != nil {
		return err
	}

	// Kill mid-play. The game keeps running; the service's own GABS
	// subprocess releases the slot shortly after, so the restart may need a
	// few attempts to attach.
	service.Stop()
	report["killed_pid"] = firstPID
	var restarted *na.ServiceProcess
	deadline := time.Now().Add(90 * time.Second)
	for {
		restarted, err = na.LaunchService(ctx, cfg, gabsExecutable, launch, report)
		if err == nil {
			if _, err = restarted.WaitAttached(identity, 30*time.Second); err == nil {
				break
			}
			restarted.Stop()
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("restarted controller never attached: %w", err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(3 * time.Second):
		}
	}
	service = restarted
	if service.PID == firstPID {
		return fmt.Errorf("restart did not produce a new process: pid %d", service.PID)
	}
	report["restarted_pid"] = service.PID
	journal, err = na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	deadline = time.Now().Add(120 * time.Second)
	second := first
	for second.Revision <= first.Revision || !second.Enabled {
		if time.Now().After(deadline) {
			return fmt.Errorf("restarted controller reached automate but the review did not advance past revision %d: %#v", first.Revision, second)
		}
		var diagnostics []map[string]any
		second, diagnostics, err = service.WaitRoutineReview(ctx, journal, time.Until(deadline))
		report["second_diagnostics"] = diagnostics
		if err != nil {
			return fmt.Errorf("restarted controller never resumed autonomous play: %w", err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
	secondData, _ := json.Marshal(second)
	report["second_review"] = json.RawMessage(secondData)
	// The native generation legitimately advances (the stale Auto is revoked
	// and a fresh grant issued); the world and root plan must not.
	if second.Snapshot.Colony != first.Snapshot.Colony || second.Snapshot.Load != first.Snapshot.Load || second.Snapshot.Map != first.Snapshot.Map || second.Snapshot.Plan != first.Snapshot.Plan {
		return fmt.Errorf("restart changed the reviewed world: %#v -> %#v", first.Snapshot, second.Snapshot)
	}
	if second.Snapshot.Native <= first.Snapshot.Native {
		return fmt.Errorf("restart did not reclaim authority at a fresh native generation: %#v -> %#v", first.Snapshot, second.Snapshot)
	}
	report["review_revisions"] = map[string]any{"before_kill": first.Revision, "after_restart": second.Revision}
	return nil
}
