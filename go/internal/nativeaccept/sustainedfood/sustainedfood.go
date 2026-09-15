// Package sustainedfood holds the single-run mechanics behind
// sustainedfoodaccept and sustainedmatrixaccept (issue #1's sustained-matrix
// acceptance): load a save, launch the live Go player service with the food
// pipeline's routine families, acquire player authority, and poll
// EnsureFoodSupply's durable goal state over a wall-clock window. It exists
// as its own package (rather than living only in cmd/sustainedfoodaccept) so
// sustainedmatrixaccept can drive the exact same mechanics across a save
// variant list without duplicating them.
package sustainedfood

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
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

// RunConfig is one variant's run parameters: which save to load and how long
// to observe it. Root/Output/GameID/Headless/RimgovernorBinary mirror
// na.Config's fields; Save/Watch/Poll/NativeTimeout vary per matrix row.
type RunConfig struct {
	Root              string
	Output            string
	GameID            string
	Headless          bool
	RimgovernorBinary string
	Save              string
	Watch             time.Duration
	Poll              time.Duration
	NativeTimeout     time.Duration
	// RequestPrefix disambiguates the anchor-plan/acquire/keep-alive
	// requestIds across variants sharing one -root's HTTP log; defaults to
	// "sustained-food" when empty.
	RequestPrefix string
	// ClockSpeed is serve's --clock-speed value (Normal, Fast or Superfast);
	// defaults to Normal when empty. A watch window measures wall-clock
	// minutes, not ticks, so requesting Fast/Superfast packs more simulated
	// ticks -- and more chances for EnsureFoodSupply to actually progress --
	// into the same cfg.Watch duration.
	ClockSpeed string
}

