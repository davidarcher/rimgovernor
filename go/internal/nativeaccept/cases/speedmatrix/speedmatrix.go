// Package speedmatrix (issue #111, M4a) runs the same staged colony under
// rimgovernor serve once per clock speed -- Normal, Fast, Superfast,
// Ultrafast and uncapped (Ultrafast with the acceptance test acceleration,
// #109) -- with an identical game-tick budget, and requires the pawns to
// achieve the same outcome at every speed.
//
// The stage is test/throughput_prepare (ThroughputFixture.cs) applied once
// to a quiet debug colony with frozen needs: three or more colonists on
// Construct/Haul, loose Steel stacks with a stockpile to haul them to, and a
// contiguous run of legal Wall cells. That game is saved once; every speed
// reloads the save, so the map, pawns and stacks are the same. Per speed
// the case releases the game to one serve process (haul + work families)
// with the speed's --clock-speed, submits the wall run as building plans,
// resumes automatic control and waits, stall-bounded, until the serve-side
// tick has advanced by the budget or the stage has run out of work (every
// wall plan completed and no storage deficit pending): once the work is done
// the clock scheduler admits no further window (refused=[no_work]) and the
// tick stops, so a budget the stage cannot fill would stall the wait (#210).
// It then stops the service, reads the outcome natively (stored units, walls
// built, RequireHealthyColonists) and counts unsuccessful plan stages in the
// service journal. The flight
// recorder gives wall TPS, paused fraction, steps, reads/step, parent hits,
// the wall-sized colony window (#126) and the budget-vs-reactive stop split
// with stop latency, the stop-to-readmit pause each admission closed
// (#162) and budget stops per 6000 ticks; the #126 throughput thresholds
// (maxPausedFraction, minUltrafastTPSRatio) are reported, not enforced, at
// zero.
//
// Postconditions must agree within tolerance across speeds and no case may
// record an unsuccessful plan stage. Reloads go through a plain hold; the
// GameReuse reset contract (issue #22) is reuseaccept's own subject.
package speedmatrix

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	prepareTool = "test/throughput_prepare"
	controlTool = "test/throughput_control"
	stageSave   = "RimGovernor-speedmatrix-stage"

	// ticks is the game-tick budget each speed runs after resume (the
	// serve-side tick must advance by this much).
	ticks = 6000
	// items and segments size the stage: Steel stacks spawned and wall
	// segments laid out.
	items    = 4
	segments = 6
	// tolerance is the allowed spread of each pawn-outcome postcondition
	// across speeds.
	tolerance = 1
	// maxPausedFraction and minUltrafastTPSRatio are the issue #126
	// throughput expectations (0.5 and 2); at zero they are reported, not
	// enforced.
	maxPausedFraction    = 0
	minUltrafastTPSRatio = 0
)

func init() {
	cases.Register(cases.Case{
		Name: "speedmatrix/plain",
		Scope: "Speed matrix (#111): one staged colony reloaded per clock speed under rimgovernor serve with an " +
			"identical tick budget; wall TPS, paused fraction, steps, reads/step, parent hits, stop latency and " +
			"budget-vs-reactive stops per speed; pawn outcomes equal within a tolerance.",
		Start: cases.Fixture{Op: prepareTool, Args: map[string]any{"itemCount": items, "wallSegments": segments},
			On: cases.DebugStart{Size: na.DebugStart{Flat: true}}},
		// The stage is hauling and wall building inside the home area; the
		// wild map is unobserved (#272).
		QuietWorld: true,
		Service:    true,
		Budget:     cases.MaxBudget,
		Run:        run,
	})
}

type matrix struct {
	s      cases.Session
	report na.Report
	cases  []na.SpeedCase
	// storage and walls are the fixture's cell lists, in the "x:z" form
	// test/throughput_control reads back.
	storage, walls string
	sites          []map[string]any
	outcomes       []na.SpeedOutcome
}

