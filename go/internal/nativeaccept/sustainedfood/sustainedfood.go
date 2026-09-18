// Package sustainedfood holds the watch mechanics behind the serve-driven
// registry cases (sustained/food, sustained/matrix-*, facility/*, farm/*,
// supply/starting, medical/stable-patient; issue #1's sustained-matrix
// acceptance first): on a session the runner opened, launch the live Go
// player service with the routine families under test, acquire player
// authority, and poll a goal's durable state over a wall-clock window
// (Observe), then stop the service and audit against live native facts.
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

// Checkpoint names a save to take mid-run and the sample that triggers it.
type Checkpoint struct {
	Name string
	When func(sample map[string]any) bool
}

// WatchConfig is the observation window Watch samples on a launched
// service: the goal, how long and how often, an early exit and a
// checkpoint.
type WatchConfig struct {
	// Watch is the wall-clock length of the observation window. Window,
	// when > 0, is the same window measured in game ticks: the watch ends
	// as soon as the live game tick has advanced Window ticks past the
	// first sampled tick, and Watch becomes the ceiling that ends it
	// regardless (a paused or starved game never advances). A tick window
	// ties the sample length to what the assertion needs (issue #133)
	// instead of a flat diagnostic length: 2500 ticks is one in-game hour,
	// 60000 one day.
	Watch  time.Duration
	Window uint64
	Poll   time.Duration
	// Goal is the maintained goal the timeline samples (default
	// EnsureFoodSupply).
	Goal policy.GoalID
	// Extra are further goals each sample also reads, under the goal id.
	Extra []policy.GoalID
	// Until, when set, ends the window early once a sample satisfies it.
	Until func(sample map[string]any) bool
	// Checkpoint, when set, saves the live game the first time a sample
	// satisfies When (see Checkpoint).
	Checkpoint *Checkpoint
}

