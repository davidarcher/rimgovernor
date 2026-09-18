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

	// Sequential native sessions, exactly like sustainedfood.Run: this
	// harness's own session starts the debug game and seeds the fixtures,
	// then releases (without games_stop) to free the sole GABP slot for the
	// service; Close reattaches at the end, on every path.
	held, err := na.OpenGame(ctx, naCfg)
	if err != nil {
		return nil, err
	}
	defer held.Close(report)
	h := na.NewHarness(held.Client, output)

	if _, err := na.StartDebugGame(ctx, h, nil, na.QuietRequired); err != nil {
		return nil, err
	}
	if _, err := h.Call(ctx, "pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return nil, err
	}
	identity, err := na.ReadIdentity(ctx, h, "identity")
	if err != nil {
		return nil, err
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

	// Medical: tend for the two Flu patients and the forced withdrawal
	// patient's CriticalMedicine deficit, plus medicine-reserve replenishment
	// so tend never runs the fixture's stocked medicine dry. Food: the same
	// EnsureFoodSupply pipeline sustainedfood exercises, to prove the
	// pre-seeded growing zone/campfire bill keeps advancing concurrently with
	// medical dispatch.
	spec := na.ServeSpec{
		Binary:        cfg.RimgovernorBinary,
		Families:      []string{"tend,medical,field,food-storage,acquisition,cooking,supply,production-policy"},
		NativeTimeout: cfg.NativeTimeout, Prefix: prefix, ClockSpeed: cfg.ClockSpeed,
	}
	// Serve releases the harness session first (no games_stop, so the
	// fixture-seeded colony survives) and waits for the service to attach.
	service, err := na.Serve(ctx, naCfg, held, identity, spec, report)
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
	service.KeepAuthority(ctx)

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

	stopService()
	if _, err := held.Reattach(ctx); err != nil {
		return timeline, err
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