func run(ctx context.Context, s cases.Session) error {
	speedCases, err := na.ParseSpeedCases(na.DefaultSpeedMatrix)
	if err != nil {
		return err
	}
	m := &matrix{s: s, report: s.Report(), cases: speedCases}
	m.report["speeds"] = speedCases
	m.report["tick_budget"] = ticks
	m.report["tolerance"] = tolerance
	m.report["max_paused_fraction"] = maxPausedFraction
	m.report["min_ultrafast_tps_ratio"] = minUltrafastTPSRatio
	if !na.Contains(s.Names(), controlTool) {
		return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture ThroughputFixture", controlTool)
	}
	if err := m.stage(ctx); err != nil {
		return fmt.Errorf("stage: %w", err)
	}

	for _, c := range m.cases {
		outcome, err := m.runCase(ctx, c)
		if err != nil {
			return fmt.Errorf("%s: %w", c.Name, err)
		}
		m.outcomes = append(m.outcomes, outcome)
		m.report["outcomes"] = m.outcomes
		fmt.Printf("DONE %s: stored=%d/%d walls=%d colonists=%d unsuccessful=%d\n", c.Name,
			outcome.StoredUnits, outcome.StoredUnits+outcome.LooseUnits, outcome.WallsBuilt, outcome.HealthyColonists, outcome.UnsuccessfulStages)
	}
	if problems := na.CompareOutcomes(m.outcomes, tolerance); len(problems) > 0 {
		m.report["outcome_problems"] = problems
		return fmt.Errorf("pawn outcomes differ across speeds: %s", strings.Join(problems, "; "))
	}
	// The stage must have been reachable: a matrix where nothing was
	// hauled or built agrees trivially and proves nothing.
	for _, o := range m.outcomes {
		if o.StoredUnits == 0 && o.WallsBuilt == 0 {
			return fmt.Errorf("%s: nothing was hauled or built within the tick budget; raise ticks or check the stage", o.Case)
		}
	}
	rows, _ := m.report["speed_metrics"].([]map[string]any)
	if problems := na.CheckSpeedMetrics(na.SpeedMetricsFromRows(rows), maxPausedFraction, minUltrafastTPSRatio); len(problems) > 0 {
		m.report["metric_problems"] = problems
		return fmt.Errorf("clock throughput short of the thresholds: %s", strings.Join(problems, "; "))
	}
	return checkStartupLog(s)
}

// stage takes the fixture's layout from the prepared colony and saves it
// as stageSave for every case to reload.
func (m *matrix) stage(ctx context.Context) error {
	h, prepared := m.s.Harness(), m.s.Prepared()
	h.Output = filepath.Join(m.s.Config().Output, "stage")
	if err := os.MkdirAll(h.Output, 0755); err != nil {
		return err
	}
	if _, err := na.ConfirmColonyNames(ctx, h, m.report); err != nil {
		return err
	}
	m.storage = cellList(prepared["storageCells"])
	m.walls = cellList(prepared["sites"])
	for _, raw := range na.AsSlice(prepared["sites"]) {
		site, _ := na.AsMap(raw)
		m.sites = append(m.sites, site)
	}
	if len(m.sites) != segments {
		return fmt.Errorf("stage laid out %d wall sites, want %d", len(m.sites), segments)
	}
	// Frozen needs do not survive a reload (the op is per game), so the
	// save carries the stage only; each case freezes again after loading.
	started := time.Now()
	if _, err := h.Call(ctx, "save-stage", "rimworld/save_game", map[string]any{"saveName": stageSave}); err != nil {
		return err
	}
	return m.waitSaved(ctx, started)
}

// waitSaved waits for the game to finish writing the stage save into the
// active profile's Saves directory (headless-profile or profile).
func (m *matrix) waitSaved(ctx context.Context, started time.Time) error {
	root := m.s.Config().Root
	deadline := started.Add(90 * time.Second)
	for {
		for _, profile := range []string{"headless-profile", "profile"} {
			candidate := filepath.Join(root, profile, "Saves", stageSave+".rws")
			info, err := os.Stat(candidate)
			if err == nil && info.Size() > 0 && !info.ModTime().Before(started.Add(-time.Second)) {
				// A save the game is still writing grows; require it stable.
				time.Sleep(2 * time.Second)
				if again, err := os.Stat(candidate); err == nil && again.Size() == info.Size() {
					m.report["stage_save"] = candidate
					return nil
				}
			}
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("stage save %s did not appear under %s within 90s", stageSave, root)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(time.Second):
		}
	}
}

