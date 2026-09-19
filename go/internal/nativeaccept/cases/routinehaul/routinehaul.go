// Package routinehaul holds the RoutineHaulPlanner/MaintainStorage
// vertical (G01.07b 05.2): a live game and a live rimgovernor Go
// player-control service. A worker is assigned and hauls a real ordinary
// (non-decaying) item into shared storage, the stored outcome is observed
// via a native read, a renewed deficit (a second item, spawned forbidden and
// released once the first haul is stored) is picked up without a duplicate
// or conflicting order, and a player-revoked Hauling priority interrupts
// dispatch without RoutineHaulPlanner overriding player intent or
// double-issuing. Uses a private disposable fixture
// (test/storage_haul_prepare, test/storage_haul_control,
// test/storage_haul_allow) since native random colony generation cannot
// reliably produce a MaintainStorage deficit (an ordinary item outside legal
// storage) with a deterministic single eligible hauler, and the hauler's own
// vanilla work scanner would haul an unforbidden second item the moment its
// ordered haul ends, before the planner could renew;
// test/guarded_construction_prepare supplies the one player-submitted
// building plan MaintainStorage's own arbitration capacity slot requires to
// be occupied by "the accepted player project," matching
// policy.RankDevelopment/AuditDevelopment's own accounting.
//
// The case's own bridge session prepares the fixture and takes independent
// native reads; a prebuilt rimgovernor "serve" binary is then launched
// against the *same* GABS configuration/game so its own routine reviewer
// and haul planner/executor drive the actual native dispatch under test --
// the planner and its dispatch are Go-owned production logic
// (buildingruntime.RoutineHaulPlanner), not something a bridge fixture can
// exercise directly. Only one GABP client can be connected to the running
// game at a time, so the two sessions are used sequentially: Serve
// releases the case's session before the service starts; the session is
// reattached once the service is stopped, to release the second item
// (the service is then restarted on the same journal) and for the final
// native read.
package routinehaul

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func init() {
	cases.Register(cases.Case{
		Name: "routinehaul/storage",
		Scope: "Native RoutineHaulPlanner/MaintainStorage vertical: a worker hauls a real " +
			"ordinary item into shared storage under the live Go routine reviewer/planner/executor, the stored " +
			"outcome is confirmed by an independent native read, a renewed deficit is picked up without a " +
			"duplicate order, and a player-revoked Hauling priority interrupts dispatch without the planner " +
			"overriding player intent or double-issuing.",
		Start:   cases.Fixture{Op: "test/storage_haul_prepare", Args: map[string]any{"itemCount": 2}},
		Service: true,
		Budget:  cases.MaxBudget,
		Run:     run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	defer na.ReportPhases(report, s.Config().Output, true)
	for _, want := range []string{"test/storage_haul_control", "test/storage_haul_allow", "test/guarded_construction_prepare"} {
		if !na.Contains(s.Names(), want) {
			return fmt.Errorf("missing %s in discovery; rebuild the native mod with -Fixture StorageHaulFixture -Fixture GuardedConstructionFixture", want)
		}
	}
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	report["prepared_storage"] = prepared
	haulerID := na.AsString(prepared["haulerId"])
	var itemIDs []string
	for _, raw := range na.AsSlice(prepared["itemIds"]) {
		itemIDs = append(itemIDs, fmt.Sprint(raw))
	}
	storageCell, _ := na.AsMap(prepared["storageCell"])
	storageX, storageZ := int(na.AsNumber(storageCell["x"])), int(na.AsNumber(storageCell["z"]))
	if haulerID == "" || len(itemIDs) != 2 {
		return fmt.Errorf("storage_haul_prepare: unexpected fixture identifiers: %#v", prepared)
	}
	// Confirm, from the fixture's own spawn-time coordinates, that each item
	// genuinely starts outside storage -- a pre-condition of this being real
	// acceptance evidence, not a fixture that already put it there -- and
	// that only the first is unforbidden, so the second is no deficit (and
	// no vanilla haul target) until the case releases it. Taken while the
	// harness is still connected, before it hands the sole GABP slot to the
	// service.
	for i, raw := range na.AsSlice(prepared["items"]) {
		item, _ := na.AsMap(raw)
		x, z := int(na.AsNumber(item["x"])), int(na.AsNumber(item["z"]))
		if x == storageX && z == storageZ {
			return fmt.Errorf("item %v already at the storage cell before any haul: %#v", item["id"], item)
		}
		if forbidden, _ := na.AsBool(item["forbidden"]); forbidden != (i > 0) {
			return fmt.Errorf("item %v forbidden=%v at spawn, want %v: %#v", item["id"], forbidden, i > 0, item)
		}
	}

	construction, err := h.Call(ctx, "prepare-construction", "test/guarded_construction_prepare", map[string]any{"siteCount": 1})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(construction["success"]); !success || !na.MatchesIdentity(construction, identity) {
		return fmt.Errorf("guarded_construction_prepare refused or identity mismatch: %#v", construction)
	}
	report["prepared_construction"] = construction
	sites := na.AsSlice(construction["sites"])
	if len(sites) != 1 {
		return fmt.Errorf("guarded_construction_prepare: expected exactly one site, got %#v", construction)
	}
	site0, _ := na.AsMap(sites[0])
	siteX, siteZ := int(na.AsNumber(site0["x"])), int(na.AsNumber(site0["z"]))

	// Launch the live Go player-control service, joined to the same GABS
	// configuration/game the harness above already started; Serve releases
	// the harness session first (no games_stop, so the running game
	// survives) and waits for the service to attach to the same identity.
	// TEMPORARY: surface ClockScheduler.Step()'s branch tracing while
	// root-causing why the routine review never persists (G01.07b). Remove
	// this env injection once resolved.
	// Compose only the haul family so the receipt under test is unambiguous.
	service, err := s.Serve(ctx, na.ServeSpec{
		Prefix:   "routine-haul",
		Families: []string{"haul"}, Env: []string{"RIMGOVERNOR_CLOCK_DEBUG=1"},
	})
	if err != nil {
		return err
	}
	if sum, err := sha256File(service.Spec.Binary); err == nil {
		report["rimgovernor_binary"] = map[string]string{"path": service.Spec.Binary, "sha256": sum}
	} else {
		return fmt.Errorf("hash rimgovernor binary: %w", err)
	}
	stopService := func() {
		if keep := service.Stop(); keep != nil {
			report["authority_reacquisitions"] = keep
		}
	}
	defer stopService()
	apiCall, token := service.API, service.Token
	// exited is the journal waits' na.Wait.Terminal: a service that died on
	// its own ends a wait at once instead of letting it stall out.
	w := na.Wait{Stall: na.StallBudget(), Terminal: service.Exited}

	// Submit the one guarded-construction building plan whose acceptance
	// occupies MaintainStorage's own arbitration capacity slot as "the
	// accepted player project" -- see policy.RankDevelopment.
	submission, status, err := apiCall("POST", "/api/buildings/plans", map[string]any{
		"requestId": s.RequestID("routine-haul-construction-1"),
		"expected":  identity,
		"building":  map[string]any{"defName": "Wall", "x": siteX, "z": siteZ, "rotation": "north", "stuff": "WoodLog"},
	}, token)
	if err != nil {
		return err
	}
	if status != 200 && status != 201 {
		return fmt.Errorf("unexpected building submission status=%d body=%#v", status, submission)
	}
	if na.AsString(submission["planId"]) == "" || na.AsString(submission["revision"]) == "" {
		return fmt.Errorf("unexpected building submission: %#v", submission)
	}
	report["submission"] = submission

	acquireBody := map[string]any{
		"requestId": s.RequestID("routine-haul-resume-1"), "expected": identity,
	}
	acquired, status, err := apiCall("POST", "/api/player/control/resume", acquireBody, token)
	if err != nil {
		return err
	}
	if status != 200 {
		return fmt.Errorf("unexpected acquire status=%d body=%#v", status, acquired)
	}
	acquiredRecord, _ := na.AsMap(acquired["record"])
	if na.AsString(acquiredRecord["phase"]) != "running" {
		return fmt.Errorf("resume was not running: %#v", acquired)
	}
	report["acquired"] = acquired
	// Work preferences hang off the world's root plan (the live authority),
	// not the submitted guidance plan.
	acquiredState, _ := na.AsMap(acquired["state"])
	acquiredGeneration, _ := na.AsMap(acquiredState["generation"])
	rootPlanID := na.AsString(acquiredGeneration["plan"])
	if rootPlanID == "" {
		return fmt.Errorf("resume reported no root plan: %#v", acquired)
	}

	verifyStore, err := service.Store(ctx)
	if err != nil {
		return err
	}

	// Native player authority is a bounded generation, not a standing grant:
	// it legitimately lapses -- on a bounded clock window running out
	// (NativeControlRevocationReason.GenerationExhausted; see
	// go/cmd/rimgovernor/serve_clock.go's MaxTicks/LeaseMS) or on ANY native
	// clock event PollEvents does not recognize as benign
	// (clockPollInterrupts in go/internal/buildingruntime/clock_poll.go
	// allowlists only Started/SpeedChanged/HostilesCleared/
	// ForcePauseCleared and a Stopped tagged TICK_BUDGET or REQUESTED_PAUSE;
	// everything else, e.g. a random world letter like a colonist pregnancy
	// notification, is conservatively treated as a genuine interruption) --
	// and nothing re-acquires it automatically: Control.Acquire's own comment
	// is explicit that this is "an authenticated explicit-player entrypoint"
	// action, never something ClockScheduler.Step() does on its own. Observed
	// live: a fresh disposable colony can take an unrelated interruption hold
	// (a "Kimmy pregnant" letter) within the first couple of review cycles,
	// well before either haul even begins -- so a one-shot acquire is not
	// enough to cover even the initial ramp-up, let alone the two full haul
	// dispatches this run waits through. Started immediately after the first
	// acquire so it can recover from an interruption at any point, including
	// during the startup diagnostic window below.
	service.KeepAuthority(ctx)

	// Diagnostic: confirm the service's own clock scheduler actually starts
	// stepping (mode reaches "automate") and a routine review gets persisted
	// before waiting on the routine review journal directly -- if the
	// scheduler never reaches automate, no review will ever be written and
	// waitHaulMethod would otherwise just time out with no clue why. Runs
	// concurrently with keepAlive above, so an early benign interruption
	// (see its comment) gets a real chance to be recovered from within this
	// same window rather than failing the whole run outright.
	diagDeadline := time.Now().Add(60 * time.Second)
	var diagnostics []map[string]any
	sawAutomate := false
	var review store.RoutineReview
	for time.Now().Before(diagDeadline) {
		st, _, stErr := apiCall("GET", "/api/state", nil, "")
		clk, _, clkErr := apiCall("GET", "/api/player/clock", nil, "")
		entry := map[string]any{"state": st, "clock": clk}
		if stErr != nil {
			entry["state_error"] = stErr.Error()
		}
		if clkErr != nil {
			entry["clock_error"] = clkErr.Error()
		}
		diagnostics = append(diagnostics, entry)
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
	report["diagnostic_post_acquire"] = diagnostics
	reviewData, _ := json.Marshal(review)
	report["diagnostic_routine_review"] = json.RawMessage(reviewData)
	if !sawAutomate {
		return fmt.Errorf("service never reached automate mode after acquire; see diagnostic_post_acquire in the report")
	}
	if review.Revision == 0 {
		return fmt.Errorf("service reached automate mode but the routine review was never persisted (revision 0); see diagnostic_post_acquire/diagnostic_routine_review")
	}

	// Item 1: a worker is assigned and hauls it into shared storage; poll the
	// production journal for the goal binding, the committed method's plan,
	// and its Completed stage -- these transitions are set only by the
	// production executor's own native observation of completion, the same
	// mechanism the live game and the live service just exercised for real.
	goalID, method1, err := waitHaulMethod(ctx, verifyStore, w, "", nil)
	if err != nil {
		return fmt.Errorf("first haul method: %w", err)
	}
	report["goal_id"] = string(goalID)
	item1, method1, renewals1, err := waitHaulItem(ctx, verifyStore, w, goalID, method1)
	if err != nil {
		return fmt.Errorf("first haul completion: %w", err)
	}
	report["first_haul_incidental_renewals"] = renewals1
	if !na.Contains(itemIDs, item1) {
		return fmt.Errorf("first haul moved an unexpected item %q", item1)
	}
	item2Expected := itemIDs[0]
	if item1 == itemIDs[0] {
		item2Expected = itemIDs[1]
	}
	report["first_haul_item"] = item1

	// Interruption: the player revokes the single eligible hauler's Hauling
	// priority before the renewed deficit (the second item) exists.
	// RoutineHaulPlanner must neither dispatch a new haul while overridden
	// nor double-issue once the override lifts.
	preferences, status, err := apiCall("GET", "/api/player/work-preferences?planId="+rootPlanID, nil, "")
	if err != nil {
		return err
	}
	if status != 200 {
		return fmt.Errorf("unexpected work-preferences read status=%d body=%#v", status, preferences)
	}
	baseRevision := na.AsString(preferences["revision"])
	revokeBody := map[string]any{
		"requestId": s.RequestID("routine-haul-revoke-hauling"), "planId": rootPlanID, "expected": identity,
		"expectedRevision": baseRevision,
		"overrides":        []map[string]any{{"pawn": haulerID, "work": "Hauling", "priority": 0}},
	}
	revoked, status, err := apiCall("POST", "/api/player/work-preferences/replace", revokeBody, token)
	if err != nil {
		return err
	}
	if status != 200 {
		return fmt.Errorf("unexpected work-preferences revoke status=%d body=%#v", status, revoked)
	}
	report["revoked_hauling"] = revoked

	// Renew the deficit: release the second item. The fixture control needs
	// the harness's own bridge session, so the service is stopped (its
	// journal -- goal, methods, the revoke above -- persists), the session
	// reattached for the one native write, and the service restarted on the
	// same state. The game keeps running throughout; the restart acquires
	// authority again exactly as the first launch did.
	stopService()
	allowHarness, err := s.Reattach(ctx)
	if err != nil {
		return fmt.Errorf("reopen bridge session to release the second item: %w", err)
	}
	allowed, err := allowHarness.Call(ctx, "allow-second-item", "test/storage_haul_allow", map[string]any{
		"colonyId": identity["colonyId"], "loadToken": identity["loadToken"], "mapId": identity["mapId"],
		"itemId": item2Expected,
	})
	if err != nil {
		return err
	}
	if success, _ := na.AsBool(allowed["success"]); !success {
		return fmt.Errorf("storage_haul_allow refused: %#v", allowed)
	}
	if forbidden, _ := na.AsBool(allowed["forbidden"]); forbidden {
		return fmt.Errorf("second item still forbidden after storage_haul_allow: %#v", allowed)
	}
	report["allowed_second_item"] = allowed
	restartDeadline := time.Now().Add(90 * time.Second)
	var restarted *na.ServiceProcess
	for {
		restarted, err = service.Restart(ctx)
		if err == nil {
			break
		}
		if time.Now().After(restartDeadline) {
			return fmt.Errorf("restarted service never attached: %w", err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(3 * time.Second):
		}
	}
	stopRestarted := func() {
		if keep := restarted.Stop(); keep != nil {
			report["authority_reacquisitions_restarted"] = keep
		}
	}
	defer stopRestarted()
	report["restarted_pid"] = restarted.PID
	apiCall, token = restarted.API, restarted.Token
	w = na.Wait{Stall: na.StallBudget(), Terminal: restarted.Exited}
	if _, err = restarted.Acquire(); err != nil {
		return fmt.Errorf("re-acquire after restart: %w", err)
	}
	restarted.KeepAuthority(ctx)
	if verifyStore, err = restarted.Store(ctx); err != nil {
		return err
	}

	// The quiet window only proves something once the restarted service has
	// reviewed the renewed deficit: wait for a MaintainStorage binding whose
	// goal is active and in deficit, then take the method-count baseline
	// there rather than after the first haul (a satisfied goal may have
	// retired its bindings in between).
	var quietGoal domain.GoalID
	err = na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := verifyStore.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.MaintainStorage {
				continue
			}
			goal, err := verifyStore.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			if err == nil && goal.Goal.Status == domain.GoalActive && goal.Goal.Need == domain.NeedDeficit {
				quietGoal = binding.Goal
				return "", true, nil
			}
			return na.Signature("goal", binding.Goal, goal.Goal.Status, goal.Goal.Need), false, nil
		}
		return na.Signature("review", review.Revision), false, nil
	})
	if err != nil {
		return fmt.Errorf("renewed deficit never reviewed after the restart: %w", err)
	}
	report["renewed_goal_id"] = string(quietGoal)
	baselineGoal, err := verifyStore.LoadGoal(ctx, quietGoal)
	if err != nil {
		return fmt.Errorf("load goal at the quiet window: %w", err)
	}
	baselineMethodCount := baselineGoal.Admitted

	// Observe several review cycles: no new method should appear for the
	// renewed deficit while the only eligible hauler is overridden off.
	quietDeadline := time.Now().Add(20 * time.Second)
	for time.Now().Before(quietDeadline) {
		goal, err := verifyStore.LoadGoal(ctx, quietGoal)
		if err != nil {
			return fmt.Errorf("poll during interruption: %w", err)
		}
		if goal.Admitted > baselineMethodCount {
			return fmt.Errorf("RoutineHaulPlanner dispatched a new haul while the only eligible hauler's Hauling priority was revoked: %#v", goal.Methods)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(1 * time.Second):
		}
	}
	report["interruption_held"] = true

	// Clear the override: hauling for the second item must now proceed,
	// exactly once, with no duplicate/conflicting order.
	clearedRevision := na.AsString(revoked["revision"])
	restoreBody := map[string]any{
		"requestId": s.RequestID("routine-haul-restore-hauling"), "planId": rootPlanID, "expected": identity,
		"expectedRevision": clearedRevision, "overrides": []map[string]any{},
	}
	restored, status, err := apiCall("POST", "/api/player/work-preferences/replace", restoreBody, token)
	if err != nil {
		return err
	}
	if status != 200 {
		return fmt.Errorf("unexpected work-preferences restore status=%d body=%#v", status, restored)
	}
	report["restored_hauling"] = restored

	_, method2, err := waitHaulMethod(ctx, verifyStore, w, quietGoal, &method1)
	if err != nil {
		return fmt.Errorf("second haul method: %w", err)
	}
	item2, _, renewals2, err := waitHaulItem(ctx, verifyStore, w, quietGoal, method2)
	if err != nil {
		return fmt.Errorf("second haul completion: %w", err)
	}
	report["second_haul_incidental_renewals"] = renewals2
	if item2 != item2Expected {
		return fmt.Errorf("second haul moved item %q, expected the renewed deficit %q", item2, item2Expected)
	}
	finalGoal, err := verifyStore.LoadGoal(ctx, quietGoal)
	if err != nil {
		return err
	}
	// One deliberate second dispatch on top of the quiet-window baseline,
	// plus whatever incidental authority-discontinuity renewals waitHaulItem
	// transparently absorbed while waiting for it -- still no
	// duplicate/conflicting order, just accounting for legitimate renewals
	// rather than a hardcoded count.
	wantMethodCount := baselineMethodCount + 1 + renewals2
	if finalGoal.Admitted != wantMethodCount {
		return fmt.Errorf("expected exactly %d committed haul methods (no duplicates; baseline=%d incidental_renewals=%d+%d), got %d: %#v", wantMethodCount, baselineMethodCount, renewals1, renewals2, finalGoal.Admitted, finalGoal.Methods)
	}
	report["second_haul_item"] = item2

	if err := na.AssertRoutineRunning(func(method, path string) (map[string]any, error) {
		v, status, err := apiCall(method, path, nil, "")
		if err != nil {
			return nil, err
		}
		if status != 200 {
			return nil, fmt.Errorf("%s %s: status %d", method, path, status)
		}
		return v, nil
	}); err != nil {
		return err
	}

	// Stop the service and its own bridge session to free the sole GABP slot
	// again, then reattach the harness session (the running game is
	// untouched) to take an independent native read of the stored outcome --
	// both hauled stacks merged into the one legal stockpile cell,
	// unforbidden. Reattach retries while the killed service's own GABS
	// subprocess frees the slot.
	stopRestarted()
	finalHarness, err := s.Reattach(ctx)
	if err != nil {
		return fmt.Errorf("reopen bridge session for final native check: %w", err)
	}

	afterBoth, err := finalHarness.Call(ctx, "after-both-hauls", "test/storage_haul_control", map[string]any{
		"colonyId": identity["colonyId"], "loadToken": identity["loadToken"], "mapId": identity["mapId"],
		"byCell": true, "x": storageX, "z": storageZ,
	})
	if err != nil {
		return err
	}
	if spawned, _ := na.AsBool(afterBoth["spawned"]); !spawned || int(na.AsNumber(afterBoth["count"])) < 50 {
		return fmt.Errorf("storage cell does not show both hauled items merged: %#v", afterBoth)
	}
	if inStockpile, _ := na.AsBool(afterBoth["inStockpile"]); !inStockpile {
		return fmt.Errorf("storage cell is not registered as the legal stockpile: %#v", afterBoth)
	}
	if forbidden, _ := na.AsBool(afterBoth["forbidden"]); forbidden {
		return fmt.Errorf("stored items unexpectedly forbidden: %#v", afterBoth)
	}
	report["after_both_hauls_storage"] = afterBoth

	return checkStartupLog(s)
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

// waitHaulMethod polls the durable routine review for a MaintainStorage goal
// binding and its (newly appearing) committed haul method, mirroring exactly
// what buildingruntime.RoutineHaulPlanner.step itself reads: review.Goals for
// the MaintainStorage Need, then that goal's Methods. previousMethod, when
// non-nil, is excluded so this waits specifically for a fresh renewal rather
// than re-observing the same method.
func waitHaulMethod(ctx context.Context, s *store.Store, w na.Wait, knownGoal domain.GoalID, previousMethod *domain.GoalMethod) (domain.GoalID, domain.GoalMethod, error) {
	var foundGoal domain.GoalID
	var found domain.GoalMethod
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		var goalID domain.GoalID
		for _, binding := range review.Goals {
			if binding.Need == policy.MaintainStorage {
				goalID = binding.Goal
				break
			}
		}
		if goalID == "" || knownGoal != "" && goalID != knownGoal {
			return na.Signature("goal", goalID), false, nil
		}
		goal, err := s.LoadGoal(ctx, goalID)
		if err != nil && !errors.Is(err, store.ErrNotFound) {
			return "", false, err
		}
		for _, method := range goal.Methods {
			if previousMethod == nil || method.Method != previousMethod.Method {
				foundGoal, found = goalID, method
				return "", true, nil
			}
		}
		return na.Signature("goal", goalID, goal.Goal.Epoch, len(goal.Methods)), false, nil
	})
	if err != nil {
		return "", domain.GoalMethod{}, err
	}
	return foundGoal, found, nil
}

