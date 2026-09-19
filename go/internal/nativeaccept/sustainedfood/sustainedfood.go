// Package sustainedfood holds the watch mechanics behind the serve-driven
// registry cases (sustained/food, sustained/matrix-*, facility/*, farm/*,
// supply/starting, medical/stable-patient; issue #1's sustained-matrix
// acceptance first): on a session the runner opened, launch the live Go
// player service with the routine families under test, acquire player
// authority, and sample a goal's durable state over a tick-measured window
// (Watch: a tick cadence, woken early by the service's own journal), then
// stop the service and audit against live native facts.
package sustainedfood

import (
	"context"
	"errors"
	"fmt"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Checkpoint is a phase boundary the watch checkpoints at: the first
// sample satisfying When saves the live game as Name (pause, save, resume
// through the service), copies the save into root/profile/Saves for the
// commit flow (tools/*-checkpoint) and, under the runner's checkpoint ring
// (#249), takes a full bundle labelled Name a rerun can resume from.
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
	// PollTicks is the sample cadence in game ticks: the next sample is
	// taken as soon as the live tick has advanced PollTicks past the
	// previous one (probed every na.RunInterval), so the cadence follows
	// the game's speed instead of the wall clock (#267; default
	// DefaultPollTicks). Poll is the wall-clock ceiling between samples
	// (default DefaultPoll): a paused or starved game is still sampled,
	// so its stall is visible in the timeline.
	PollTicks uint64
	Poll      time.Duration
	// Wake, when a service journal row satisfies it, takes a sample at
	// once instead of waiting for the cadence: the service's flight
	// recorder is tailed between samples (na.FlightTail). Default
	// na.WorkerOutcome, the row an action's outcome changes on. Until,
	// FailFast and the checkpoints all see the woken sample.
	Wake func(na.FlightRow) bool
	// Goal is the maintained goal the timeline samples (default
	// EnsureFoodSupply).
	Goal policy.GoalID
	// Extra are further goals each sample also reads, under the goal id.
	Extra []policy.GoalID
	// Until, when set, ends the window early once a sample satisfies it.
	Until func(sample map[string]any) bool
	// Checkpoint, when set, saves the live game the first time a sample
	// satisfies When (see Checkpoint); Checkpoints are further phase
	// boundaries, each taken once. The report's checkpoint row is the
	// first's; checkpoints lists them all.
	Checkpoint  *Checkpoint
	Checkpoints []Checkpoint
	// FailFast ends the window early with the journal's refusal text once
	// the case cannot pass (see FailFast); on by default.
	FailFast FailFast
}

// Watch is the serve-driven family's observation window on a service
// na.Serve has just launched (not yet acquired): it acquires player
// authority and keeps it granted, confirms the scheduler reaches automate
// with a persisted routine review, then samples the goal's durable state
// at the cadence WatchConfig sets (PollTicks, Poll, Wake) for Watch (or
// until Until) with the step-stall check from the service's spec. Every sample also carries the service's live colony
// census (sampleColony) so starvation is visible in the timeline itself
// (#261). It records timeline, events and timeline_samples on report and
// returns the samples; the caller stops the service.
func Watch(ctx context.Context, naCfg *na.Config, service *na.ServiceProcess, cfg WatchConfig, report na.Report) (timeline []map[string]any, err error) {
	apiCall := service.API
	token := service.Token
	identity := service.Identity
	prefix := service.Spec.Prefix
	if prefix == "" {
		prefix = "serve"
	}
	if cfg.Poll <= 0 {
		cfg.Poll = DefaultPoll
	}
	if cfg.PollTicks == 0 {
		cfg.PollTicks = DefaultPollTicks
	}
	if cfg.Wake == nil {
		cfg.Wake = na.WorkerOutcome
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
		case <-time.After(na.RunInterval):
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
	var pending []Checkpoint
	if cfg.Checkpoint != nil {
		pending = append(pending, *cfg.Checkpoint)
	}
	pending = append(pending, cfg.Checkpoints...)
	var taken []map[string]any
	stepped := false
	failFast := newFailFast(cfg.FailFast, goalID, service.StderrPath())
	tail := na.NewFlightTail(service.FlightPath)
	cadence := map[string]any{"poll_ticks": cfg.PollTicks, "poll_ms": cfg.Poll.Milliseconds(), "wakes": 0, "tick_polls": 0, "wall_polls": 0}
	report["cadence"] = cadence
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
		sample["colony"] = sampleColony(apiCall)
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
		// The failed checkpoint bundle (#249) is still taken: the runner
		// captures it on the error path like any other watch failure.
		if err == nil {
			if verdict, failed := failFast.check(sample); failed {
				report["fail_fast"] = verdict
				return timeline, verdict
			}
		}
		if err == nil {
			for i := 0; i < len(pending); i++ {
				cp := pending[i]
				if !cp.When(sample) {
					continue
				}
				pending = append(pending[:i], pending[i+1:]...)
				i--
				saved, err := checkpoint(ctx, naCfg, &cp, service.HoldAuthority, apiCall, identity, token, prefix)
				if err != nil {
					report["checkpoint"] = map[string]any{"name": cp.Name, "error": err.Error()}
					return timeline, fmt.Errorf("checkpoint %s: %w", cp.Name, err)
				}
				if bundle, ok, err := na.CaptureCheckpoint(ctx, cp.Name); ok {
					if err != nil {
						saved["bundle_error"] = err.Error()
					} else {
						saved["bundle"] = bundle.Path
					}
				}
				taken = append(taken, saved)
				if _, first := report["checkpoint"]; !first {
					report["checkpoint"] = saved
				}
				report["checkpoints"] = taken
			}
		}
		// Between samples is a natural pause for the runner's checkpoint
		// ring (#249); a capture's own time does not count against Watch.
		if took := na.CheckpointPause(ctx); took > 0 {
			watchDeadline = watchDeadline.Add(took)
		}
		sampledTick, _ := sample["tick"].(uint64)
		reason, err := waitNextSample(ctx, apiCall, tail, cfg, sampledTick, watchDeadline)
		if err != nil {
			return timeline, err
		}
		cadence[reason] = cadence[reason].(int) + 1
	}
	window.WatchWall = time.Since(watchStarted).Round(time.Second).String()
	window.Reached = window.reached()
	return timeline, nil
}

