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
	"sync/atomic"
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
	// Families is serve's RIMGOVERNOR_ROUTINE_FAMILIES value; empty composes
	// only EnsureFoodSupply's own pipeline and "all" the autonomous default. Goal is the maintained goal the
	// timeline samples (default EnsureFoodSupply). Until, when set, ends the
	// watch window early once a sample satisfies it. Audit, when set, runs
	// against a fresh bridge session after the service has stopped and
	// before the game is stopped, so a harness can compare the durable
	// journal against live native facts.
	// ServeArgs are appended to the serve argv verbatim (for example a
	// --routine-resource-target); Prepare, when set, runs against the
	// fixture-prep bridge session after the save is loaded and the naming
	// dialog dismissed, before the service starts, so a harness can record
	// the live baseline its audit later compares against.
	Families  string
	Goal      policy.GoalID
	ServeArgs []string
	Prepare   func(ctx context.Context, h *na.Harness, report na.Report) error
	Until     func(sample map[string]any) bool
	Audit     func(ctx context.Context, h *na.Harness, report na.Report) error
	// Reuse, when set, runs this variant as one case of an already-launched
	// game (issue #22): the save is loaded through Reuse.BeginCase into the
	// running process instead of a fresh games_start, and the run ends with
	// Reuse.EndCase instead of games_stop. The caller owns the lifecycle and
	// its final Retire. Root/GameID/Headless must match Reuse.Config.
	Reuse *na.GameReuse
	// Checkpoint, when set, saves the live game the first time a sample
	// satisfies When: the clock is paused, the service's lifecycle save
	// writes Name, the .rws is copied into root/profile/Saves (the durable
	// location Prepare mirrors into the headless profile), and the clock
	// resumes. A later run started with -save Name skips the startup ladder
	// and begins where this run got interesting.
	Checkpoint *Checkpoint
}

// Checkpoint names a save to take mid-run and the sample that triggers it.
type Checkpoint struct {
	Name string
	When func(sample map[string]any) bool
}