// Watch is the serve-driven family's observation window on a service
// na.Serve has just launched (not yet acquired): it acquires player
// authority and keeps it granted, confirms the scheduler reaches automate
// with a persisted routine review, then samples the goal's durable state
// every Poll for Watch (or until Until) with the step-stall check from the
// service's spec. It records timeline, events and timeline_samples on
// report and returns the samples; the caller stops the service.
func Watch(ctx context.Context, naCfg *na.Config, service *na.ServiceProcess, cfg WatchConfig, report na.Report) (timeline []map[string]any, err error) {
	apiCall := service.API
	token := service.Token
	identity := service.Identity
	prefix := service.Spec.Prefix
	if prefix == "" {
		prefix = "serve"
	}
	if cfg.Poll <= 0 {
		cfg.Poll = 5 * time.Second
	}

	// Resume needs no anchor plan: authority is the world's own root plan,
	// created on first resume (SIMP02, #55).
	if _, err := service.Acquire(); err != nil {
		return nil, err
	}
	report["acquired"] = service.Entry()["resumed"]
	report["window_ms"] = cfg.Watch.Milliseconds()
	window := newTickWindow(cfg.Window)
	report["window"] = window

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

	// The observation window: sample the goal's state and the stage of
	// every one of its committed methods' plans, at Poll intervals, for
	// Watch wall-clock duration. Every sample is retained
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
	defer func() {
		report["timeline"] = timeline
		report["events"] = events
		report["timeline_samples"] = len(timeline)
	}()
	for time.Now().Before(watchDeadline) {
		sample, err := SampleGoal(ctx, verifyStore, goalID)
		if !stepped {
			var stepErr error
			if stepped, stepErr = service.StepAdmitted(ctx, watchStarted); stepErr != nil {
				return timeline, stepErr
			}
		}
		if err != nil {
			sample = map[string]any{"error": err.Error(), "at": time.Now().UTC().Format(time.RFC3339)}
		}
		for _, extra := range cfg.Extra {
			also, err := SampleGoal(ctx, verifyStore, extra)
			if err != nil {
				also = map[string]any{"error": err.Error()}
			}
			sample[string(extra)] = also
		}
		if tick, ok := liveTick(apiCall); ok {
			sample["tick"] = tick
			window.observe(tick)
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
		if window.reached() {
			break
		}
		if cfg.Checkpoint != nil && !checkpointed && err == nil && cfg.Checkpoint.When(sample) {
			checkpointed = true
			saved, err := checkpoint(ctx, naCfg, cfg.Checkpoint, service.HoldAuthority, apiCall, identity, token, prefix)
			if err != nil {
				report["checkpoint"] = map[string]any{"name": cfg.Checkpoint.Name, "error": err.Error()}
				return timeline, fmt.Errorf("checkpoint %s: %w", cfg.Checkpoint.Name, err)
			}
			report["checkpoint"] = saved
		}
		select {
		case <-ctx.Done():
			return timeline, ctx.Err()
		case <-time.After(cfg.Poll):
		}
	}
	window.WatchWall = time.Since(watchStarted).Round(time.Second).String()
	window.Reached = window.reached()
	return timeline, nil
}

// TickWindow is the report's record of the tick-measured watch window
// (report["window"]): the configured length, the first and last live ticks
// the watch sampled, how many ticks elapsed between them, whether the
// configured length was reached before the wall-clock ceiling, and how
// long the watch ran on the wall clock. Ticks is 0 when the run watched on
// wall-clock alone, in which case Reached stays false.
type TickWindow struct {
	Ticks     uint64 `json:"ticks"`
	FirstTick uint64 `json:"first_tick"`
	LastTick  uint64 `json:"last_tick"`
	Elapsed   uint64 `json:"elapsed_ticks"`
	Sampled   bool   `json:"tick_sampled"`
	Reached   bool   `json:"reached"`
	WatchWall string `json:"watch_wall"`
}

func newTickWindow(ticks uint64) *TickWindow {
	return &TickWindow{Ticks: ticks}
}

// observe folds one live tick into the window; the first observation
// anchors it and a tick below the anchor (a rewind) is ignored.
func (w *TickWindow) observe(tick uint64) {
	if !w.Sampled {
		w.Sampled = true
		w.FirstTick = tick
	}
	if tick >= w.FirstTick {
		w.LastTick = tick
		w.Elapsed = tick - w.FirstTick
	}
}

// reached reports whether a configured tick window has elapsed.
func (w *TickWindow) reached() bool {
	return w.Ticks > 0 && w.Sampled && w.Elapsed >= w.Ticks
}

// liveTick reads the service's live game tick from /api/state; false when
// the snapshot has none (not connected yet, or a stale read).
func liveTick(apiCall func(string, string, map[string]any, string) (map[string]any, int, error)) (uint64, bool) {
	st, status, err := apiCall("GET", "/api/state", nil, "")
	if err != nil || status != 200 {
		return 0, false
	}
	game, _ := st["game"].(map[string]any)
	tick, ok := game["tick"].(float64)
	if !ok || tick < 0 {
		return 0, false
	}
	return uint64(tick), true
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
func checkpoint(ctx context.Context, naCfg *na.Config, cp *Checkpoint, hold func(bool), apiCall func(string, string, map[string]any, string) (map[string]any, int, error), identity map[string]any, token, prefix string) (map[string]any, error) {
	name := cp.Name
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
	if naCfg.Headless {
		profile = "headless-profile"
	}
	src := filepath.Join(naCfg.Root, profile, "Saves", name+".rws")
	if _, err := os.Stat(src); err != nil {
		return nil, fmt.Errorf("save completed but %s is missing: %w", src, err)
	}
	dst := filepath.Join(naCfg.Root, "profile", "Saves", name+".rws")
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

// Observed is the slice of a registry case's session Observe drives
// (cases.Session satisfies it): the loaded, paused, frozen game the shared
// runner opened.
type Observed interface {
	Harness() *na.Harness
	Config() *na.Config
	Report() na.Report
	Spec() na.ServeSpec
	Serve(ctx context.Context, spec na.ServeSpec) (*na.ServiceProcess, error)
	Reattach(ctx context.Context) (*na.Harness, error)
}

// Observation is one serve-driven case's shape over Watch: Prepare runs
// against the loaded game before the service starts (the place to record a
// live baseline), the service declared on the case is launched and
// watched, then stopped, and Audit runs against the reattached session so
// the durable journal can be compared with live native facts. Spec, when
// set, edits the case's declared serve spec before launch.
type Observation struct {
	WatchConfig
	Spec    func(spec *na.ServeSpec)
	Prepare func(ctx context.Context, h *na.Harness, report na.Report) error
	Audit   func(ctx context.Context, h *na.Harness, report na.Report) error
}

// Observe runs one Observation on s and returns the timeline; the service
// is stopped and the session reattached before Audit, on every path.
func Observe(ctx context.Context, s Observed, o Observation) ([]map[string]any, error) {
	report := s.Report()
	if o.Prepare != nil {
		if err := o.Prepare(ctx, s.Harness(), report); err != nil {
			return nil, fmt.Errorf("prepare: %w", err)
		}
	}
	spec := s.Spec()
	if o.Spec != nil {
		o.Spec(&spec)
	}
	service, err := s.Serve(ctx, spec)
	if err != nil {
		return nil, err
	}
	stopService := func() {
		if keep := service.Stop(); keep != nil {
			report["authority_reacquisitions"] = keep
		}
	}
	defer stopService()
	timeline, err := Watch(ctx, s.Config(), service, o.WatchConfig, report)
	report["metrics"] = DeriveMetrics(timeline)
	if err != nil {
		return timeline, err
	}
	stopService()
	h, err := s.Reattach(ctx)
	if err != nil {
		return timeline, err
	}
	if o.Audit != nil {
		if err := o.Audit(ctx, h, report); err != nil {
			return timeline, err
		}
	}
	return timeline, nil
}
