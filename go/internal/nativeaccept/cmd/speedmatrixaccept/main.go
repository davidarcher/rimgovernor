// Command speedmatrixaccept (issue #111, M4a) runs the same staged colony
// under rimgovernor serve once per clock speed -- Normal, Fast, Superfast,
// Ultrafast and uncapped (Ultrafast with the headless test acceleration,
// #109) -- with an identical game-tick budget, and requires the pawns to
// achieve the same outcome at every speed.
//
// The stage is test/throughput_prepare (ThroughputFixture.cs) applied once
// to a quiet debug colony with frozen needs: three or more colonists on
// Construct/Haul, loose Steel stacks with a stockpile to haul them to, and a
// contiguous run of legal Wall cells. That game is saved once; every speed
// reloads the save, so the map, pawns and stacks are the same. Per speed
// the harness releases the game to one serve process (haul + work
// families) with the speed's --clock-speed, submits the wall run as
// building plans, resumes automatic control and waits, stall-bounded,
// until the serve-side tick has advanced by the budget. It then stops the
// service, reads the outcome natively (stored units, walls built,
// RequireHealthyColonists) and counts unsuccessful plan stages in the
// service journal. The flight recorder gives wall TPS, paused fraction,
// steps, reads/step, parent hits and the budget-vs-reactive stop split with
// stop latency.
//
// Postconditions must agree within -tolerance (default 1) across speeds
// and no case may record an unsuccessful plan stage; report.json under
// -output carries the matrix and the process exits non-zero on failure.
package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	prepareTool = "test/throughput_prepare"
	controlTool = "test/throughput_control"
	stageSave   = "RimGovernor-speedmatrix-stage"
)

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-speedmatrix-acceptance)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless (refuses uncapped)")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	binary := flag.String("rimgovernor", "", "path to the rimgovernor binary (default: PATH lookup)")
	speeds := flag.String("speeds", na.DefaultSpeedMatrix, "comma-separated cases to run, in order")
	ticks := flag.Int64("ticks", 6000, "game ticks each speed runs after resume (the serve-side tick must advance by this much)")
	items := flag.Int("items", 4, "Steel stacks the stage spawns (1..8)")
	segments := flag.Int("segments", 6, "wall segments the stage lays out (1..12)")
	tolerance := flag.Int("tolerance", 1, "allowed spread of each pawn-outcome postcondition across speeds")
	reuseGame := flag.Bool("reuse-game", true, "reload the stage through GameReuse's reset contract (issue #22); off reloads without the contract checks")
	timeout := flag.Duration("timeout", 60*time.Minute, "overall run timeout")
	stall := flag.Duration("stall", 0, "stall budget for each speed's wait (default na.StallBudget)")
	flag.Parse()
	if *root == "" {
		fmt.Fprintln(os.Stderr, "-root is required")
		os.Exit(2)
	}
	if *output == "" {
		*output = filepath.Join(*root, "native-speedmatrix-acceptance")
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	cases, err := na.ParseSpeedCases(*speeds)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	for _, c := range cases {
		if c.TestAcceleration && *rendered {
			fmt.Fprintln(os.Stderr, "the uncapped case needs the headless profile (test acceleration is headless-only); drop it from -speeds or run without -rendered")
			os.Exit(2)
		}
	}
	if *binary == "" {
		*binary = "rimgovernor"
	}
	report := na.NewReport("Speed matrix (#111): one staged colony reloaded per clock speed under rimgovernor serve with an identical tick budget; wall TPS, paused fraction, steps, reads/step, parent hits, stop latency and budget-vs-reactive stops per speed; pawn outcomes equal within a tolerance.", !*rendered)
	report["speeds"] = cases
	report["tick_budget"] = *ticks
	report["tolerance"] = *tolerance
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	m := &matrix{root: *root, output: *output, gameID: *game, headless: !*rendered, binary: *binary,
		cases: cases, ticks: *ticks, items: *items, segments: *segments, tolerance: *tolerance,
		reuseGame: *reuseGame, stall: *stall, report: report}
	if err := m.run(ctx); err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

type matrix struct {
	root, output, gameID, binary string
	headless                     bool
	cases                        []na.SpeedCase
	ticks                        int64
	items, segments, tolerance   int
	reuseGame                    bool
	stall                        time.Duration
	report                       na.Report

	cfg      *na.Config
	gabs     string
	reuse    *na.GameReuse
	game     *na.Game
	prepared map[string]any
	// storage and walls are the fixture's cell lists, in the "x:z" form
	// test/throughput_control reads back.
	storage, walls string
	sites          []map[string]any
	outcomes       []na.SpeedOutcome
}

func (m *matrix) stallBudget() time.Duration {
	if m.stall > 0 {
		return m.stall
	}
	return na.StallBudget()
}

func (m *matrix) run(ctx context.Context) (err error) {
	if abs, err := filepath.Abs(m.root); err == nil {
		m.root = abs
	}
	if abs, err := filepath.Abs(m.output); err == nil {
		m.output = abs
	}
	m.cfg = &na.Config{Root: m.root, Output: m.output, Headless: m.headless, GameID: m.gameID}
	if err := m.cfg.PrepareConfig(); err != nil {
		return fmt.Errorf("prepare profile: %w", err)
	}
	game, err := m.cfg.GameSection()
	if err != nil {
		return err
	}
	files, err := na.PackageFiles(fmt.Sprint(game["workingDir"]))
	if err != nil {
		return err
	}
	m.report["package_files"] = files
	if m.gabs, err = na.GABSExecutable(m.root, m.cfg.Configuration); err != nil {
		return err
	}

	// One RimWorld process for the whole matrix. With -reuse-game the
	// reloads go through GameReuse's reset contract; otherwise a plain hold
	// reloads the stage itself.
	var h *na.Harness
	if m.reuseGame {
		if m.reuse, err = na.OpenReusableGame(ctx, m.cfg, m.gabs); err != nil {
			return err
		}
		defer func() {
			retireCtx, retireCancel := context.WithTimeout(context.Background(), 2*time.Minute)
			defer retireCancel()
			reason := "matrix complete"
			if err != nil {
				reason = "matrix failed: " + err.Error()
			}
			_ = m.reuse.Retire(retireCtx, reason)
			m.reuse.Record(m.report)
		}()
		if h, err = m.reuse.Session(ctx); err != nil {
			return err
		}
	} else {
		if m.game, err = na.OpenGame(ctx, m.cfg); err != nil {
			return err
		}
		defer m.game.Close(m.report)
		h = na.NewHarness(m.game.Client, m.output)
	}
	h.Output = filepath.Join(m.output, "stage")
	if err := os.MkdirAll(h.Output, 0755); err != nil {
		return err
	}
	if err := m.stage(ctx, h); err != nil {
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
	if problems := na.CompareOutcomes(m.outcomes, m.tolerance); len(problems) > 0 {
		m.report["outcome_problems"] = problems
		return fmt.Errorf("pawn outcomes differ across speeds: %s", strings.Join(problems, "; "))
	}
	// The stage must have been reachable: a matrix where nothing was
	// hauled or built agrees trivially and proves nothing.
	for _, o := range m.outcomes {
		if o.StoredUnits == 0 && o.WallsBuilt == 0 {
			return fmt.Errorf("%s: nothing was hauled or built within the tick budget; raise -ticks or check the stage", o.Case)
		}
	}
	logData, err := os.ReadFile(m.cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), m.headless)
}

// stage starts the quiet debug colony, freezes needs, applies the fixture
// and saves the result as stageSave for every case to reload.
func (m *matrix) stage(ctx context.Context, h *na.Harness) error {
	names, err := h.Discovery(ctx)
	if err != nil {
		return err
	}
	m.report["discovery"] = names
	for _, tool := range []string{prepareTool, controlTool} {
		if !na.Contains(names, tool) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture ThroughputFixture", tool)
		}
	}
	quiet, err := na.StartDebugGame(ctx, h, names, na.QuietRequired)
	if err != nil {
		return err
	}
	m.report["quiet"] = quiet
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	if err := confirmColonyNames(ctx, h, m.report); err != nil {
		return err
	}
	prepared, err := h.Call(ctx, "prepare", prepareTool, map[string]any{"itemCount": m.items, "wallSegments": m.segments})
	if err != nil {
		return err
	}
	if ok, _ := na.AsBool(prepared["success"]); !ok {
		return fmt.Errorf("%s refused: %#v", prepareTool, prepared)
	}
	m.prepared = prepared
	m.report["prepared"] = prepared
	m.storage = cellList(prepared["storageCells"])
	m.walls = cellList(prepared["sites"])
	for _, raw := range na.AsSlice(prepared["sites"]) {
		site, _ := na.AsMap(raw)
		m.sites = append(m.sites, site)
	}
	if len(m.sites) != m.segments {
		return fmt.Errorf("stage laid out %d wall sites, want %d", len(m.sites), m.segments)
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
	deadline := started.Add(90 * time.Second)
	for {
		for _, profile := range []string{"headless-profile", "profile"} {
			candidate := filepath.Join(m.root, profile, "Saves", stageSave+".rws")
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
			return fmt.Errorf("stage save %s did not appear under %s within 90s", stageSave, m.root)
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
	output := filepath.Join(m.output, c.Name)
	if err := os.MkdirAll(output, 0755); err != nil {
		return outcome, err
	}
	report := na.NewReport("speed case "+c.Name, m.headless)
	report["case"] = c
	defer func() {
		if err != nil {
			report["error"] = err.Error()
		} else {
			report["passed"] = true
		}
		report.Finalize(output)
	}()
	cfg := *m.cfg
	cfg.Output = output

	var h *na.Harness
	var service *na.ServiceProcess
	var reuseCase *na.ReuseCase
	stopped := false
	if m.reuse != nil {
		if reuseCase, err = m.reuse.BeginCase(ctx, c.Name, stageSave, output); err != nil {
			return outcome, err
		}
		h = reuseCase.Harness
		report["reuse_case"] = map[string]any{"loadToken": reuseCase.Reset.LoadToken, "tick": reuseCase.Reset.Tick}
		defer func() {
			if stopped {
				return
			}
			stopped = true
			if service != nil {
				service.Stop()
			}
			endCtx, endCancel := context.WithTimeout(context.Background(), 2*time.Minute)
			defer endCancel()
			if endErr := m.reuse.EndCase(endCtx, reuseCase, err != nil); endErr != nil && err == nil {
				err = endErr
			}
		}()
	} else {
		client, reattachErr := m.game.Reattach(ctx)
		if reattachErr != nil {
			return outcome, reattachErr
		}
		h = na.NewHarness(client, output)
		defer func() {
			if stopped {
				return
			}
			stopped = true
			if service != nil {
				service.Stop()
			}
		}()
		if _, err := h.Call(ctx, "load-stage", "rimworld/load_game_ready", map[string]any{
			"saveName": stageSave, "readiness": "visual", "timeoutMs": 90000, "ignoreModCompatibility": false,
		}); err != nil {
			return outcome, err
		}
		if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
			return outcome, err
		}
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
	if reuseCase != nil && !na.MatchesIdentity(identity, reuseCase.Identity) {
		return outcome, fmt.Errorf("identity %#v does not match the reuse case %#v", identity, reuseCase.Identity)
	}
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
	release := m.reuse.ReleaseSession
	if m.reuse == nil {
		release = m.game.Release
	}
	if err := release(); err != nil {
		return outcome, fmt.Errorf("release the game to the service: %w", err)
	}

	extra := append(c.ServeArgs(), na.FlightRecorderArgs(output, true)...)
	service, err = na.LaunchService(ctx, &cfg, m.gabs, na.ServiceLaunch{Binary: m.binary, Families: []string{"haul", "work"}, Extra: extra}, report)
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
	// The budget is game time: the wait ends once the service's own review
	// tick has advanced by -ticks past the reload tick. The signature is
	// the tick itself, so a clock that stops advancing stalls the wait.
	var lastTick uint64
	waitErr := na.WaitProgress(ctx, na.Wait{Stall: m.stallBudget(), Interval: 2 * time.Second, Terminal: service.Exited},
		func(ctx context.Context) (string, bool, error) {
			review, err := journal.LoadRoutineReview(ctx)
			if err != nil {
				return "", false, err
			}
			lastTick = uint64(review.Tick)
			return na.Signature(lastTick), lastTick >= startTick+uint64(m.ticks), nil
		})
	wallSeconds := time.Since(resumedAt).Seconds()
	report["wait"] = map[string]any{"start_tick": startTick, "last_tick": lastTick, "wall_seconds": wallSeconds}
	if waitErr != nil {
		if final, loadErr := journal.LoadRoutineReview(ctx); loadErr == nil {
			data, _ := json.Marshal(final)
			report["routine_review_at_failure"] = json.RawMessage(data)
		}
		return outcome, fmt.Errorf("tick budget wait (start %d, last %d, want +%d): %w", startTick, lastTick, m.ticks, waitErr)
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
	if m.reuse != nil {
		if h, err = m.reuse.Session(ctx); err != nil {
			return outcome, err
		}
		h.Output = output
	} else {
		client, reattachErr := m.game.Reattach(ctx)
		if reattachErr != nil {
			return outcome, reattachErr
		}
		h = na.NewHarness(client, output)
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return outcome, err
	}
	// The service was stopped, not shut down: its authority grant lingers
	// until the tick budget lapses, and EndCase would retire the game over
	// it.
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
	pausedFraction := 0.0
	if phases.Clock.ClockSamples > 0 {
		pausedFraction = float64(phases.Clock.PausedSamples) / float64(phases.Clock.ClockSamples)
	}
	budgetTPS := 0.0
	if wallSeconds > 0 && lastTick > startTick {
		budgetTPS = float64(lastTick-startTick) / wallSeconds
	}
	return map[string]any{
		"case": c.Name, "speed": c.Speed, "test_acceleration": c.TestAcceleration,
		"ticks_advanced": lastTick - startTick, "wall_seconds": wallSeconds, "budget_wall_tps": budgetTPS,
		"wall_tps": phases.Clock.WallTPS, "paused_fraction": pausedFraction,
		"steps": phases.Steps.Steps, "reads_per_step": readsPerStep, "parent_hits": phases.Steps.ParentHits,
		"cache_hits": phases.Steps.CacheHits, "stops": stops.Stops, "budget_stops": stops.BudgetStops,
		"reactive_stops": stops.ReactiveStops, "stop_reasons": stops.Reasons,
		"stop_latency_mean_ms": stops.MeanLatencyMs, "stop_latency_max_ms": stops.MaxLatencyMs,
	}
}

func appendMetrics(report na.Report, row map[string]any) {
	rows, _ := report["metrics"].([]map[string]any)
	report["metrics"] = append(rows, row)
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

// confirmColonyNames dismisses the debug colony's naming dialog, which
// otherwise holds the clock (STOP_REASON_COLONY_NAMING).
func confirmColonyNames(ctx context.Context, h *na.Harness, report na.Report) error {
	facts, err := h.Call(ctx, "colony-facts", "home/colony_facts", map[string]any{})
	if err != nil {
		return err
	}
	naming, ok := na.AsMap(facts["colonyNaming"])
	if !ok || naming == nil {
		report["confirmed_colony_names"] = "no pending naming dialog"
		return nil
	}
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
	return nil
}
