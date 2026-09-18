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
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"

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
	// ClockSpeed is the harness's own serve --clock-speed default, applied
	// only while RIMGOVERNOR_ACCEPT_CLOCK_SPEED is unset (na.ClockSpeedArgs
	// otherwise decides, #128). A watch window measures wall-clock minutes,
	// not ticks, so a faster clock packs more simulated ticks -- and more
	// chances for EnsureFoodSupply to actually progress -- into cfg.Watch.
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
	Families string
	Goal     policy.GoalID
	// StepStall, when > 0, fails the run fast instead of watching an idle
	// service for the whole window: unless a scheduler step has admitted a
	// clock window (a journaled clock attempt) within StepStall of the watch
	// starting, Run returns a StepStallError naming the last step failure
	// the service logged. Under peer contention (several headless games on
	// one machine, issue #103) every planner in the composed pipeline shares
	// one step budget and a starved step admits nothing for the whole watch.
	StepStall time.Duration
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

	// Sequential native sessions, exactly like routinehaulaccept: this
	// harness's own session loads the save and reads identity, then
	// releases (without games_stop) to free the sole GABP slot for the
	// service, and reattaches at the very end for the audit and the close.
	var held *na.Game
	var h *na.Harness
	if cfg.Reuse != nil {
		reuseCase, err = cfg.Reuse.BeginCase(ctx, filepath.Base(output), cfg.Save, output)
		if err != nil {
			return nil, err
		}
		h = reuseCase.Harness
		report["reuse_case"] = map[string]any{"loadToken": reuseCase.Reset.LoadToken, "tick": reuseCase.Reset.Tick}
		// BeginCase loaded the save; the naming dialog still needs dismissing.
		if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
			return nil, err
		}
	} else {
		held, err = na.OpenGame(ctx, naCfg)
		if err != nil {
			return nil, err
		}
		// Runs after the service's deferred stop (LIFO), so the slot is
		// free for the reattach Close makes on its own; every earlier
		// failure path ends the game too instead of orphaning it.
		defer held.Close(report)
		h = na.NewHarness(held.Client, output)
		if _, err := na.LoadSave(ctx, h, cfg.Save, report); err != nil {
			return nil, err
		}
	}
	identity, err := na.ReadIdentity(ctx, h, "identity")
	if err != nil {
		return nil, err
	}

	if cfg.Prepare != nil {
		if err := cfg.Prepare(ctx, h, report); err != nil {
			return nil, fmt.Errorf("prepare: %w", err)
		}
	}

	// Compose only EnsureFoodSupply's own pipeline so the food outcome under
	// diagnosis is not confounded by other families: field growing, food
	// storage, harvest/wood acquisition, cooking bills and starting supplies.
	// production-policy is composed only so the executor's ProductionPolicy
	// capability is wired up -- the acquire below dispatches through it.
	// With no --routine-resource-reserve/--routine-resource-stop the planner
	// it also enables stays a no-op. "all" composes serve's autonomous
	// default (an empty selection enables every family); empty keeps
	// EnsureFoodSupply's own pipeline.
	families := cfg.Families
	switch families {
	case "":
		families = "field,food-storage,acquisition,cooking,supply,production-policy"
	case "all":
		families = ""
	}
	spec := na.ServeSpec{
		Binary: cfg.RimgovernorBinary, Families: []string{families},
		Extra:         cfg.ServeArgs,
		ClockSpeed:    cfg.ClockSpeed,
		NativeTimeout: cfg.NativeTimeout, StepStall: cfg.StepStall, Prefix: prefix,
	}
	var service *na.ServiceProcess
	if reuseCase != nil {
		// Free the sole GABP slot before the service starts its own bridge
		// session; this does NOT call games_stop, so the loaded save survives.
		if err := cfg.Reuse.ReleaseSession(); err != nil {
			return nil, fmt.Errorf("release reuse bridge session: %w", err)
		}
		service, err = na.Serve(ctx, naCfg, nil, identity, spec, report)
	} else {
		service, err = na.Serve(ctx, naCfg, held, identity, spec, report)
	}
	if err != nil {
		return nil, err
	}
	stopService := func() {
		if keep := service.Stop(); keep != nil {
			report["authority_reacquisitions"] = keep
		}
	}
	defer stopService()
	apiCall := service.API
	token := service.Token

	// Resume needs no anchor plan: authority is the world's own root plan,
	// created on first resume (SIMP02, #55).
	if _, err := service.Acquire(); err != nil {
		return nil, err
	}
	report["acquired"] = service.Entry()["resumed"]

	verifyStore, err := service.Store(ctx)
	if err != nil {
		return nil, err
	}

	// Keeps player authority granted for the full watch window: native
	// authority is a bounded generation that legitimately lapses (tick
	// budget exhaustion, an unrecognized native clock event), and nothing
	// re-acquires it automatically -- see na.AuthorityKeepAlive.
	service.KeepAuthority(ctx)

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
	watchStarted := time.Now()
	watchDeadline := watchStarted.Add(cfg.Watch)
	checkpointed := false
	stepped := false
	for time.Now().Before(watchDeadline) {
		sample, err := SampleGoal(ctx, verifyStore, goalID)
		if !stepped {
			var stepErr error
			if stepped, stepErr = service.StepAdmitted(ctx, watchStarted); stepErr != nil {
				report["timeline"] = timeline
				report["events"] = events
				return timeline, stepErr
			}
		}
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
			saved, err := checkpoint(ctx, cfg, service.HoldAuthority, apiCall, identity, token, prefix)
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
		finalClient, err := held.Reattach(ctx)
		if err != nil {
			return timeline, err
		}
		finalHarness = na.NewHarness(finalClient, output)
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
		stages, kinds := map[string]int{}, map[string]int{}
		for _, p := range plan.Progress {
			stages[string(p.View().Stage)]++
			kinds[string(p.Action().Kind())]++
		}
		return map[string]any{"plan": string(method.Plan), "actions": len(plan.Spec.Actions()), "stages": stages, "kinds": kinds}
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

// checkpoint pauses the clock, saves through the service's lifecycle save
// (which needs manual control), copies the save into root/profile/Saves and
// resumes. The keep-alive is held (hold) for the duration so it does not
// re-acquire under the save. The result is the report's checkpoint record.
func checkpoint(ctx context.Context, cfg RunConfig, hold func(bool), apiCall func(string, string, map[string]any, string) (map[string]any, int, error), identity map[string]any, token, prefix string) (map[string]any, error) {
	name := cfg.Checkpoint.Name
	hold(true)
	defer hold(false)
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
