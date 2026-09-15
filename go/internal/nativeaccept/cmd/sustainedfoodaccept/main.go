// Command sustainedfoodaccept is a diagnostic (not pass/fail) native
// acceptance run for issue #1's "crop labor and interim food before rations
// run out" eight-colonist startup deficit: it loads the real
// RimGovernor-tribal8-baseline.rws save, launches the live Go player-control
// service (every routine family on, per go/README.md's "naming zero of those
// flags turns all of them on"), acquires player authority, and then polls the
// durable store's EnsureFoodSupply goal state over a long wall-clock window
// to build a timeline of its Status/Need/Priority and committed methods --
// evidence for exactly where a real 8-colonist campaign's crop-replacement
// loop stalls (prior campaign evidence: commit 1c6c8af1's crop-eight-01
// "blocked at tick 272000 with zero food runway before sustained crop
// replacement").
//
// Unlike routinehaulaccept, this tool never needs a fixture (natural colony
// generation from the baseline save is exactly what's under test) and never
// reopens its own native session mid-run: every sample after the service
// starts comes from the durable SQLite journal (store.Store), which is safe
// to read concurrently with the service's own native session because it
// never touches the single shared GABP slot -- see routinehaulaccept's own
// comment on why only one native session can be connected at a time.
package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const baselineSave = "RimGovernor-tribal8-baseline"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-sustained-food-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	rimgovernorBinary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	watch := flag.Duration("watch", 20*time.Minute, "wall-clock duration to observe EnsureFoodSupply after authority is acquired")
	poll := flag.Duration("poll", 5*time.Second, "sampling interval during the watch window")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall run timeout (must exceed -watch plus startup/shutdown)")
	nativeTimeout := flag.Duration("native-timeout", 15*time.Second, "serve subprocess's own --timeout (native call budget per ClockScheduler.Step, shared across every chained routine planner in that step)")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *rimgovernorBinary == "" {
		fmt.Fprintln(os.Stderr, "-rimgovernor is required (absolute path to a prebuilt rimgovernor binary)")
		os.Exit(2)
	}
	if !filepath.IsAbs(*rimgovernorBinary) {
		fmt.Fprintln(os.Stderr, "-rimgovernor must be an absolute path")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-sustained-food-acceptance"
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	entries, _ := os.ReadDir(*output)
	if len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Diagnostic: EnsureFoodSupply goal-state timeline against the real "+
		"RimGovernor-tribal8-baseline save under the live routine reviewer/field planner, evidence for "+
		"issue #1's eight-colonist crop labor / interim food deficit. Not a pass/fail acceptance gate.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, *rimgovernorBinary, *watch, *poll, *nativeTimeout, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, rimgovernorBinary string, watch, poll, nativeTimeout time.Duration, report na.Report) error {
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

	// Sequential native sessions, exactly like routinehaulaccept: this
	// harness's own session loads the baseline save and reads identity, then
	// closes (without games_stop) to free the sole GABP slot for the
	// service. A fresh session is reopened at the very end for the final
	// games_stop.
	openHarness := func() (*bridge.Client, *na.Harness, error) {
		c, err := na.OpenSession(ctx, gabsExecutable, cfg.Configuration, gameID, 60*time.Second)
		if err != nil {
			return nil, nil, err
		}
		return c, na.NewHarness(c, output), nil
	}
	stopGame := func(c *bridge.Client) {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer stopCancel()
		if stopped, err := c.GamesStop(stopCtx); err == nil {
			report["stop"] = string(stopped.Envelope)
		} else {
			report["stop_error"] = err.Error()
		}
	}

	client, h, err := openHarness()
	if err != nil {
		return err
	}

	if _, err := h.Call(ctx, "load-baseline", "rimworld/load_game_ready", map[string]any{
		"saveName": baselineSave, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
	}); err != nil {
		return err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}

	// A fresh load can leave the initial faction/settlement naming dialog
	// open; ConfirmColonyNames (priority 0) is treated as a global emergency
	// that blocks every other goal, including EnsureFoodSupply, until
	// resolved -- see policy.RankDevelopment / routine.go. Dismiss it here,
	// same as routinehaulaccept.
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return err
	}
	if naming, ok := na.AsMap(facts["colonyNaming"]); ok && naming != nil {
		confirmed, err := h.Call(ctx, "confirm-colony-names", "home/confirm_colony_names", map[string]any{
			"windowId":       int(na.AsNumber(naming["windowId"])),
			"factionName":    na.AsString(naming["factionName"]),
			"settlementName": na.AsString(naming["settlementName"]),
			"dryRun":         false,
		})
		if err != nil {
			return err
		}
		if success, _ := na.AsBool(confirmed["success"]); !success {
			return fmt.Errorf("confirm_colony_names refused: %#v", confirmed)
		}
		report["confirmed_colony_names"] = confirmed
	} else {
		report["confirmed_colony_names"] = "no pending naming dialog"
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
	matchesIdentity := func(v map[string]any) bool {
		return na.AsString(v["colonyId"]) == na.AsString(identity["colonyId"]) &&
			na.AsString(v["loadToken"]) == na.AsString(identity["loadToken"]) &&
			na.AsNumber(v["mapId"]) == na.AsNumber(identity["mapId"])
	}

	// Free the sole GABP slot before the service starts its own bridge
	// session; this does NOT call games_stop, so the loaded save survives.
	if err := client.Close(); err != nil {
		return fmt.Errorf("close fixture-prep bridge session: %w", err)
	}

	profileDir := filepath.Join(output, "service-profile")
	if err := os.MkdirAll(profileDir, 0755); err != nil {
		return err
	}
	statePath := filepath.Join(output, "service.sqlite")
	serviceDir := filepath.Join(output, "service")
	if err := os.MkdirAll(serviceDir, 0755); err != nil {
		return err
	}
	// Explicit food-pipeline routine plans only -- NOT the "name zero
	// --routine-*-plans flags to enable every family" composed default.
	// --routine-recovery-plans (and likely others gated the same way) is
	// unusable in that composed-default combination: RecoverDisasterServices
	// is only added to DetectRoutine's recognized-assessments set when
	// r.Disaster != nil (routine.go:751), which is never true during
	// NewRoutineReviewer's empty-facts capability validation call
	// (routine.go:59), so the service fails immediately with "invalid
	// routine method capability" whenever recovery-plans is combined with
	// every other family this way. That's a narrow pre-existing gap
	// unrelated to food/crop logic and out of this milestone's scope --
	// sidestep it by requesting only what EnsureFoodSupply's own pipeline
	// needs: field growing (the goal under diagnosis), food storage,
	// harvest/wood acquisition, cooking bills, and starting supplies.
	argv := []string{
		"serve", "--player-control", "--clock-control", "--routine-reviews", "--routine-methods",
		"--routine-field-plans", "--routine-food-storage-plans", "--routine-acquisition-plans",
		"--routine-cooking-plans", "--routine-supply-plans",
		"--profile", profileDir,
		"--gabs", gabsExecutable,
		"--config", cfg.Configuration,
		"--game", gameID,
		"--state", statePath,
		"--listen", "127.0.0.1:0",
		"--refresh", "1s",
		"--timeout", nativeTimeout.String(),
	}
	report["service_argv"] = append([]string{rimgovernorBinary}, argv...)
	cmd := exec.CommandContext(ctx, rimgovernorBinary, argv...)
	stdoutPipe, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	stderrFile, err := os.Create(filepath.Join(serviceDir, "stderr.log"))
	if err != nil {
		return err
	}
	defer stderrFile.Close()
	cmd.Stderr = stderrFile
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("start rimgovernor serve: %w", err)
	}
	report["service_pid"] = cmd.Process.Pid
	serviceDone := make(chan error, 1)
	go func() { serviceDone <- cmd.Wait() }()
	serviceStopped := false
	stopService := func() {
		if serviceStopped {
			return
		}
		serviceStopped = true
		if cmd.ProcessState == nil {
			_ = cmd.Process.Kill()
		}
		<-serviceDone
	}
	defer stopService()

	reader := bufio.NewReader(stdoutPipe)
	firstLine, err := reader.ReadString('\n')
	if err != nil {
		return fmt.Errorf("read service startup line: %w", err)
	}
	const prefix = "RimGovernor Go player service: "
	firstLine = strings.TrimSpace(firstLine)
	if !strings.HasPrefix(firstLine, prefix) {
		return fmt.Errorf("unexpected service startup line: %q", firstLine)
	}
	serviceURL := strings.TrimPrefix(firstLine, prefix)
	report["service_url"] = serviceURL
	stdoutLogFile, err := os.Create(filepath.Join(serviceDir, "stdout.log"))
	if err != nil {
		return err
	}
	defer stdoutLogFile.Close()
	go func() { _, _ = io.Copy(stdoutLogFile, reader) }()

	httpClient := &http.Client{Timeout: 20 * time.Second}
	apiCall := func(method, path string, body map[string]any, token string) (map[string]any, int, error) {
		var reqBody io.Reader
		if body != nil {
			data, err := json.Marshal(body)
			if err != nil {
				return nil, 0, err
			}
			reqBody = bytes.NewReader(data)
		}
		req, err := http.NewRequestWithContext(ctx, method, serviceURL+path, reqBody)
		if err != nil {
			return nil, 0, err
		}
		if body != nil {
			req.Header.Set("Content-Type", "application/json")
		}
		if token != "" {
			req.Header.Set("X-RimGovernor-Player", token)
		}
		req.Header.Set("Origin", serviceURL)
		resp, err := httpClient.Do(req)
		if err != nil {
			return nil, 0, err
		}
		defer resp.Body.Close()
		data, err := io.ReadAll(resp.Body)
		if err != nil {
			return nil, 0, err
		}
		var out map[string]any
		if len(data) > 0 {
			if err := json.Unmarshal(data, &out); err != nil {
				return nil, resp.StatusCode, fmt.Errorf("decode %s %s: %w", method, path, err)
			}
		}
		return out, resp.StatusCode, nil
	}

	health, status, err := apiCall("GET", "/api/health", nil, "")
	if err != nil {
		return err
	}
	if status != 200 || na.AsString(health["service"]) != "rimgovernor" || na.AsString(health["backend"]) != "go" || int(na.AsNumber(health["pid"])) != cmd.Process.Pid {
		return fmt.Errorf("unexpected /api/health: status=%d body=%#v", status, health)
	}
	session, status, err := apiCall("GET", "/api/player/session", nil, "")
	if err != nil {
		return err
	}
	if status != 200 || na.AsString(session["mode"]) != "explicit-player" || na.AsString(session["token"]) == "" {
		return fmt.Errorf("unexpected /api/player/session: status=%d body=%#v", status, session)
	}
	token := na.AsString(session["token"])

	deadline := time.Now().Add(90 * time.Second)
	var state map[string]any
	for {
		state, status, err = apiCall("GET", "/api/state", nil, "")
		if err != nil {
			return err
		}
		if status != 200 {
			return fmt.Errorf("unexpected /api/state status=%d body=%#v", status, state)
		}
		if connected, _ := na.AsBool(state["connected"]); connected {
			if svcIdentity, ok := na.AsMap(state["identity"]); ok && matchesIdentity(svcIdentity) {
				break
			}
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("service did not attach to the loaded save's identity in time: %#v", state)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
	report["service_state_attached"] = state

	// Acquire needs an anchor plan; its own fate is irrelevant to this
	// diagnostic (EnsureFoodSupply is priority 2 and bypasses
	// policy.RankDevelopment's capacity arbitration entirely -- see
	// routine.go's addGoal(EnsureFoodSupply, 2) and development.go's
	// `if g.Priority < 3 { continue }`), it exists only because
	// /api/player/control/acquire requires a planId/revision.
	submission, status, err := apiCall("POST", "/api/buildings/plans", map[string]any{
		"requestId": "sustained-food-anchor-1",
		"expected":  identity,
		"building":  map[string]any{"defName": "Wall", "x": 10, "z": 10, "rotation": "north", "stuff": "WoodLog"},
	}, token)
	if err != nil {
		return err
	}
	if status != 200 && status != 201 {
		return fmt.Errorf("unexpected anchor plan submission status=%d body=%#v", status, submission)
	}
	planID := na.AsString(submission["planId"])
	revision := na.AsString(submission["revision"])
	if planID == "" || revision == "" {
		return fmt.Errorf("unexpected anchor plan submission: %#v", submission)
	}
	report["anchor_submission"] = submission

	acquireBody := map[string]any{
		"requestId": "sustained-food-acquire-1", "expected": identity,
		"planId": planID, "revision": revision, "expectedDirection": "0",
	}
	acquired, status, err := apiCall("POST", "/api/player/control/acquire", acquireBody, token)
	if err != nil {
		return err
	}
	if status != 200 {
		return fmt.Errorf("unexpected acquire status=%d body=%#v", status, acquired)
	}
	acquiredRecord, _ := na.AsMap(acquired["record"])
	if na.AsString(acquiredRecord["phase"]) != "granted" {
		return fmt.Errorf("acquire was not granted: %#v", acquired)
	}
	report["acquired"] = acquired

	verifyStore, err := openStoreWithRetry(ctx, statePath)
	if err != nil {
		return fmt.Errorf("open verification store: %w", err)
	}
	defer verifyStore.Close()

	// Keeps player authority granted for the full watch window: native
	// authority is a bounded generation that legitimately lapses (tick
	// budget exhaustion, an unrecognized native clock event), and nothing
	// re-acquires it automatically -- see routinehaulaccept's
	// authorityKeepAlive doc comment for the full mechanism, reused verbatim
	// here since a multi-minute observation window needs the same recovery.
	keepAlive := &authorityKeepAlive{apiCall: apiCall, identity: identity, planID: planID, revision: revision, token: token}
	keepAlive.direction = na.AsString(acquiredRecord["direction"])
	keepAliveCtx, stopKeepAlive := context.WithCancel(ctx)
	var keepAliveWG sync.WaitGroup
	keepAliveWG.Add(1)
	go func() { defer keepAliveWG.Done(); keepAlive.run(keepAliveCtx) }()
	defer func() {
		stopKeepAlive()
		keepAliveWG.Wait()
		report["authority_reacquisitions"] = keepAlive.snapshot()
	}()

	// Confirm the scheduler actually reaches automate and a routine review
	// gets persisted before starting the real observation window.
	diagDeadline := time.Now().Add(60 * time.Second)
	sawAutomate := false
	var review store.RoutineReview
	for time.Now().Before(diagDeadline) {
		st, _, _ := apiCall("GET", "/api/state", nil, "")
		if na.AsString(st["mode"]) == "automate" {
			sawAutomate = true
		}
		if r, err := verifyStore.LoadRoutineReview(ctx); err == nil {
			review = r
			if review.Revision > 0 {
				break
			}
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
	if !sawAutomate {
		return fmt.Errorf("service never reached automate mode after acquire")
	}
	if review.Revision == 0 {
		return fmt.Errorf("service reached automate mode but the routine review was never persisted")
	}

	// The observation window: sample EnsureFoodSupply's goal state and the
	// stage of every one of its committed methods' plans, at -poll
	// intervals, for -watch wall-clock duration. Every sample is retained
	// (report["timeline"]); "events" additionally isolates the moments that
	// actually changed (a method count change or a Need transition) so the
	// failure mode is readable without wading through every sample.
	var timeline []map[string]any
	var events []map[string]any
	lastMethodCount := -1
	lastNeed := domain.NeedState("")
	watchDeadline := time.Now().Add(watch)
	for time.Now().Before(watchDeadline) {
		sample, err := sampleFoodGoal(ctx, verifyStore)
		if err != nil {
			sample = map[string]any{"error": err.Error(), "at": time.Now().UTC().Format(time.RFC3339)}
		}
		timeline = append(timeline, sample)
		methodCount, _ := sample["method_count"].(int)
		need, _ := sample["need"].(string)
		if methodCount != lastMethodCount || domain.NeedState(need) != lastNeed {
			events = append(events, sample)
			lastMethodCount, lastNeed = methodCount, domain.NeedState(need)
		}
		select {
		case <-ctx.Done():
			report["timeline"] = timeline
			report["events"] = events
			return ctx.Err()
		case <-time.After(poll):
		}
	}
	report["timeline"] = timeline
	report["events"] = events
	report["timeline_samples"] = len(timeline)

	verifyStore.Close()
	stopService()
	var finalClient *bridge.Client
	reopenDeadline := time.Now().Add(30 * time.Second)
	for {
		finalClient, _, err = openHarness()
		if err == nil {
			break
		}
		if time.Now().After(reopenDeadline) {
			return fmt.Errorf("reopen bridge session for final games_stop: %w", err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(time.Second):
		}
	}
	defer stopGame(finalClient)
	defer finalClient.Close()

	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// sampleFoodGoal reads the current EnsureFoodSupply goal binding (if any) and
// its committed methods' plan stages, mirroring exactly what
// buildingruntime.RoutineFieldPlanner.step itself reads: review.Goals for the
// EnsureFoodSupply Need, then that goal's Status/Need/Priority/Methods.
func sampleFoodGoal(ctx context.Context, s *store.Store) (map[string]any, error) {
	sample := map[string]any{"at": time.Now().UTC().Format(time.RFC3339), "method_count": 0}
	review, err := s.LoadRoutineReview(ctx)
	if err != nil {
		return sample, err
	}
	sample["review_revision"] = review.Revision
	sample["review_tick"] = uint64(review.Tick)
	sample["latch_food"] = review.Latches.Food
	var goalID domain.GoalID
	for _, binding := range review.Goals {
		if binding.Need == policy.EnsureFoodSupply {
			goalID = binding.Goal
			break
		}
	}
	if goalID == "" {
		sample["goal_bound"] = false
		return sample, nil
	}
	sample["goal_bound"] = true
	goal, err := s.LoadGoal(ctx, goalID)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return sample, nil
		}
		return sample, err
	}
	sample["status"] = string(goal.Goal.Status)
	sample["need"] = string(goal.Goal.Need)
	sample["priority"] = goal.Goal.Priority
	sample["epoch"] = goal.Goal.Epoch
	sample["method_count"] = len(goal.Methods)
	var plans []map[string]any
	for _, method := range goal.Methods {
		plan, err := s.LoadPlan(ctx, method.Plan)
		if err != nil {
			plans = append(plans, map[string]any{"plan": string(method.Plan), "error": err.Error()})
			continue
		}
		stages := map[string]int{}
		for _, p := range plan.Progress {
			stages[string(p.View().Stage)]++
		}
		plans = append(plans, map[string]any{"plan": string(method.Plan), "actions": len(plan.Spec.Actions()), "stages": stages})
	}
	sample["plans"] = plans
	return sample, nil
}

// authorityKeepAlive is routinehaulaccept's keep-alive, unmodified: a
// multi-minute observation window needs the same continuous re-acquisition
// and hold-acknowledgment a real continuously-automating caller would do.
type authorityKeepAlive struct {
	apiCall  func(method, path string, body map[string]any, token string) (map[string]any, int, error)
	identity map[string]any
	planID   string
	revision string
	token    string

	mu                sync.Mutex
	direction         string
	attempts          int
	reacquired        int
	acknowledged      int
	acknowledgeFailed int
	lastError         string
}

func (k *authorityKeepAlive) snapshot() map[string]any {
	k.mu.Lock()
	defer k.mu.Unlock()
	out := map[string]any{
		"attempts": k.attempts, "reacquired": k.reacquired, "final_direction": k.direction,
		"acknowledged": k.acknowledged, "acknowledge_failed": k.acknowledgeFailed,
	}
	if k.lastError != "" {
		out["last_error"] = k.lastError
	}
	return out
}

func (k *authorityKeepAlive) run(ctx context.Context) {
	for {
		select {
		case <-ctx.Done():
			return
		case <-time.After(2 * time.Second):
		}
		state, status, err := k.apiCall("GET", "/api/state", nil, "")
		if err != nil || status != 200 {
			continue
		}
		if na.AsString(state["mode"]) == "automate" {
			continue
		}
		if clk, clkStatus, clkErr := k.apiCall("GET", "/api/player/clock", nil, ""); clkErr == nil && clkStatus == 200 {
			if holds := na.AsSlice(clk["holds"]); len(holds) > 0 {
				ackBody := map[string]any{
					"requestId":        fmt.Sprintf("sustained-food-ack-%d", time.Now().UnixNano()),
					"expectedRevision": na.AsString(clk["revision"]),
					"throughCursor":    na.AsString(clk["inboxCursor"]),
				}
				if _, ackStatus, ackErr := k.apiCall("POST", "/api/player/clock/acknowledge", ackBody, k.token); ackErr != nil || ackStatus != 200 {
					k.mu.Lock()
					if ackErr != nil {
						k.lastError = "acknowledge: " + ackErr.Error()
					} else {
						k.lastError = fmt.Sprintf("acknowledge status=%d", ackStatus)
					}
					k.acknowledgeFailed++
					k.mu.Unlock()
				} else {
					k.mu.Lock()
					k.acknowledged++
					k.mu.Unlock()
				}
			}
		}
		k.mu.Lock()
		direction := k.direction
		k.attempts++
		k.mu.Unlock()
		body := map[string]any{
			"requestId": fmt.Sprintf("sustained-food-reacquire-%d", time.Now().UnixNano()),
			"expected":  k.identity, "planId": k.planID, "revision": k.revision,
			"expectedDirection": direction,
		}
		acquired, status, err := k.apiCall("POST", "/api/player/control/acquire", body, k.token)
		if err != nil {
			k.mu.Lock()
			k.lastError = err.Error()
			k.mu.Unlock()
			continue
		}
		record, _ := na.AsMap(acquired["record"])
		if observed := na.AsString(record["direction"]); observed != "" {
			k.mu.Lock()
			k.direction = observed
			k.mu.Unlock()
		} else if state, ok := na.AsMap(acquired["state"]); ok {
			if generation, ok := na.AsMap(state["generation"]); ok {
				if observed := na.AsString(generation["direction"]); observed != "" {
					k.mu.Lock()
					k.direction = observed
					k.mu.Unlock()
				}
			}
		}
		if status != 200 {
			k.mu.Lock()
			k.lastError = fmt.Sprintf("reacquire status=%d body=%#v", status, acquired)
			k.mu.Unlock()
			continue
		}
		if na.AsString(record["phase"]) != "granted" {
			k.mu.Lock()
			k.lastError = fmt.Sprintf("reacquire not granted: %#v", acquired)
			k.mu.Unlock()
			continue
		}
		k.mu.Lock()
		k.reacquired++
		k.lastError = ""
		k.mu.Unlock()
	}
}

func openStoreWithRetry(ctx context.Context, path string) (*store.Store, error) {
	deadline := time.Now().Add(60 * time.Second)
	for {
		s, err := store.Open(ctx, path)
		if err == nil {
			return s, nil
		}
		if time.Now().After(deadline) {
			return nil, err
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
}