// runCase reloads the stage, hands the game to one serve process at the
// case's speed, waits out the tick budget and reads the outcome back.
func (m *matrix) runCase(ctx context.Context, c na.SpeedCase) (outcome na.SpeedOutcome, err error) {
	output := filepath.Join(m.s.Config().Output, c.Name)
	if err := os.MkdirAll(output, 0755); err != nil {
		return outcome, err
	}
	report := na.NewReport("speed case "+c.Name, m.s.Config().Headless)
	report["case"] = c
	defer func() {
		if err != nil {
			report["error"] = err.Error()
		} else {
			report["passed"] = true
		}
		report.Finalize(output)
	}()

	var service *na.ServiceProcess
	defer func() {
		if service != nil {
			service.Stop()
		}
	}()
	h, err := m.s.Reattach(ctx)
	if err != nil {
		return outcome, err
	}
	h.Output = output
	if _, err := h.Call(ctx, "load-stage", "rimworld/load_game_ready", map[string]any{
		"saveName": stageSave, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
	}); err != nil {
		return outcome, err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return outcome, err
	}
	identityReply, err := h.Wire(ctx, "identity", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return outcome, err
	}
	_, loaded, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return outcome, err
	}
	loadedContext, _ := na.AsMap(loaded["context"])
	identity, _ := na.AsMap(loadedContext["identity"])
	frozen, err := na.FreezeNeeds(ctx, h, nil)
	if err != nil {
		return outcome, err
	}
	report["frozen_needs"] = frozen
	before, err := m.control(ctx, h, identity, "control-before")
	if err != nil {
		return outcome, err
	}
	report["control_before"] = before
	if na.AsNumber(before["storedUnits"]) != 0 || na.AsNumber(before["wallsBuilt"]) != 0 || na.AsNumber(before["wallBlueprints"]) != 0 {
		return outcome, fmt.Errorf("stage is not fresh after reload: %#v", before)
	}
	startTick := uint64(na.AsNumber(before["tick"]))
	extra := append(c.ServeArgs(), na.FlightRecorderArgs(output, true)...)
	service, err = m.s.Launch(ctx, na.ServiceLaunch{Families: []string{"haul", "work"}, Extra: extra, Output: output, Report: report})
	if err != nil {
		return outcome, err
	}
	token, err := service.SessionToken()
	if err != nil {
		return outcome, err
	}
	attached, err := service.WaitAttached(identity, 90*time.Second)
	if err != nil {
		return outcome, err
	}
	report["service_state_attached"] = attached
	prefix := "speedmatrix-" + strings.ToLower(c.Name) + "-" + randomSuffix()
	planIDs, err := m.submitWalls(service, prefix, identity, token, report)
	if err != nil {
		return outcome, err
	}
	rootPlanID, err := service.Resume(prefix, identity, token, report)
	if err != nil {
		return outcome, err
	}
	report["root_plan"] = rootPlanID
	resumedAt := time.Now()
	keepAlive := &na.AuthorityKeepAlive{Service: service, Prefix: prefix, Identity: identity, Token: token}
	stopKeepAlive := keepAlive.Start(ctx)
	defer func() { report["authority_reacquisitions"] = stopKeepAlive() }()

	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return outcome, err
	}
	defer journal.Close()
	if _, _, err := service.WaitRoutineReview(ctx, journal, 90*time.Second); err != nil {
		return outcome, err
	}
	// The budget is game time: the wait ends once the tick the service
	// last observed (its flight recorder's newest clock sample; the routine
	// review's tick only moves once per full step under a day-long window,
	// #244) has advanced by -ticks past the reload tick, or earlier once the
	// stage has no work left for the clock to admit. The signature is the
	// tick itself, so a clock that stops advancing with work pending stalls
	// the wait.
	var lastTick uint64
	var workDone bool
	flight := na.FlightRecorderPath(output)
	waitErr := na.WaitProgress(ctx, na.Wait{Stall: na.StallBudget(), Interval: 2 * time.Second, Terminal: service.Exited},
		func(ctx context.Context) (string, bool, error) {
			review, err := journal.LoadRoutineReview(ctx)
			if err != nil {
				return "", false, err
			}
			lastTick = uint64(review.Tick)
			if rows, err := bridge.ReadTimeline(flight); err == nil {
				if observed := bridge.SummarizePhases(rows).Clock.LastTick; observed > int64(lastTick) {
					lastTick = uint64(observed)
				}
			}
			if lastTick >= startTick+uint64(ticks) {
				return na.Signature(lastTick), true, nil
			}
			workDone, err = stageWorkDone(ctx, journal, review, planIDs)
			if err != nil {
				return "", false, err
			}
			return na.Signature(lastTick), workDone, nil
		})
	wallSeconds := time.Since(resumedAt).Seconds()
	report["wait"] = map[string]any{"start_tick": startTick, "last_tick": lastTick, "wall_seconds": wallSeconds, "work_done": workDone}
	if waitErr != nil {
		if final, loadErr := journal.LoadRoutineReview(ctx); loadErr == nil {
			data, _ := json.Marshal(final)
			report["routine_review_at_failure"] = json.RawMessage(data)
		}
		return outcome, fmt.Errorf("tick budget wait (start %d, last %d, want +%d): %w", startTick, lastTick, ticks, waitErr)
	}
	unsuccessful, planCount, err := countUnsuccessful(ctx, journal, planIDs)
	if err != nil {
		return outcome, err
	}
	report["plans_inspected"] = planCount
	journal.Close()
	service.Stop()
	rows, err := bridge.ReadTimeline(na.FlightRecorderPath(output))
	if err != nil {
		return outcome, fmt.Errorf("read flight recorder: %w", err)
	}
	phases := bridge.SummarizePhases(rows)
	stops := na.SummarizeStops(rows, resumedAt.UnixMilli())
	report["phases"] = phases
	report["stops"] = stops
	metrics := caseMetrics(c, phases, stops, startTick, lastTick, wallSeconds)
	report["metrics"] = metrics
	appendMetrics(m.report, metrics)

	// Independent native read after the service released the slot.
	if h, err = m.s.Reattach(ctx); err != nil {
		return outcome, err
	}
	h.Output = output
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return outcome, err
	}
	// The service was stopped, not shut down: its authority grant lingers
	// until the tick budget lapses, and the next reload would carry it.
	if revoked, err := na.ReleaseAuthority(ctx, h, identity); err != nil {
		return outcome, err
	} else if revoked != nil {
		report["authority_released"] = revoked
	}
	after, err := m.control(ctx, h, identity, "control-after")
	if err != nil {
		return outcome, err
	}
	report["control_after"] = after
	pawnsReply, err := h.Wire(ctx, "pawns-after", "observations_list_pawns", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"colonist": true},
	})
	if err != nil {
		return outcome, err
	}
	if err := na.RequireHealthyColonists(pawnsReply); err != nil {
		return outcome, err
	}
	_, observed, _ := na.Outcome(pawnsReply, "observed")
	outcome = na.OutcomeFromControl(c.Name, after)
	outcome.HealthyColonists = len(na.AsSlice(observed["pawns"]))
	outcome.UnsuccessfulStages = unsuccessful
	report["outcome"] = outcome
	return outcome, nil
}

