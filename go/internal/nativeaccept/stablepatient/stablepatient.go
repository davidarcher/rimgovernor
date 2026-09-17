// Package stablepatient holds the single-run mechanics behind
// stablepatientaccept (issue #1's "extend stable-patient feeding acceptance
// to withdrawal recovery and concurrent food production"): start a fresh
// debug game, use the disposable test/medical_management_setup and
// test/routine_production_prepare fixtures to build a deterministic stable
// scenario (two tendable Flu patients, a missing-leg surgical patient never
// exercised here, and a fourth colonist forced into GoJuiceAddiction's
// withdrawal stage) plus a concurrent food-production site, launch the live
// Go player-control service with both the tend/medical and food routine
// families enabled, and poll the durable store's CriticalMedicine and
// EnsureFoodSupply goal states over a wall-clock window. This is the
// sustainedfood package's own run shape (load/launch/acquire/poll-the-store)
// reused for a fixture-seeded colony instead of the tribal8 baseline save,
// since medical_management_setup's disposable patients -- not natural colony
// generation -- are what this milestone needs to be deterministic.
package stablepatient

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

// RunConfig mirrors sustainedfood.RunConfig; there is no Save field since
// this run always starts a fresh debug game and seeds it through fixtures.
type RunConfig struct {
	Root              string
	Output            string
	GameID            string
	Headless          bool
	RimgovernorBinary string
	Watch             time.Duration
	Poll              time.Duration
	NativeTimeout     time.Duration
	RequestPrefix     string
	ClockSpeed        string
}