// DefaultPollTicks and DefaultPoll are WatchConfig's sample cadence when
// unset: 600 ticks (a quarter of an in-game hour; ~1.7 s at Superfast,
// ~0.7 s at Ultrafast, the probe interval once boosted) and a 5 s
// wall-clock ceiling.
const (
	DefaultPollTicks uint64 = 600
	DefaultPoll             = 5 * time.Second
)

// waitNextSample pauses between two samples until one of the cadence's
// conditions holds and names it for the report: a journal row satisfying
// Wake ("wakes"), PollTicks of game time past the previous sample's tick
// ("tick_polls"), or the Poll wall-clock ceiling ("wall_polls"). The live
// tick and the journal are probed every na.RunInterval. The watch
// deadline ends the pause early so the loop's own check sees it.
func waitNextSample(ctx context.Context, apiCall func(string, string, map[string]any, string) (map[string]any, int, error), tail *na.FlightTail, cfg WatchConfig, sampledTick uint64, watchDeadline time.Time) (string, error) {
	started := time.Now()
	for {
		wait := na.RunInterval
		if remaining := time.Until(watchDeadline); remaining < wait {
			wait = remaining
		}
		if wait <= 0 {
			return "wall_polls", nil
		}
		select {
		case <-ctx.Done():
			return "", ctx.Err()
		case <-time.After(wait):
		}
		if rows, err := tail.Next(); err == nil {
			for _, row := range rows {
				if cfg.Wake(row) {
					return "wakes", nil
				}
			}
		}
		if time.Since(started) >= cfg.Poll {
			return "wall_polls", nil
		}
		if tick, ok := liveTick(apiCall); ok && sampledTick > 0 && tick >= sampledTick+cfg.PollTicks {
			return "tick_polls", nil
		}
	}
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
	// The persisted review does not name the emergency need; its
	// development rows say which priority>=2 goals it held back.
	emergency := []string{}
	for _, row := range review.Development.Rows {
		if row.Reason == policy.DevelopmentEmergency {
			emergency = append(emergency, string(row.Goal))
		}
	}
	sample["emergency"] = emergency
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
	// The review's ranking row (keyed by need) says why a deficit goal is
	// or is not selected this review (startup_survival, capacity_committed...).
	for _, row := range review.Development.Rows {
		if row.Goal == need {
			sample["development"] = map[string]any{"reason": string(row.Reason), "selected": row.Selected, "committed": row.Committed, "idle": row.Idle}
		}
	}
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
		var unsuccessful []map[string]any
		for _, p := range plan.Progress {
			view := p.View()
			stages[string(view.Stage)]++
			kinds[string(p.Action().Kind())]++
			if view.Stage == domain.Unsuccessful {
				reason, _ := view.UnsuccessfulReason.Value()
				unsuccessful = append(unsuccessful, map[string]any{"action": string(view.Action), "kind": string(p.Action().Kind()), "reason": string(reason)})
			}
		}
		described := map[string]any{"plan": string(method.Plan), "actions": len(plan.Spec.Actions()), "stages": stages, "kinds": kinds}
		if len(unsuccessful) > 0 {
			described["unsuccessful"] = unsuccessful
		}
		return described
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

// checkpoint saves the live game as cp.Name through the service
// (na.ServiceSave: pause to manual control, save, resume, the keep-alive
// held throughout) and copies the save into root/profile/Saves. The result
// is the report's checkpoint record.
func checkpoint(ctx context.Context, naCfg *na.Config, cp *Checkpoint, hold func(bool), apiCall func(string, string, map[string]any, string) (map[string]any, int, error), identity map[string]any, token, prefix string) (map[string]any, error) {
	name := cp.Name
	src, err := na.ServiceSave(ctx, naCfg, name, hold, apiCall, identity, token, prefix+"-checkpoint")
	if err != nil {
		return nil, err
	}
	dst := filepath.Join(naCfg.Root, "profile", "Saves", name+".rws")
	if src != dst {
		if err := na.CopyFile(src, dst); err != nil {
			return nil, err
		}
	}
	return map[string]any{"name": name, "path": dst, "at": time.Now().UTC().Format(time.RFC3339)}, nil
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
	report["colony_outcome"] = DeriveColonyOutcome(timeline)
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