// checkStartupLog is the run's last assertion: no native error in the
// game's startup log.
func checkStartupLog(s cases.Session) error {
	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}

// control reads the stage's counters through test/throughput_control.
func (m *matrix) control(ctx context.Context, h *na.Harness, identity map[string]any, label string) (map[string]any, error) {
	reply, err := h.Call(ctx, label, controlTool, map[string]any{
		"colonyId": na.AsString(identity["colonyId"]), "loadToken": na.AsString(identity["loadToken"]),
		"mapId": int(na.AsNumber(identity["mapId"])), "storageCells": m.storage, "wallCells": m.walls,
	})
	if err != nil {
		return nil, err
	}
	if ok, _ := na.AsBool(reply["success"]); !ok {
		return nil, fmt.Errorf("%s refused: %#v", controlTool, reply)
	}
	return reply, nil
}

// submitWalls submits every staged wall segment as its own building plan
// (the API takes one building per request) and returns the plan ids.
func (m *matrix) submitWalls(service *na.ServiceProcess, prefix string, identity map[string]any, token string, report na.Report) ([]domain.PlanID, error) {
	var ids []domain.PlanID
	var submissions []map[string]any
	for i, site := range m.sites {
		building := map[string]any{
			"defName": na.AsString(site["defName"]), "x": int(na.AsNumber(site["x"])), "z": int(na.AsNumber(site["z"])),
			"rotation": na.AsString(site["rotation"]), "stuff": na.AsString(site["stuff"]),
		}
		submission, status, err := service.API("POST", "/api/buildings/plans", map[string]any{
			"requestId": fmt.Sprintf("%s-wall-%d", prefix, i+1), "expected": identity, "building": building,
		}, token)
		if err != nil {
			return nil, err
		}
		if status != 200 && status != 201 {
			return nil, fmt.Errorf("wall %d: unexpected submission status=%d body=%#v", i+1, status, submission)
		}
		id := na.AsString(submission["planId"])
		if id == "" {
			return nil, fmt.Errorf("wall %d: unexpected submission %#v", i+1, submission)
		}
		ids = append(ids, domain.PlanID(id))
		submissions = append(submissions, submission)
	}
	report["submissions"] = submissions
	return ids, nil
}