// Run executes one run against a fresh fixture-seeded debug game. cfg.Output
// must be a fresh, empty directory. Every finding goes into report (mutated
// in place); the timeline samples are also returned directly.
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
		prefix = "stable-patient"
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

	// Sequential native sessions, exactly like sustainedfood.Run: this
	// harness's own session starts the debug game and seeds the fixtures,
	// then closes (without games_stop) to free the sole GABP slot for the
	// service. A fresh session is reopened at the end for the final
	// games_stop.
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

	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietRequired); err != nil {
		return nil, err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
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

	medicalPrepared, err := h.Call(ctx, "medical-setup", "test/medical_management_setup", map[string]any{
		"disease": true, "failSurgery": false, "manualTending": false, "withdrawal": true,
	})
	if err != nil {
		return nil, err
	}
	if success, _ := na.AsBool(medicalPrepared["success"]); !success {
		return nil, fmt.Errorf("medical_management_setup refused: %#v", medicalPrepared)
	}
	report["medical_prepared"] = medicalPrepared
	withdrawalPatient := na.AsString(medicalPrepared["withdrawalPatient"])
	if withdrawalPatient == "" {
		return nil, fmt.Errorf("medical_management_setup: missing withdrawalPatient identifier: %#v", medicalPrepared)
	}

	productionPrepared, err := h.Call(ctx, "production-setup", "test/routine_production_prepare", map[string]any{})
	if err != nil {
		return nil, err
	}
	if success, _ := na.AsBool(productionPrepared["success"]); !success {
		return nil, fmt.Errorf("routine_production_prepare refused: %#v", productionPrepared)
	}
	report["production_prepared"] = productionPrepared

	// Free the sole GABP slot before the service starts its own bridge
	// session; this does NOT call games_stop, so the fixture-seeded colony
	// survives.
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
	clockSpeed := cfg.ClockSpeed
	if clockSpeed == "" {
		clockSpeed = "Normal"
	}
	argv := []string{
		"serve", "--clock-speed", clockSpeed,
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
	// Medical: tend for the two Flu patients and the forced withdrawal
	// patient's CriticalMedicine deficit, plus medicine-reserve replenishment
	// so tend never runs the fixture's stocked medicine dry. Food: the same
	// EnsureFoodSupply pipeline sustainedfood exercises, to prove the
	// pre-seeded growing zone/campfire bill keeps advancing concurrently with
	// medical dispatch.
	cmd.Env = append(os.Environ(), "RIMGOVERNOR_ROUTINE_FAMILIES=tend,medical,field,food-storage,acquisition,cooking,supply,production-policy")
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
			return nil, fmt.Errorf("service did not attach to the fixture-seeded save's identity in time: %#v", state)
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
	report["service_state_attached"] = state

	// Resume needs no anchor plan: authority is the world's own root plan,
	// created on first resume (SIMP02, #55).
	acquireBody := map[string]any{
		"requestId": prefix + "-resume-1", "expected": identity,
	}
	acquired, status, err := apiCall("POST", "/api/player/control/resume", acquireBody, token)
	if err != nil {
		return nil, err
	}
	if status != 200 {
		return nil, fmt.Errorf("unexpected acquire status=%d body=%#v", status, acquired)
	}
	acquiredRecord, _ := na.AsMap(acquired["record"])
	if na.AsString(acquiredRecord["phase"]) != "running" {
		return nil, fmt.Errorf("resume was not running: %#v", acquired)
	}
	report["acquired"] = acquired

	verifyStore, err := openStoreWithRetry(ctx, statePath)
	if err != nil {
		return nil, fmt.Errorf("open verification store: %w", err)
	}
	defer verifyStore.Close()

	keepAlive := &authorityKeepAlive{apiCall: apiCall, identity: identity, token: token, prefix: prefix}
	keepAliveCtx, stopKeepAlive := context.WithCancel(ctx)
	var keepAliveWG sync.WaitGroup
	keepAliveWG.Add(1)
	go func() { defer keepAliveWG.Done(); keepAlive.run(keepAliveCtx) }()
	defer func() {
		stopKeepAlive()
		keepAliveWG.Wait()
		report["authority_reacquisitions"] = keepAlive.snapshot()
	}()

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

	// The observation window: sample both CriticalMedicine (the Flu and
	// withdrawal patients' tend deficit) and EnsureFoodSupply (the
	// pre-seeded concurrent growing zone/campfire bill) goal states at
	// cfg.Poll intervals, for cfg.Watch wall-clock duration -- proving tend
	// dispatch and food production progress in the same window rather than
	// one starving the other of pawn time.
	var timeline []map[string]any
	var events []map[string]any
	lastMedicalMethods, lastFoodMethods := -1, -1
	lastMedicalNeed, lastFoodNeed := domain.NeedState(""), domain.NeedState("")
	watchDeadline := time.Now().Add(cfg.Watch)
	for time.Now().Before(watchDeadline) {
		medicalSample, medicalErr := sampleGoal(ctx, verifyStore, policy.CriticalMedicine)
		if medicalErr != nil {
			medicalSample = map[string]any{"error": medicalErr.Error()}
		}
		foodSample, foodErr := sampleGoal(ctx, verifyStore, policy.EnsureFoodSupply)
		if foodErr != nil {
			foodSample = map[string]any{"error": foodErr.Error()}
		}
		sample := map[string]any{
			"at":      time.Now().UTC().Format(time.RFC3339),
			"medical": medicalSample, "food": foodSample,
		}
		timeline = append(timeline, sample)
		medicalMethods, _ := medicalSample["method_count"].(int)
		foodMethods, _ := foodSample["method_count"].(int)
		medicalNeed, _ := medicalSample["need"].(string)
		foodNeed, _ := foodSample["need"].(string)
		if medicalMethods != lastMedicalMethods || domain.NeedState(medicalNeed) != lastMedicalNeed ||
			foodMethods != lastFoodMethods || domain.NeedState(foodNeed) != lastFoodNeed {
			events = append(events, sample)
			lastMedicalMethods, lastFoodMethods = medicalMethods, foodMethods
			lastMedicalNeed, lastFoodNeed = domain.NeedState(medicalNeed), domain.NeedState(foodNeed)
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
	// Deferred LIFO: stop the game while the session is still open, then close.
	defer finalClient.Close()
	defer stopGame(finalClient)

	logData, err := os.ReadFile(naCfg.StartupLogPath())
	if err != nil {
		return timeline, fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), cfg.Headless); err != nil {
		return timeline, err
	}
	return timeline, nil
}

// sampleGoal reads one Need's current goal binding (if any) and its
// committed methods' plan stages, the same shape sustainedfood's own
// sampleFoodGoal reads for EnsureFoodSupply, generalized here to also cover
// CriticalMedicine (RoutineTendPlanner's own goal, routine_tend.go).
func sampleGoal(ctx context.Context, s *store.Store, need policy.GoalID) (map[string]any, error) {
	sample := map[string]any{"method_count": 0}
	review, err := s.LoadRoutineReview(ctx)
	if err != nil {
		return sample, err
	}
	sample["review_revision"] = review.Revision
	sample["review_tick"] = uint64(review.Tick)
	var goalID domain.GoalID
	for _, binding := range review.Goals {
		if binding.Need == need {
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

// authorityKeepAlive is sustainedfood's own keep-alive, unmodified: a
// multi-minute observation window needs the same continuous re-acquisition
// and hold-acknowledgment a real continuously-automating caller would do.
type authorityKeepAlive struct {
	apiCall  func(method, path string, body map[string]any, token string) (map[string]any, int, error)
	identity map[string]any
	token    string
	prefix   string

	mu                sync.Mutex
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
		"attempts": k.attempts, "reacquired": k.reacquired,
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
		k.attempts++
		k.mu.Unlock()
		body := map[string]any{
			"requestId": fmt.Sprintf("%s-reacquire-%d", k.prefix, time.Now().UnixNano()),
			"expected":  k.identity,
		}
		acquired, status, err := k.apiCall("POST", "/api/player/control/resume", body, k.token)
		if err != nil {
			k.mu.Lock()
			k.lastError = err.Error()
			k.mu.Unlock()
			continue
		}
		record, _ := na.AsMap(acquired["record"])
		if status != 200 {
			k.mu.Lock()
			k.lastError = fmt.Sprintf("reacquire status=%d body=%#v", status, acquired)
			k.mu.Unlock()
			continue
		}
		if na.AsString(record["phase"]) != "running" {
			k.mu.Lock()
			k.lastError = fmt.Sprintf("reacquire not running: %#v", acquired)
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