// Run executes exactly one variant: it must be called with a fresh, empty
// cfg.Output directory. Every finding goes into report (mutated in place),
// matching every other native acceptance binary's convention; the timeline
// samples are also returned directly so a caller (sustainedmatrixaccept) can
// derive cross-variant metrics without re-reading result.json.
func Run(ctx context.Context, cfg RunConfig, report na.Report) (timeline []map[string]any, err error) {
	// A reuse case that fails anywhere below retires the shared game; a
	// successful one is verified quiescent by EndCase. This defer runs after
	// the service's own deferred stop, so the GABP slot is free again.
	var reuseCase *na.ReuseCase
	defer func() {
		if reuseCase == nil {
			return
		}
		if endErr := cfg.Reuse.EndCase(ctx, reuseCase, err != nil); endErr != nil && err == nil {
			err = endErr
		}
	}()
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
	// The save carries its own expansion list; a Core-only profile would refuse it.
	if err := naCfg.UseSaveExpansions(cfg.Save); err != nil {
		return nil, fmt.Errorf("prepare profile: %w", err)
	}
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

	var client *bridge.Client
	var h *na.Harness
	if cfg.Reuse != nil {
		reuseCase, err = cfg.Reuse.BeginCase(ctx, filepath.Base(output), cfg.Save, output)
		if err != nil {
			return nil, err
		}
		h = reuseCase.Harness
		report["reuse_case"] = map[string]any{"loadToken": reuseCase.Reset.LoadToken, "tick": reuseCase.Reset.Tick}
	} else {
		client, h, err = openHarness()
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

	if cfg.Prepare != nil {
		if err := cfg.Prepare(ctx, h, report); err != nil {
			return nil, fmt.Errorf("prepare: %w", err)
		}
	}

	// Free the sole GABP slot before the service starts its own bridge
	// session; this does NOT call games_stop, so the loaded save survives.
	if reuseCase != nil {
		if err := cfg.Reuse.ReleaseSession(); err != nil {
			return nil, fmt.Errorf("release reuse bridge session: %w", err)
		}
	} else if err := client.Close(); err != nil {
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
	// Compose only EnsureFoodSupply's own pipeline so the food outcome under
	// diagnosis is not confounded by other families: field growing, food
	// storage, harvest/wood acquisition, cooking bills and starting supplies.
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
	argv = append(argv, cfg.ServeArgs...)
	report["service_argv"] = append([]string{cfg.RimgovernorBinary}, argv...)
	cmd := exec.CommandContext(ctx, cfg.RimgovernorBinary, argv...)
	// production-policy is composed only so the executor's ProductionPolicy
	// capability is wired up -- the acquire-anchor below dispatches through
	// it. With no --routine-resource-reserve/--routine-resource-stop the
	// planner it also enables stays a no-op.
	// "all" composes serve's autonomous default (an empty selection enables
	// every family); empty keeps EnsureFoodSupply's own pipeline.
	families := cfg.Families
	switch families {
	case "":
		families = "field,food-storage,acquisition,cooking,supply,production-policy"
	case "all":
		families = ""
	}
	cmd.Env = append(os.Environ(), "RIMGOVERNOR_ROUTINE_FAMILIES="+families)
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

	// Keeps player authority granted for the full watch window: native
	// authority is a bounded generation that legitimately lapses (tick
	// budget exhaustion, an unrecognized native clock event), and nothing
	// re-acquires it automatically -- see routinehaulaccept's
	// authorityKeepAlive doc comment for the full mechanism, reused verbatim
	// here since a multi-minute observation window needs the same recovery.
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
	var events []map[string]any
	lastMethodCount := -1
	lastNeed := domain.NeedState("")
	goalID := cfg.Goal
	if goalID == "" {
		goalID = policy.EnsureFoodSupply
	}
	watchDeadline := time.Now().Add(cfg.Watch)
	checkpointed := false
	for time.Now().Before(watchDeadline) {
		sample, err := SampleGoal(ctx, verifyStore, goalID)
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
		if cfg.Until != nil && err == nil && cfg.Until(sample) {
			report["watch_ended_early"] = true
			break
		}
		if cfg.Checkpoint != nil && !checkpointed && err == nil && cfg.Checkpoint.When(sample) {
			checkpointed = true
			saved, err := checkpoint(ctx, cfg, keepAlive, apiCall, identity, token, prefix)
			if err != nil {
				report["checkpoint"] = map[string]any{"name": cfg.Checkpoint.Name, "error": err.Error()}
				return timeline, fmt.Errorf("checkpoint %s: %w", cfg.Checkpoint.Name, err)
			}
			report["checkpoint"] = saved
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
	var finalHarness *na.Harness
	if reuseCase != nil {
		finalHarness, err = cfg.Reuse.Session(ctx)
		if err != nil {
			return timeline, err
		}
		// The service was killed, not shut down, so the authority it held
		// stays granted until its tick budget lapses; EndCase would retire
		// the game over it. Revoke at the current generation.
		if revoked, err := na.ReleaseAuthority(ctx, finalHarness, identity); err != nil {
			return timeline, err
		} else if revoked != nil {
			report["authority_released"] = revoked
		}
	} else {
		var finalClient *bridge.Client
		reopenDeadline := time.Now().Add(30 * time.Second)
		for {
			finalClient, finalHarness, err = openHarness()
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
	}
	if cfg.Audit != nil {
		if err := cfg.Audit(ctx, finalHarness, report); err != nil {
			return timeline, err
		}
	}

	logData, err := os.ReadFile(naCfg.StartupLogPath())
	if err != nil {
		return timeline, fmt.Errorf("read startup log: %w", err)
	}
	if err := na.CheckStartupLog(string(logData), cfg.Headless); err != nil {
		return timeline, err
	}
	return timeline, nil
}

// SampleGoal reads one maintained goal's current binding (if any) and its
// committed methods' plan stages, mirroring exactly what a routine planner's
// step itself reads: review.Goals for the Need, then that goal's
// Status/Need/Priority/Methods.
func SampleGoal(ctx context.Context, s *store.Store, need policy.GoalID) (map[string]any, error) {
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
	describe := func(method domain.GoalMethod) map[string]any {
		plan, err := s.LoadPlan(ctx, method.Plan)
		if err != nil {
			return map[string]any{"plan": string(method.Plan), "error": err.Error()}
		}
		stages := map[string]int{}
		for _, p := range plan.Progress {
			stages[string(p.View().Stage)]++
		}
		return map[string]any{"plan": string(method.Plan), "actions": len(plan.Spec.Actions()), "stages": stages}
	}
	var plans []map[string]any
	active := map[domain.PlanID]bool{}
	for _, method := range goal.Methods {
		active[method.Plan] = true
		plans = append(plans, describe(method))
	}
	sample["plans"] = plans
	// A completed method leaves goal.Methods at the next review, so a
	// "did the bench plan finish" question needs this epoch's history too.
	var retired []map[string]any
	if history, err := s.LoadGoalMethods(ctx, goalID, goal.Goal.Epoch); err == nil {
		for _, method := range history {
			if !active[method.Plan] {
				retired = append(retired, describe(method))
			}
		}
	}
	sample["retired_plans"] = retired
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
	token    string
	prefix   string

	// hold, while set, stops the loop re-acquiring: a checkpoint pauses the
	// clock on purpose and the save needs manual control to stay manual.
	hold atomic.Bool

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
		if k.hold.Load() {
			continue
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

// checkpoint pauses the clock, saves through the service's lifecycle save
// (which needs manual control), copies the save into root/profile/Saves and
// resumes. The keep-alive is held for the duration so it does not re-acquire
// under the save. The result is the report's checkpoint record.
func checkpoint(ctx context.Context, cfg RunConfig, keepAlive *authorityKeepAlive, apiCall func(string, string, map[string]any, string) (map[string]any, int, error), identity map[string]any, token, prefix string) (map[string]any, error) {
	name := cfg.Checkpoint.Name
	keepAlive.hold.Store(true)
	defer keepAlive.hold.Store(false)
	stamp := time.Now().UnixNano()
	paused, status, err := apiCall("POST", "/api/player/control/pause", map[string]any{"requestId": fmt.Sprintf("%s-checkpoint-pause-%d", prefix, stamp), "expected": identity}, token)
	if err != nil {
		return nil, fmt.Errorf("pause: %w", err)
	}
	if status != 200 {
		return nil, fmt.Errorf("pause status=%d body=%#v", status, paused)
	}
	// The pause is acknowledged before the mode reads manual; wait for it.
	manualDeadline := time.Now().Add(30 * time.Second)
	for {
		state, status, err := apiCall("GET", "/api/state", nil, "")
		if err == nil && status == 200 && na.AsString(state["mode"]) == "manual" {
			break
		}
		if time.Now().After(manualDeadline) {
			return nil, fmt.Errorf("service did not reach manual control after pause: %#v", state)
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(500 * time.Millisecond):
		}
	}
	saved, status, err := apiCall("POST", "/api/lifecycle/save", map[string]any{"requestId": fmt.Sprintf("%s-checkpoint-save-%d", prefix, stamp), "saveName": name}, token)
	if err != nil {
		return nil, fmt.Errorf("save: %w", err)
	}
	if status != 201 {
		return nil, fmt.Errorf("save status=%d body=%#v", status, saved)
	}
	profile := "profile"
	if cfg.Headless {
		profile = "headless-profile"
	}
	src := filepath.Join(cfg.Root, profile, "Saves", name+".rws")
	if _, err := os.Stat(src); err != nil {
		return nil, fmt.Errorf("save completed but %s is missing: %w", src, err)
	}
	dst := filepath.Join(cfg.Root, "profile", "Saves", name+".rws")
	if src != dst {
		if err := na.CopyFile(src, dst); err != nil {
			return nil, err
		}
	}
	resumed, status, err := apiCall("POST", "/api/player/control/resume", map[string]any{"requestId": fmt.Sprintf("%s-checkpoint-resume-%d", prefix, stamp), "expected": identity}, token)
	if err != nil {
		return nil, fmt.Errorf("resume: %w", err)
	}
	if status != 200 {
		return nil, fmt.Errorf("resume status=%d body=%#v", status, resumed)
	}
	return map[string]any{"name": name, "path": dst, "saved": saved, "at": time.Now().UTC().Format(time.RFC3339)}, nil
}