// stageWorkDone reports whether the stage has run out of work: every wall
// plan has observed its building completed and the routine review holds no
// MaintainStorage goal still active in deficit (a satisfied goal may have
// retired its binding). After that the clock scheduler refuses every window
// as no_work and the review tick no longer advances.
func stageWorkDone(ctx context.Context, s *store.Store, review store.RoutineReview, walls []domain.PlanID) (bool, error) {
	for _, id := range walls {
		state, err := s.LoadPlan(ctx, id)
		if err != nil {
			if errors.Is(err, store.ErrNotFound) {
				return false, nil
			}
			return false, err
		}
		if len(state.Progress) == 0 {
			return false, nil
		}
		for _, progress := range state.Progress {
			if progress.View().Stage != domain.Completed {
				return false, nil
			}
		}
	}
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainStorage {
			continue
		}
		goal, err := s.LoadGoal(ctx, binding.Goal)
		if err != nil {
			if errors.Is(err, store.ErrNotFound) {
				continue
			}
			return false, err
		}
		if goal.Goal.Status == domain.GoalActive && goal.Goal.Need == domain.NeedDeficit {
			return false, nil
		}
	}
	return true, nil
}

// countUnsuccessful counts Unsuccessful action stages over the wall plans
// and every routine goal method the service dispatched (the haul family's
// MaintainStorage among them). LoadPlan sees retired plans; LoadPlans would
// hide them.
func countUnsuccessful(ctx context.Context, s *store.Store, walls []domain.PlanID) (int, int, error) {
	seen := map[domain.PlanID]bool{}
	var ids []domain.PlanID
	for _, id := range walls {
		if !seen[id] {
			seen[id] = true
			ids = append(ids, id)
		}
	}
	review, err := s.LoadRoutineReview(ctx)
	if err != nil {
		return 0, 0, err
	}
	for _, binding := range review.Goals {
		goal, err := s.LoadGoal(ctx, binding.Goal)
		if err != nil {
			if errors.Is(err, store.ErrNotFound) {
				continue
			}
			return 0, 0, err
		}
		methods := goal.Methods
		if more, err := s.LoadGoalMethods(ctx, binding.Goal, goal.Goal.Epoch); err == nil {
			methods = append(methods, more...)
		}
		for _, method := range methods {
			if !seen[method.Plan] {
				seen[method.Plan] = true
				ids = append(ids, method.Plan)
			}
		}
	}
	unsuccessful := 0
	for _, id := range ids {
		state, err := s.LoadPlan(ctx, id)
		if err != nil {
			if errors.Is(err, store.ErrNotFound) {
				continue
			}
			return 0, 0, err
		}
		for _, progress := range state.Progress {
			if progress.View().Stage == domain.Unsuccessful {
				unsuccessful++
			}
		}
	}
	return unsuccessful, len(ids), nil
}