// waitHaulCompleted polls one haul plan until its single action reaches a
// terminal stage. Completed returns the hauled item's thing id. Unsuccessful
// is always a genuine failure.
//
// Cancelled needs a closer look: domain.Progress.observe (go/internal/domain/progress.go)
// sets Stage=Cancelled from its EffectAbsent branch specifically when the
// action's *dispatch-time* GenerationSnapshot no longer matches the current
// one -- e.g. because the load token or native generation advanced. That happens whenever this
// harness's own authorityKeepAlive reacquires player authority mid-dispatch,
// which live observation confirms a disposable headless colony can trigger
// well before either haul even begins (an incidental native interruption --
// a random letter, not anything this test scripted -- see authorityKeepAlive's
// doc comment above). domain.Progress.Observe's own doc comment explains why
// this is correct, not a bug: it deliberately refuses to resolve a dispatch
// across an authority discontinuity, rather than risk misattributing its
// effect. So this Cancelled shape is not a test failure -- it is the executor
// safely abandoning an in-flight attempt, and the still-live MaintainStorage
// deficit is expected to get a fresh method on the next routine review. That
// signal is returned as incidentalCancel=true so the caller can wait for the
// renewal instead of failing outright -- see waitHaulItem.
//
// Any other Cancelled shape (Effect not observed as Absent) is treated as a
// genuine failure, same as Unsuccessful.
func waitHaulCompleted(ctx context.Context, s *store.Store, w na.Wait, planID domain.PlanID) (item string, incidentalCancel bool, err error) {
	_, err = na.WaitPlan(ctx, s, w, planID, func(state store.PlanState) (string, bool, error) {
		actions := state.Spec.Actions()
		if len(actions) != 1 || len(state.Progress) != 1 {
			return "", false, fmt.Errorf("unexpected haul plan shape: %d actions, %d progress", len(actions), len(state.Progress))
		}
		haul, ok := actions[0].Haul()
		if !ok {
			return "", false, fmt.Errorf("haul plan action is not a haul action")
		}
		view := state.Progress[0].View()
		switch view.Stage {
		case domain.Completed:
			item = haul.Thing()
			return "", true, nil
		case domain.Unsuccessful:
			return "", false, fmt.Errorf("haul plan %s reached unsuccessful instead of completed", planID)
		case domain.Cancelled:
			if effect, known := view.Effect.Value(); known && effect == domain.EffectAbsent {
				incidentalCancel = true
				return "", true, nil
			}
			return "", false, fmt.Errorf("haul plan %s reached cancelled instead of completed", planID)
		}
		return na.PlanSignature(state), false, nil
	})
	if err != nil {
		return "", false, err
	}
	return item, incidentalCancel, nil
}

// waitHaulItem waits for method's plan to complete, transparently following
// any renewal caused by an incidental authority-discontinuity cancellation
// (see waitHaulCompleted) by waiting for the routine reviewer to naturally
// bind a fresh method to the same still-live goal -- exactly the production
// recovery behavior a real player would see -- and retrying against that
// plan instead of failing. Returns the hauled item id, the method whose plan
// actually completed (which differs from the one passed in whenever a
// renewal occurred), and how many incidental renewals were absorbed so
// callers can adjust their own method-count bookkeeping.
func waitHaulItem(ctx context.Context, s *store.Store, w na.Wait, goalID domain.GoalID, method domain.GoalMethod) (item string, final domain.GoalMethod, renewals int, err error) {
	for {
		item, incidental, err := waitHaulCompleted(ctx, s, w, method.Plan)
		if err != nil {
			return "", method, renewals, err
		}
		if !incidental {
			return item, method, renewals, nil
		}
		renewals++
		_, next, err := waitHaulMethod(ctx, s, w, goalID, &method)
		if err != nil {
			return "", method, renewals, fmt.Errorf("waiting for renewed haul method after incidental interruption #%d: %w", renewals, err)
		}
		method = next
	}
}

func sha256File(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}
