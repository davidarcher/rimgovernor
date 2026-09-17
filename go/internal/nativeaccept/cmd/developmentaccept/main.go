// Command developmentaccept is issue #9's controller-side native acceptance
// for colony-wide development priorities. A resumed controller plays the
// prepared world autonomously while the harness samples the development
// ranking every review records through GET /api/routines, then kills and
// restarts the controller on the same state and keeps sampling. It asserts
// what the controller replay can establish -- admission bounded by the
// project limit, the observed worker count and per-work-type free labor;
// an explicit reason on every deferred goal, with a censused bottleneck on
// labor deferrals; a measured (never unknown) research deficit when a
// research target is configured, proving the review-time research read;
// and waiting ages retained across the restart pair -- and records
// per-goal admission and deferral metrics. It does not establish pawn
// progress on any project: that is sustainedmatrixaccept's campaign
// evidence, gathered separately.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-development-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	binary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	save := flag.String("save", "", "save at profile/Saves/<save>.rws to load; empty starts a fresh debug game")
	families := flag.String("families", "", "RIMGOVERNOR_ROUTINE_FAMILIES for serve; empty composes every family")
	limit := flag.Int("project-limit", 2, "serve's --routine-project-limit")
	minReviews := flag.Int("min-reviews", 2, "distinct review ticks the timeline must contain; fewer means the clock never advanced and the run is vacuous")
	researchTarget := flag.String("research-target", "MicroelectronicsBasics", "serve's --routine-research-target; empty leaves research unranked")
	resourceTargets := flag.String("resource-targets", "WoodLog:400", "comma-separated RESOURCE:TARGET list for --routine-resource-target (requires the resource family)")
	clockSpeed := flag.String("clock-speed", "Fast", "serve's --clock-speed")
	nativeTimeout := flag.Duration("native-timeout", 15*time.Second, "serve's --timeout (native call budget per clock step)")
	watch := flag.Duration("watch", 6*time.Minute, "maximum wall-clock sampling window before the restart; ends early once -min-reviews distinct review ticks were sampled")
	afterRestart := flag.Duration("after-restart", 3*time.Minute, "maximum wall-clock sampling window after the restart; ends early once a review beyond the pre-kill tick was sampled")
	poll := flag.Duration("poll", 5*time.Second, "sampling interval")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall run timeout")
	flag.Parse()
	if *root == "" || *binary == "" || !filepath.IsAbs(*binary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-development-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Development priorities (#9): a resumed controller's recorded ranking is sampled through /api/routines across a kill-and-restart pair; admission stays within the project limit, worker count and free labor, every deferral carries a reason, a configured research target is measured at review time, and waiting ages survive the restart. Pawn progress is out of scope.", !*rendered)
	cfg := runConfig{root: *root, output: *output, gameID: *game, headless: !*rendered, binary: *binary, save: *save, families: *families, limit: *limit, minReviews: *minReviews,
		researchTarget: *researchTarget, resourceTargets: *resourceTargets, clockSpeed: *clockSpeed, nativeTimeout: *nativeTimeout, watch: *watch, afterRestart: *afterRestart, poll: *poll}
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, cfg, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

type runConfig struct {
	root, output, gameID, binary, save, families, researchTarget, resourceTargets, clockSpeed string
	headless                                                                                  bool
	limit, minReviews                                                                         int
	nativeTimeout, watch, afterRestart, poll                                                  time.Duration
}

func run(ctx context.Context, c runConfig, report na.Report) error {
	root, output := c.root, c.output
	if abs, err := filepath.Abs(root); err == nil {
		root = abs
	}
	if abs, err := filepath.Abs(output); err == nil {
		output = abs
	}
	cfg := &na.Config{Root: root, Output: output, Headless: c.headless, GameID: c.gameID}
	// The save carries its own expansion list; a Core-only profile would refuse it.
	if err := cfg.UseSaveExpansions(c.save); err != nil {
		return err
	}
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
	client, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, c.gameID, 120*time.Second)
	if err != nil {
		return err
	}
	h := na.NewHarness(client, output)
	// The harness closes its GABS session while a controller owns the sole
	// slot and reopens one to stop the game; see cmd/restartaccept.
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
			reopened, err := na.ReopenSession(stopCtx, gabsExecutable, cfg.Configuration, c.gameID)
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

	if c.save != "" {
		if _, err := h.Call(ctx, "load-save", "rimworld/load_game_ready", map[string]any{
			"saveName": c.save, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
		}); err != nil {
			return err
		}
	} else if _, err := na.StartDebugGame(ctx, h, nil, na.QuietIfAvailable); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	// A pending naming dialog is a priority-0 emergency that would hold every
	// optional goal for the whole window; dismiss it like restartaccept does.
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

	extra := []string{"--resume", "--clock-speed", c.clockSpeed, "--routine-project-limit", fmt.Sprint(c.limit), "--flight-recorder", filepath.Join(output, "flight.jsonl")}
	if c.researchTarget != "" {
		extra = append(extra, "--routine-research-target", c.researchTarget)
	}
	for _, t := range strings.Split(c.resourceTargets, ",") {
		if t = strings.TrimSpace(t); t != "" {
			extra = append(extra, "--routine-resource-target", t)
		}
	}
	var fams []string
	for _, f := range strings.Split(c.families, ",") {
		if f = strings.TrimSpace(f); f != "" {
			fams = append(fams, f)
		}
	}
	launch := na.ServiceLaunch{Binary: c.binary, Families: fams, Extra: extra}
	// LaunchService's fixed --timeout is the per-step native budget; a
	// full composition on a populated map needs the caller's value instead.
	launch.Extra = append(launch.Extra, "--timeout", c.nativeTimeout.String())
	service, err = na.LaunchService(ctx, cfg, gabsExecutable, launch, report)
	if err != nil {
		return err
	}
	firstPID := service.PID
	if _, err := service.SessionToken(); err != nil {
		return err
	}
	if _, err := service.WaitAttached(identity, 90*time.Second); err != nil {
		return err
	}
	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	first, diagnostics, err := service.WaitRoutineReview(ctx, journal, 180*time.Second)
	journal.Close()
	report["first_diagnostics"] = diagnostics
	if err != nil {
		return fmt.Errorf("controller never played autonomously: %w", err)
	}
	report["first_review_revision"] = first.Revision

	var samples []Sample
	var violations []string
	// sampleFor polls until window elapses or enough has been seen: the
	// windows are ceilings, so a healthy box finishes as soon as the clock
	// has produced the reviews the verdict needs.
	sampleFor := func(phase string, window time.Duration, enough func() bool) error {
		deadline := time.Now().Add(window)
		for time.Now().Before(deadline) && !enough() {
			status, code, err := service.API("GET", "/api/routines", nil, "")
			if err != nil {
				return fmt.Errorf("%s: /api/routines: %w", phase, err)
			}
			if code != 200 {
				return fmt.Errorf("%s: /api/routines status %d: %#v", phase, code, status)
			}
			s, err := decodeSample(status, phase, time.Now())
			if err != nil {
				return err
			}
			samples = append(samples, s)
			for _, v := range checkSample(s, c.limit, c.researchTarget) {
				violations = append(violations, fmt.Sprintf("%s sample %d (tick %d): %s", phase, len(samples), tickOf(s), v))
			}
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(c.poll):
			}
		}
		return nil
	}
	distinctTicks := func(phase string, above int64) int {
		seen := map[int64]bool{}
		for _, s := range samples {
			if s.Phase == phase && s.Development != nil && s.Development.Tick > above {
				seen[s.Development.Tick] = true
			}
		}
		return len(seen)
	}
	if err := sampleFor("before-restart", c.watch, func() bool { return distinctTicks("before-restart", -1) >= c.minReviews }); err != nil {
		return err
	}
	var before *Development
	for i := len(samples) - 1; i >= 0; i-- {
		if samples[i].Development != nil {
			before = samples[i].Development
			break
		}
	}
	if before == nil {
		return fmt.Errorf("no development ranking was recorded in %s of autonomous play", c.watch)
	}

	// Kill mid-play and restart on the same state; the service's own GABS
	// subprocess releases the slot shortly after, so attaching may retry.
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
	report["restarted_pid"] = service.PID
	journal, err = na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	deadline = time.Now().Add(180 * time.Second)
	second := first
	for second.Revision <= first.Revision || !second.Enabled {
		if time.Now().After(deadline) {
			return fmt.Errorf("restarted controller did not advance the review past revision %d", first.Revision)
		}
		second, _, err = service.WaitRoutineReview(ctx, journal, time.Until(deadline))
		if err != nil {
			return fmt.Errorf("restarted controller never resumed autonomous play: %w", err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
	report["restart_review_revisions"] = map[string]any{"before_kill": first.Revision, "after_restart": second.Revision}
	if err := sampleFor("after-restart", c.afterRestart, func() bool { return distinctTicks("after-restart", before.Tick) >= 1 }); err != nil {
		return err
	}
	var after *Development
	for i := range samples {
		if samples[i].Phase == "after-restart" && samples[i].Development != nil {
			after = samples[i].Development
			break
		}
	}
	if after == nil {
		return fmt.Errorf("no development ranking was recorded after the restart")
	}
	for _, v := range checkRestart(*before, *after) {
		violations = append(violations, "restart: "+v)
	}

	timeline, _ := json.MarshalIndent(samples, "", "  ")
	_ = os.WriteFile(filepath.Join(output, "timeline.json"), timeline, 0644)
	metrics := deriveMetrics(samples)
	report["metrics"] = metrics
	report["violations"] = violations
	report["before_restart"] = before
	report["after_restart"] = after
	if len(metrics.Goals) == 0 {
		return fmt.Errorf("no optional goal was ever ranked; the run is vacuous")
	}
	if metrics.Reviews < c.minReviews {
		return fmt.Errorf("only %d distinct review tick(s) sampled (minimum %d); the clock never advanced past the resumed review", metrics.Reviews, c.minReviews)
	}
	if c.researchTarget != "" && !metrics.ResearchRanked {
		return fmt.Errorf("research target %q never produced a ranked EnsureResearch row", c.researchTarget)
	}
	if len(violations) > 0 {
		return fmt.Errorf("%d invariant violation(s); first: %s", len(violations), violations[0])
	}
	return nil
}

func tickOf(s Sample) int64 {
	if s.Development == nil {
		return -1
	}
	return s.Development.Tick
}