// caseMetrics is the per-speed row the issue asks for.
func caseMetrics(c na.SpeedCase, phases bridge.PhaseSummary, stops na.StopSummary, startTick, lastTick uint64, wallSeconds float64) map[string]any {
	readsPerStep := 0.0
	if phases.Steps.Steps > 0 {
		readsPerStep = float64(phases.Steps.Reads) / float64(phases.Steps.Steps)
	}
	// Time-weighted (bridge.ClockSample.PausedFraction): the count ratio
	// over-represents pauses, when the service issues most of its reads.
	pausedFraction := phases.Clock.PausedFraction()
	budgetTPS, budgetStopsPer6000 := 0.0, 0.0
	if wallSeconds > 0 && lastTick > startTick {
		budgetTPS = float64(lastTick-startTick) / wallSeconds
	}
	if lastTick > startTick {
		budgetStopsPer6000 = float64(stops.BudgetStops) * 6000 / float64(lastTick-startTick)
	}
	// The colony windows the service sized by wall time (issue #126) and
	// the stop-to-readmit pauses its admissions closed (issue #162).
	windowMean, pauseMean := 0.0, 0.0
	if phases.Steps.Windows > 0 {
		windowMean = float64(phases.Steps.WindowTicks) / float64(phases.Steps.Windows)
	}
	if phases.Steps.Pauses > 0 {
		pauseMean = phases.Steps.PauseSecs / float64(phases.Steps.Pauses)
	}
	return map[string]any{
		"case": c.Name, "speed": c.Speed, "test_acceleration": c.TestAcceleration,
		"ticks_advanced": lastTick - startTick, "wall_seconds": wallSeconds, "budget_wall_tps": budgetTPS,
		"wall_tps": phases.Clock.WallTPS, "paused_fraction": pausedFraction, "paused_samples": phases.Clock.PausedSamples, "clock_samples": phases.Clock.ClockSamples, "paused_sampled_seconds": phases.Clock.SampledSecs,
		"steps": phases.Steps.Steps, "reads_per_step": readsPerStep, "parent_hits": phases.Steps.ParentHits,
		"window_ticks_mean": windowMean, "window_ticks_max": phases.Steps.MaxWindowTicks,
		"cache_hits": phases.Steps.CacheHits, "stops": stops.Stops, "budget_stops": stops.BudgetStops, "budget_stops_per_6000_ticks": budgetStopsPer6000,
		"reactive_stops": stops.ReactiveStops, "stop_reasons": stops.Reasons,
		"stop_latency_mean_ms": stops.MeanLatencyMs, "stop_latency_max_ms": stops.MaxLatencyMs,
		"readmit_pause_count": phases.Steps.Pauses, "readmit_pause_mean_s": pauseMean, "readmit_pause_max_s": phases.Steps.MaxPauseSecs,
		// Live dispatch (#243): steps by reason ("live" plans under a running
		// window), the worker's native runs made under a running window and
		// the fraction native refused.
		"step_reasons": phases.Steps.Reasons, "dispatches": phases.Dispatch.Calls, "live_dispatches": phases.Dispatch.Live,
		"refused_dispatches": phases.Dispatch.Refused, "live_refused_dispatches": phases.Dispatch.LiveRefused, "refused_fraction": phases.Dispatch.RefusedFraction(),
	}
}

func appendMetrics(report na.Report, row map[string]any) {
	rows, _ := report["speed_metrics"].([]map[string]any)
	report["speed_metrics"] = append(rows, row)
}

// cellList renders fixture cell objects as the "x:z,x:z" list
// test/throughput_control parses.
func cellList(v any) string {
	var parts []string
	for _, raw := range na.AsSlice(v) {
		cell, _ := na.AsMap(raw)
		parts = append(parts, fmt.Sprintf("%d:%d", int(na.AsNumber(cell["x"])), int(na.AsNumber(cell["z"]))))
	}
	return strings.Join(parts, ",")
}

func randomSuffix() string {
	buf := make([]byte, 4)
	_, _ = rand.Read(buf)
	return hex.EncodeToString(buf)
}