// Run executes exactly one variant: it must be called with a fresh, empty
// cfg.Output directory. Every finding goes into report (mutated in place),
// matching every other native acceptance binary's convention; the timeline
// samples are also returned directly so a caller (sustainedmatrixaccept) can
// derive cross-variant metrics without re-reading result.json.
func Run(ctx context.Context, cfg RunConfig, report na.Report) ([]map[string]any, error) {
	root, output := cfg.Root, cfg.Output
	if abs, err := filepath.Abs(root); err == nil {
		root = abs
	}
	if abs, err := filepath.Abs(output); err == nil {
		output = abs
	}
	prefix := cfg.RequestPrefix
	if prefix == "" {
		prefix = "sustained-food"
	}
	naCfg := &na.Config{Root: root, Output: output, Headless: cfg.Headless, GameID: cfg.GameID}
	if err := naCfg.PrepareConfig(); err != nil {
		return nil, fmt.Errorf("prepare profile: %w", err)
	}
	game, err := naCfg.GameSection()
	if err != nil {
		return nil, err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return nil, err
	}
	report["package_files"] = files
	gabsExecutable, err := na.GABSExecutable(root, naCfg.Configuration)
	if err != nil {
		return nil, err
	}

	// Sequential native sessions, exactly like routinehaulaccept: this
	// harness's own session loads the save and reads identity, then closes
	// (without games_stop) to free the sole GABP slot for the service. A
	// fresh session is reopened at the very end for the final games_stop.
	openHarness := func() (*bridge.Client, *na.Harness, error) {
		c, err := na.OpenSession(ctx, gabsExecutable, naCfg.Configuration, cfg.GameID, 60*time.Second)
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
		return nil, err
	}

	if _, err := h.Call(ctx, "load-save", "rimworld/load_game_ready", map[string]any{
		"saveName": cfg.Save, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
	}); err != nil {
		return nil, err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}

	// A fresh load can leave the initial faction/settlement naming dialog
	// open; ConfirmColonyNames (priority 0) is treated as a global emergency
	// that blocks every other goal, including EnsureFoodSupply, until
	// resolved -- see policy.RankDevelopment / routine.go. Dismiss it here,
	// same as routinehaulaccept.
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return nil, err
	}
	if naming, ok := na.AsMap(facts["colonyNaming"]); ok && naming != nil {
		confirmed, err := h.Call(ctx, "confirm-colony-names", "home/confirm_colony_names", map[string]any{
			"windowId":       int(na.AsNumber(naming["windowId"])),
			"factionName":    na.AsString(naming["factionName"]),
			"settlementName": na.AsString(naming["settlementName"]),
			"dryRun":         false,
		})
		if err != nil {
			return nil, err
		}
		if success, _ := na.AsBool(confirmed["success"]); !success {
			return nil, fmt.Errorf("confirm_colony_names refused: %#v", confirmed)
		}
		report["confirmed_colony_names"] = confirmed
	} else {
		report["confirmed_colony_names"] = "no pending naming dialog"
	}

	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return nil, err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return nil, err
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
		return nil, fmt.Errorf("close fixture-prep bridge session: %w", err)
	}

	profileDir := filepath.Join(output, "service-profile")
	if err := os.MkdirAll(profileDir, 0755); err != nil {
		return nil, err
	}
	statePath := filepath.Join(output, "service.sqlite")
	serviceDir := filepath.Join(output, "service")
	if err := os.MkdirAll(serviceDir, 0755); err != nil {
		return nil, err
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
	clockSpeed := cfg.ClockSpeed
	if clockSpeed == "" {
		clockSpeed = "Normal"
	}
	argv := []string{
		"serve", "--player-control", "--clock-control", "--clock-speed", clockSpeed,
		"--routine-reviews", "--routine-methods",
		"--routine-field-plans", "--routine-food-storage-plans", "--routine-acquisition-plans",
		"--routine-cooking-plans", "--routine-supply-plans",
		// Needed only so the executor's ProductionPolicy capability is wired up
		// at all (serve_building.go gates it on this same flag) -- the
		// acquire-anchor below dispatches through that capability. With no
		// --routine-resource-reserve/--routine-resource-stop configured, the
		// routine planner it also enables stays a no-op (see
		// RoutineProductionPolicyPlanner's doc comment: its target
		// floors/stopped set is entirely operator-config-derived).
		"--routine-production-policy-plans",
		"--profile", profileDir,
		"--gabs", gabsExecutable,
		"--config", naCfg.Configuration,
		"--game", cfg.GameID,
		"--state", statePath,
		"--listen", "127.0.0.1:0",
		"--refresh", "1s",
		"--timeout", cfg.NativeTimeout.String(),
	}
	report["service_argv"] = append([]string{cfg.RimgovernorBinary}, argv...)
	cmd := exec.CommandContext(ctx, cfg.RimgovernorBinary, argv...)
	stdoutPipe, err := cmd.StdoutPipe()
	if err != nil {
		return nil, err
	}
	stderrFile, err := os.Create(filepath.Join(serviceDir, "stderr.log"))
	if err != nil {
		return nil, err
	}
	defer stderrFile.Close()
	cmd.Stderr = stderrFile
	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("start rimgovernor serve: %w", err)
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
		return nil, fmt.Errorf("read service startup line: %w", err)
	}
	const startupPrefix = "RimGovernor Go player service: "
	firstLine = strings.TrimSpace(firstLine)
	if !strings.HasPrefix(firstLine, startupPrefix) {
		return nil, fmt.Errorf("unexpected service startup line: %q", firstLine)
	}
	serviceURL := strings.TrimPrefix(firstLine, startupPrefix)
	report["service_url"] = serviceURL
	stdoutLogFile, err := os.Create(filepath.Join(serviceDir, "stdout.log"))
	if err != nil {
		return nil, err
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
		return nil, err
	}
	if status != 200 || na.AsString(health["service"]) != "rimgovernor" || na.AsString(health["backend"]) != "go" || int(na.AsNumber(health["pid"])) != cmd.Process.Pid {
		return nil, fmt.Errorf("unexpected /api/health: status=%d body=%#v", status, health)
	}
	session, status, err := apiCall("GET", "/api/player/session", nil, "")
	if err != nil {
		return nil, err
	}
	if status != 200 || na.AsString(session["mode"]) != "explicit-player" || na.AsString(session["token"]) == "" {
		return nil, fmt.Errorf("unexpected /api/player/session: status=%d body=%#v", status, session)
	}
	token := na.AsString(session["token"])

	deadline := time.Now().Add(90 * time.Second)
	var state map[string]any
	for {
		state, status, err = apiCall("GET", "/api/state", nil, "")
		if err != nil {
			return nil, err
		}
		if status != 200 {
			return nil, fmt.Errorf("unexpected /api/state status=%d body=%#v", status, state)
		}
		if connected, _ := na.AsBool(state["connected"]); connected {
			if svcIdentity, ok := na.AsMap(state["identity"]); ok && matchesIdentity(svcIdentity) {
				break
			}
		}
		if time.Now().After(deadline) {
			return nil, fmt.Errorf("service did not attach to the loaded save's identity in time: %#v", state)
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
	report["service_state_attached"] = state

	// Acquire needs an anchor plan purely because /api/player/control/acquire
	// requires a planId/revision to attach to (store.checkedControl looks the
	// plan up in the shared submissions table) -- it exists only for that.
	// Any submission kind works here since AcquireControl does not care what
	// the plan does, only that it exists, so this uses a resource-policy
	// submission: it commits its own one-action plan the same way a building
	// or zone does (see ResourcePolicySubmissionRequest's doc comment), but
	// unlike either of those its native effect (SetProductionPolicy) is a
	// pure settings write with no pawn labor and no persistent map object --
	// no travel, no haul, no structure or zone left behind. Earlier versions
	// anchored on a real Wall building (which a solo colony's only pawn
	// travels to, hauls for, and builds, competing with EnsureFoodSupply for
	// the one pawn's time -- issue #1) and then on a NothingPreset stockpile
	// zone (still a stockpile zone in principle, and still left a spurious
	// designation on the map for the run's duration). Setting Silver's
	// spending to its own default ("normal") is a genuine no-op: it changes
	// nothing about the colony, just gives AcquireControl something real to
	// attach to.
	submission, status, err := apiCall("POST", "/api/player/resource-policy/update", map[string]any{
		"requestId": prefix + "-anchor-1",
		"expected":  identity,
		"policy":    map[string]any{"resource": "Silver", "spending": "normal"},
	}, token)
	if err != nil {
		return nil, err
	}
	if status != 200 && status != 201 {
		return nil, fmt.Errorf("unexpected anchor plan submission status=%d body=%#v", status, submission)
	}
	planID := na.AsString(submission["planId"])
	revision := na.AsString(submission["revision"])
	if planID == "" || revision == "" {
		return nil, fmt.Errorf("unexpected anchor plan submission: %#v", submission)
	}
	report["anchor_submission"] = submission

	acquireBody := map[string]any{
		"requestId": prefix + "-acquire-1", "expected": identity,
		"planId": planID, "revision": revision, "expectedDirection": "0",
	}
	acquired, status, err := apiCall("POST", "/api/player/control/acquire", acquireBody, token)
	if err != nil {
		return nil, err
	}
	if status != 200 {
		return nil, fmt.Errorf("unexpected acquire status=%d body=%#v", status, acquired)
	}
	acquiredRecord, _ := na.AsMap(acquired["record"])
	if na.AsString(acquiredRecord["phase"]) != "granted" {
		return nil, fmt.Errorf("acquire was not granted: %#v", acquired)
	}
	report["acquired"] = acquired

	verifyStore, err := openStoreWithRetry(ctx, statePath)
	if err != nil {
		return nil, fmt.Errorf("open verification store: %w", err)
	}
	defer verifyStore.Close()

	// Keeps player authority granted for the full watch window: native
	// authority is a bounded generation that legitimately lapses (tick
	// budget exhaustion, an unrecognized native clock event), and nothing
	// re-acquires it automatically -- see routinehaulaccept's
	// authorityKeepAlive doc comment for the full mechanism, reused verbatim
	// here since a multi-minute observation window needs the same recovery.
	keepAlive := &authorityKeepAlive{apiCall: apiCall, identity: identity, planID: planID, revision: revision, token: token, prefix: prefix}
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
			return nil, ctx.Err()
		case <-time.After(2 * time.Second):
		}
	}
	if !sawAutomate {
		return nil, fmt.Errorf("service never reached automate mode after acquire")
	}
	if review.Revision == 0 {
		return nil, fmt.Errorf("service reached automate mode but the routine review was never persisted")
	}

	// The observation window: sample EnsureFoodSupply's goal state and the
	// stage of every one of its committed methods' plans, at cfg.Poll
	// intervals, for cfg.Watch wall-clock duration. Every sample is retained
	// (report["timeline"]); "events" additionally isolates the moments that
	// actually changed (a method count change or a Need transition) so the
	// failure mode is readable without wading through every sample.
	var timeline []map[string]any
	var events []map[string]any
	lastMethodCount := -1
	lastNeed := domain.NeedState("")
	watchDeadline := time.Now().Add(cfg.Watch)
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
			return timeline, ctx.Err()
		case <-time.After(cfg.Poll):
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
			return timeline, fmt.Errorf("reopen bridge session for final games_stop: %w", err)
		}
		select {
		case <-ctx.Done():
			return timeline, ctx.Err()
		case <-time.After(time.Second):
		}
	}
	defer stopGame(finalClient)
	defer finalClient.Close()

	logData, err := os.ReadFile(naCfg.StartupLogPath())
	if err != nil {
		return timeline, fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), cfg.Headless); err != nil {
		return timeline, err
	}
	return timeline, nil
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

// authorityKeepAlive is routinehaulaccept's keep-alive, unmodified except for
// a prefix field so concurrent-looking requestIds across matrix variants
// (sharing one -root's HTTP/service logs) stay distinguishable: a
// multi-minute observation window needs the same continuous re-acquisition
// and hold-acknowledgment a real continuously-automating caller would do.
type authorityKeepAlive struct {
	apiCall  func(method, path string, body map[string]any, token string) (map[string]any, int, error)
	identity map[string]any
	planID   string
	revision string
	token    string
	prefix   string

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
					"requestId":        fmt.Sprintf("%s-ack-%d", k.prefix, time.Now().UnixNano()),
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
			"requestId": fmt.Sprintf("%s-reacquire-%d", k.prefix, time.Now().UnixNano()),
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
