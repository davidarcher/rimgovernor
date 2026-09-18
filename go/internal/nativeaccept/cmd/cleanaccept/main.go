// Command cleanaccept exercises MaintainCleanFacilities' bounded cleaning
// response and the kitchen/butcher separation rule (issue #6 slice 2) end to
// end against a live game and a live rimgovernor Go player-control service:
//
//	filthy     -- a kitchen and a butchery each hold blood filth, and every
//	              colonist has Cleaning at priority 0, so ordinary coverage
//	              has failed outright. The review must latch only the kitchen
//	              dirty (the butchery is inherently dirty), respond without
//	              waiting out the coverage grace period (no cleaners exist to
//	              wait for), then order the kitchen's filth cleaned one
//	              player-forced target at a time until the measured room
//	              cleanliness releases the latch. The butchery's filth must
//	              survive untouched. (The grace path itself is unit-tested:
//	              a rested pawn works through Sleep and Joy slots, so no
//	              fixture can hold coverage back for 30000 ticks.)
//	separation -- one kitchen holds both a stove and a butcher spot, the
//	              colony has no food and an armed colonist. The food-supply
//	              family must admit a fresh ButcherSpot outside the kitchen
//	              instead of counting the co-located one, and the butcher
//	              bill must land on the separated spot.
//
// Uses the private disposable test/cleanliness_prepare fixture
// (CleanlinessFixture.cs). The harness's own bridge session and the
// service's are used sequentially, never concurrently.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const prefix = "clean-accept"

func main() {
	root := flag.String("root", "", "absolute disposable worker root (e.g. .rimgovernor/bridge)")
	output := flag.String("output", "", "fresh output directory (default <root>/native-clean-acceptance-<scenario>)")
	rendered := flag.Bool("rendered", false, "use the windowed profile instead of headless")
	game := flag.String("game", "rimgovernor-trial", "configured game ID")
	binary := flag.String("rimgovernor", "", "absolute path to a prebuilt rimgovernor binary (go build ./go/cmd/rimgovernor)")
	scenario := flag.String("scenario", "filthy", "filthy or separation")
	timeout := flag.Duration("timeout", 30*time.Minute, "overall run timeout")
	na.BudgetFlag((30 * time.Minute) / 2)
	flag.Parse()
	if *root == "" || *binary == "" || !filepath.IsAbs(*binary) {
		fmt.Fprintln(os.Stderr, "-root and an absolute -rimgovernor are required")
		os.Exit(2)
	}
	if *scenario != "filthy" && *scenario != "separation" {
		fmt.Fprintln(os.Stderr, "-scenario must be filthy or separation")
		os.Exit(2)
	}
	if *output == "" {
		*output = *root + "/native-clean-acceptance-" + *scenario
	}
	if err := os.MkdirAll(*output, 0755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	if entries, _ := os.ReadDir(*output); len(entries) > 0 {
		fmt.Fprintln(os.Stderr, "-output must be a fresh, empty directory")
		os.Exit(2)
	}
	report := na.NewReport("Native MaintainCleanFacilities vertical ("+*scenario+"): measured room cleanliness latches only "+
		"a dirty workspace, direct clean orders respond at once when no cleaner exists and never touch an inherently dirty "+
		"room; butcher placement and bills keep butchery out of the cooking room; confirmed by independent native reads.", !*rendered)
	ctx, cancel := context.WithTimeout(context.Background(), *timeout)
	defer cancel()
	err := run(ctx, *root, *output, *game, !*rendered, *binary, *scenario, report)
	if err != nil {
		report["error"] = err.Error()
	} else {
		report["passed"] = true
	}
	os.Exit(report.Finalize(*output))
}

func run(ctx context.Context, root, output, gameID string, headless bool, binary, scenario string, report na.Report) error {
	if abs, err := filepath.Abs(root); err == nil {
		root = abs
	}
	if abs, err := filepath.Abs(output); err == nil {
		output = abs
	}
	cfg := &na.Config{Root: root, Output: output, Headless: headless, GameID: gameID}
	var service *na.ServiceProcess
	var s *na.Session
	var postmortem map[string]any
	stopped := false
	stopGame := func() {
		if stopped {
			return
		}
		stopped = true
		if service != nil {
			service.Stop()
		}
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 90*time.Second)
		defer stopCancel()
		ph, err := s.Reattach(stopCtx)
		if err != nil {
			report["stop_error"] = "reopen session for games_stop: " + err.Error()
			return
		}
		if postmortem != nil {
			if rooms, err := readRooms(stopCtx, ph, postmortem, "rooms-postmortem"); err == nil {
				report["rooms_postmortem"] = rooms
			} else {
				report["rooms_postmortem_error"] = err.Error()
			}
			if filth, err := readFilth(stopCtx, ph, postmortem, "filth-postmortem"); err == nil {
				report["filth_postmortem"] = filth
			} else {
				report["filth_postmortem_error"] = err.Error()
			}
			if reply, err := ph.Wire(stopCtx, "threats-postmortem", "observations_read_status", map[string]any{
				"scope": map[string]any{"expectedIdentity": postmortem}, "colonists": false, "threats": true, "colonistDetail": false, "page": map[string]any{"limit": 256},
			}); err == nil {
				if _, observed, err := na.Outcome(reply, "observed"); err == nil {
					report["threats_postmortem"] = observed["threats"]
				}
			}
		}
		s.Close()
	}

	s, err := na.OpenSession(ctx, cfg, report, na.Fixture{Op: "test/cleanliness_prepare", Args: map[string]any{"scenario": scenario, "filthPerRoom": 3}}, na.QuietRequired)
	if err != nil {
		return err
	}
	defer stopGame()
	h, identity, prepared := s.Harness, s.Identity, s.Prepared
	postmortem = identity
	if err := confirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	// The service's own routine read is the typed colony facts with planning
	// definitions; record its section sizes so a native 1MiB refusal on an
	// unlucky map is diagnosable from the report.
	if reply, err := h.Wire(ctx, "colony-facts-typed", "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": true, "page": map[string]any{"limit": 256},
	}); err == nil {
		if _, observed, err := na.Outcome(reply, "observed"); err == nil {
			sizes := map[string]int{}
			for key, value := range observed {
				if encoded, err := json.Marshal(value); err == nil {
					sizes[key] = len(encoded)
				}
			}
			report["typed_colony_facts_sizes"] = sizes
		} else {
			report["typed_colony_facts_error"] = err.Error()
		}
	} else {
		report["typed_colony_facts_error"] = err.Error()
	}
	// Rooms are identified by their latch key (lowest interior cell), the
	// same identity the review latches on: native room IDs are renumbered
	// by every region rebuild. A room's interior starts one cell inside
	// its wall rectangle.
	kitchenRect, _ := na.AsMap(prepared["kitchen"])
	butcheryRect, _ := na.AsMap(prepared["butchery"])
	kitchenID := fmt.Sprintf("%d,%d", int(na.AsNumber(kitchenRect["minX"]))+1, int(na.AsNumber(kitchenRect["minZ"]))+1)
	butcheryID := ""
	if butcheryRect != nil {
		butcheryID = fmt.Sprintf("%d,%d", int(na.AsNumber(butcheryRect["minX"]))+1, int(na.AsNumber(butcheryRect["minZ"]))+1)
	}
	spare, _ := na.AsMap(prepared["spareCell"])
	kitchenFilth := map[string]bool{}
	for _, raw := range na.AsSlice(prepared["kitchenFilth"]) {
		kitchenFilth[na.AsString(raw)] = true
	}
	butcheryFilth := map[string]bool{}
	for _, raw := range na.AsSlice(prepared["butcheryFilth"]) {
		butcheryFilth[na.AsString(raw)] = true
	}
	inKitchen := func(c domain.Cell) bool {
		return float64(c.X) >= na.AsNumber(kitchenRect["minX"]) && float64(c.X) <= na.AsNumber(kitchenRect["maxX"]) &&
			float64(c.Z) >= na.AsNumber(kitchenRect["minZ"]) && float64(c.Z) <= na.AsNumber(kitchenRect["maxZ"])
	}

	// Before: the typed reads must show what the review will see -- both
	// rooms with a measured Cleanliness stat under the entry threshold, and
	// every fixture filth carrying its room identity.
	roomsBefore, err := readRooms(ctx, h, identity, "rooms-before")
	if err != nil {
		return err
	}
	report["rooms_before"] = roomsBefore
	cleanliness := policy.DefaultCleanlinessPolicy()
	if kitchen, ok := roomsBefore[kitchenID]; !ok || kitchen.cleanliness > cleanliness.EnterC && scenario == "filthy" {
		return fmt.Errorf("rooms-before: kitchen %s missing or not dirty enough: %+v", kitchenID, roomsBefore[kitchenID])
	}
	filthBefore, err := readFilth(ctx, h, identity, "filth-before")
	if err != nil {
		return err
	}
	report["filth_before"] = filthBefore
	if scenario == "filthy" {
		if butchery, ok := roomsBefore[butcheryID]; !ok || butchery.cleanliness > cleanliness.EnterC {
			return fmt.Errorf("rooms-before: butchery %s missing or not dirty: %+v", butcheryID, roomsBefore[butcheryID])
		}
		for id := range kitchenFilth {
			if row, ok := filthBefore[id]; !ok || roomsBefore.key(row.room) != kitchenID || !row.home {
				return fmt.Errorf("filth-before: kitchen filth %s not observed at home in room %s: %+v", id, kitchenID, filthBefore[id])
			}
		}
		for id := range butcheryFilth {
			if row, ok := filthBefore[id]; !ok || roomsBefore.key(row.room) != butcheryID {
				return fmt.Errorf("filth-before: butchery filth %s not observed in room %s: %+v", id, butcheryID, filthBefore[id])
			}
		}
	}
	if err := s.Release(); err != nil {
		return fmt.Errorf("close fixture-prep bridge session: %w", err)
	}

	families := []string{"clean"}
	if scenario == "separation" {
		// "work" rides along because every building method's builder check
		// requires the colony's work priorities to match the controller's own
		// assignment, which only the work family applies.
		families = []string{"bill", "work"}
	}
	service, err = na.LaunchService(ctx, cfg, s.GABS, na.ServiceLaunch{Binary: binary, Families: families, Extra: na.ClockSpeedArgs()}, report)
	if err != nil {
		return err
	}
	defer service.Stop()
	token, err := service.SessionToken()
	if err != nil {
		return err
	}
	attached, err := service.WaitAttached(identity, 90*time.Second)
	if err != nil {
		return err
	}
	report["service_state_attached"] = attached
	rootPlanID, err := service.SubmitAndResume(prefix, identity, map[string]any{
		"defName": "Wall", "x": int(na.AsNumber(spare["x"])), "z": int(na.AsNumber(spare["z"])), "rotation": "north", "stuff": "WoodLog",
	}, token, report)
	if err != nil {
		return err
	}
	report["root_plan"] = rootPlanID
	keepAlive := &na.AuthorityKeepAlive{Service: service, Prefix: prefix, Identity: identity, Token: token}
	stopKeepAlive := keepAlive.Start(ctx)
	defer func() { report["authority_reacquisitions"] = stopKeepAlive() }()

	journal, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		return err
	}
	defer journal.Close()
	review, diagnostics, err := service.WaitRoutineReview(ctx, journal, 90*time.Second)
	report["diagnostic_post_acquire"] = diagnostics
	if err != nil {
		return err
	}
	reviewData, _ := json.Marshal(review)
	report["routine_review_first"] = json.RawMessage(reviewData)

	if scenario == "filthy" {
		err = runFilthy(ctx, journal, service, report, kitchenID, butcheryID, kitchenFilth, inKitchen, cleanliness)
	} else {
		err = runSeparation(ctx, journal, service, report, inKitchen)
	}
	if err != nil {
		return err
	}
	if err := na.AssertRoutineRunning(service.Get); err != nil {
		return err
	}

	// Independent native read after the service releases the game slot.
	journal.Close()
	service.Stop()
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen harness session after service stop: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	roomsAfter, err := readRooms(ctx, h, identity, "rooms-after")
	if err != nil {
		return err
	}
	report["rooms_after"] = roomsAfter
	filthAfter, err := readFilth(ctx, h, identity, "filth-after")
	if err != nil {
		return err
	}
	report["filth_after"] = filthAfter
	if scenario == "filthy" {
		kitchen, ok := roomsAfter[kitchenID]
		if !ok || kitchen.cleanliness < cleanliness.ExitC {
			return fmt.Errorf("rooms-after: kitchen %s cleanliness %.2f is still under the exit threshold %.2f", kitchenID, kitchen.cleanliness, cleanliness.ExitC)
		}
		for id := range kitchenFilth {
			if _, present := filthAfter[id]; present {
				return fmt.Errorf("filth-after: kitchen filth %s survived", id)
			}
		}
		for id := range butcheryFilth {
			if _, present := filthAfter[id]; !present {
				return fmt.Errorf("filth-after: butchery filth %s was cleaned; inherently dirty rooms are never a target", id)
			}
		}
		if butchery, ok := roomsAfter[butcheryID]; !ok || butchery.cleanliness > cleanliness.EnterC {
			return fmt.Errorf("rooms-after: butchery %s is no longer dirty: %+v", butcheryID, roomsAfter[butcheryID])
		}
	} else {
		benches, err := readButcherBenches(ctx, h, identity)
		if err != nil {
			return err
		}
		report["butcher_benches_after"] = benches
		var separated []string
		for _, b := range benches {
			if roomsAfter.key(b.room) != kitchenID {
				separated = append(separated, b.id)
			}
		}
		if len(separated) != 1 {
			return fmt.Errorf("expected exactly one butcher bench outside kitchen %s, observed %d: %+v", kitchenID, len(separated), benches)
		}
		if report["butcher_bill_bench"] != separated[0] {
			return fmt.Errorf("the butcher bill landed on %v, not the separated bench %s", report["butcher_bill_bench"], separated[0])
		}
		for _, b := range benches {
			if b.id == separated[0] && b.bills == 0 {
				return fmt.Errorf("separated bench %s holds no bill natively: %+v", b.id, benches)
			}
			if b.id != separated[0] && b.bills != 0 {
				return fmt.Errorf("co-located bench %s holds %d bills; butchery must leave the kitchen: %+v", b.id, b.bills, benches)
			}
		}
	}
	logData, err := os.ReadFile(cfg.StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), headless)
}

// runFilthy follows the review's dirty-room latch and the clean methods it
// admits: the kitchen alone latches, the first order lands inside the grace
// period (zero cleaners means there is no coverage to wait for), every order
// targets kitchen filth, and the latch releases once the room's measured
// cleanliness recovers.
func runFilthy(ctx context.Context, journal *store.Store, service *na.ServiceProcess, report na.Report, kitchenID, butcheryID string, kitchenFilth map[string]bool, inKitchen func(domain.Cell) bool, p policy.CleanlinessPolicy) error {
	latchCtx, latchCancel := context.WithTimeout(ctx, 4*time.Minute)
	latched, since, err := waitDirtyRoom(latchCtx, journal, service, kitchenID)
	latchCancel()
	if err != nil {
		return fmt.Errorf("kitchen latch: %w", err)
	}
	for _, room := range latched.Latches.Upkeep.DirtyRooms {
		if room.Key == butcheryID {
			return fmt.Errorf("the butchery %s latched dirty; a room holding a butcher bench is inherently dirty: %+v", butcheryID, latched.Latches.Upkeep.DirtyRooms)
		}
	}
	report["latched_review_revision"] = latched.Revision
	report["latched_since_tick"] = int64(since)
	report["latched_at_tick"] = int64(latched.Tick)

	// No cleaners: the first method must arrive well inside Since+GraceTicks
	// rather than after it. Poll the journal until the first method.
	methodCtx, methodCancel := context.WithTimeout(ctx, 12*time.Minute)
	defer methodCancel()
	goalID, method, err := na.WaitGoalMethod(methodCtx, journal, policy.MaintainCleanFacilities, nil)
	if err != nil {
		return fmt.Errorf("first clean method: %w", err)
	}
	current, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return err
	}
	// The method was committed under a review at or before `current`; the
	// plan's first progress tick (its dispatch) bounds the admitting review's
	// tick from above, so a dispatch inside the grace window proves the
	// zero-cleaner branch fired rather than the grace timer.
	plan, err := journal.LoadPlan(ctx, method.Plan)
	if err != nil {
		return err
	}
	dispatchTick := domain.Tick(-1)
	for _, progress := range plan.Progress {
		if t := progress.View().Tick; dispatchTick < 0 || t < dispatchTick {
			dispatchTick = t
		}
	}
	report["first_clean_method"] = string(method.Method)
	report["first_clean_dispatch_tick"] = int64(dispatchTick)
	report["review_tick_at_first_method"] = int64(current.Tick)
	if dispatchTick < 0 || dispatchTick >= since+p.GraceTicks {
		return fmt.Errorf("clean order dispatched at tick %d, not inside the grace window ending at %d (latched %d + %d) although no cleaner existed", dispatchTick, since+p.GraceTicks, since, p.GraceTicks)
	}
	report["goal_id"] = string(goalID)
	orders := 0
	for renewals := 0; ; {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		actions := plan.Spec.Actions()
		if len(actions) != 1 {
			return fmt.Errorf("clean plan %s has %d actions, expected 1", method.Plan, len(actions))
		}
		clean, ok := actions[0].Clean()
		if !ok {
			return fmt.Errorf("clean plan action is not a clean order: %#v", actions[0])
		}
		// The fixture's blood, or dirt the cleaner tracked in while the
		// room stayed latched: either way the target must lie inside the
		// kitchen, never in the butchery or elsewhere.
		if !kitchenFilth[clean.Filth()] && !inKitchen(clean.Cell()) {
			return fmt.Errorf("clean order targets %s at %v, which is not kitchen filth", clean.Filth(), clean.Cell())
		}
		doneCtx, doneCancel := context.WithTimeout(ctx, 5*time.Minute)
		_, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return fmt.Errorf("clean plan %s: %w", method.Plan, err)
		}
		if incidental {
			renewals++
		} else {
			orders++
		}
		report["clean_orders_completed"] = orders
		report["incidental_renewals"] = renewals
		releaseCtx, releaseCancel := context.WithTimeout(ctx, 90*time.Second)
		released, releaseErr := waitRelease(releaseCtx, journal, service, kitchenID)
		releaseCancel()
		if releaseErr == nil {
			report["released_review_revision"] = released.Revision
			report["released_at_tick"] = int64(released.Tick)
			return nil
		}
		if orders+renewals > 12 {
			return fmt.Errorf("kitchen latch never released after %d clean orders", orders)
		}
		nextCtx, nextCancel := context.WithTimeout(ctx, 5*time.Minute)
		_, method, err = na.WaitGoalMethod(nextCtx, journal, policy.MaintainCleanFacilities, &method)
		nextCancel()
		if err != nil {
			return fmt.Errorf("next clean method after %d orders: %w", orders, err)
		}
	}
}

// runSeparation follows the food-supply family: a ButcherSpot build outside
// the kitchen rectangle, its completion, then a butcher bill on the new
// bench (recorded in report["butcher_bill_bench"] for the native check).
func runSeparation(ctx context.Context, journal *store.Store, service *na.ServiceProcess, report na.Report, inKitchen func(domain.Cell) bool) error {
	methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
	defer methodCancel()
	var previous *domain.GoalMethod
	var built domain.Cell
	for {
		goalID, method, err := na.WaitGoalMethod(methodCtx, journal, policy.EnsureFoodSupply, previous)
		if err != nil {
			return fmt.Errorf("butcher spot method: %w", err)
		}
		previous = &method
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		actions := plan.Spec.Actions()
		if len(actions) != 1 {
			continue
		}
		b, ok := actions[0].Building()
		if !ok || b.Definition() != "ButcherSpot" {
			continue
		}
		if method.Method != "butcher-spot-separated" {
			return fmt.Errorf("butcher spot admitted under method %s, expected butcher-spot-separated", method.Method)
		}
		built = b.Cell()
		if inKitchen(built) {
			return fmt.Errorf("butcher spot placed at %v, inside the kitchen", built)
		}
		report["goal_id"] = string(goalID)
		report["butcher_spot_cell"] = map[string]any{"x": built.X, "z": built.Z}
		doneCtx, doneCancel := context.WithTimeout(ctx, 8*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return fmt.Errorf("butcher spot plan: %w", err)
		}
		if incidental {
			continue
		}
		report["butcher_spot_plan"] = string(method.Plan)
		report["butcher_spot_completed_tick"] = int64(state.Progress[0].View().Tick)
		break
	}
	billCtx, billCancel := context.WithTimeout(ctx, 6*time.Minute)
	defer billCancel()
	for {
		_, method, err := na.WaitGoalMethod(billCtx, journal, policy.EnsureFoodSupply, previous)
		if err != nil {
			return fmt.Errorf("butcher bill method: %w", err)
		}
		previous = &method
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		for _, action := range plan.Spec.Actions() {
			bill, ok := action.ProductionBill()
			if !ok || bill.Mode() != domain.ButcherForever {
				continue
			}
			report["butcher_bill_bench"] = bill.Bench()
			report["butcher_bill_plan"] = string(method.Plan)
			// A forever butcher bill stays pending until a corpse is
			// processed, which the fixture never supplies: the accepted
			// receipt plus the native bench read below are the evidence.
			doneCtx, doneCancel := context.WithTimeout(ctx, 3*time.Minute)
			err := waitAcceptedReceipt(doneCtx, journal, service, method.Plan)
			doneCancel()
			if err != nil {
				return fmt.Errorf("butcher bill plan: %w", err)
			}
			return nil
		}
	}
}

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

type roomRow struct {
	id, role    string
	cleanliness float64
	measured    bool
}

func (r roomRow) MarshalJSON() ([]byte, error) {
	return json.Marshal(map[string]any{"id": r.id, "role": r.role, "cleanliness": r.cleanliness, "measured": r.measured})
}

// roomCensus is keyed by latch key (policy.RoomLatchKey); key maps a native
// room ID from the same census back to it ("" for outdoors or unknown).
type roomCensus map[string]roomRow

func (c roomCensus) key(id string) string {
	for key, row := range c {
		if row.id == id {
			return key
		}
	}
	return ""
}

// readRooms decodes the typed room census the way the Go projection does:
// each proper room's role, cells and Cleanliness stat when measured.
func readRooms(ctx context.Context, h *na.Harness, identity map[string]any, label string) (roomCensus, error) {
	reply, err := h.Wire(ctx, label, "observations_list_rooms", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "includeOutdoors": false, "includeBoundary": false, "includeCells": true, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, err
	}
	rooms := roomCensus{}
	for _, raw := range na.AsSlice(observed["rooms"]) {
		row, _ := na.AsMap(raw)
		r := roomRow{id: na.AsString(row["id"]), role: na.AsString(row["role"])}
		room := policy.Room{ID: r.id}
		for _, rawCell := range na.AsSlice(row["cells"]) {
			cell, _ := na.AsMap(rawCell)
			room.Cells = append(room.Cells, domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))})
		}
		for _, rawStat := range na.AsSlice(row["stats"]) {
			stat, _ := na.AsMap(rawStat)
			if na.AsString(stat["defName"]) != "Cleanliness" {
				continue
			}
			if _, unavailable := stat["unavailable"]; unavailable {
				continue
			}
			if _, present := stat["value"]; present {
				r.cleanliness, r.measured = na.AsNumber(stat["value"]), true
			}
		}
		rooms[policy.RoomLatchKey(room)] = r
	}
	return rooms, nil
}

type filthRow struct {
	room string
	home bool
}

func (f filthRow) MarshalJSON() ([]byte, error) {
	return json.Marshal(map[string]any{"room": f.room, "home": f.home})
}

// readFilth lists the typed upkeep filth census by filth id with each row's
// room identity, the field the cleaning review keys its targets on.
func readFilth(ctx context.Context, h *na.Harness, identity map[string]any, label string) (map[string]filthRow, error) {
	observed, err := readColonyFacts(ctx, h, identity, label)
	if err != nil {
		return nil, err
	}
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return nil, fmt.Errorf("%s: upkeep facts unavailable: %w", label, err)
	}
	rows := map[string]filthRow{}
	for _, raw := range na.AsSlice(upkeep["filth"]) {
		row, _ := na.AsMap(raw)
		filth, _ := na.AsMap(row["filth"])
		home, _ := na.AsBool(row["home"])
		rows[na.AsString(filth["id"])] = filthRow{room: na.AsString(row["roomId"]), home: home}
	}
	return rows, nil
}

type benchRow struct {
	id, room string
	bills    int
}

func (b benchRow) MarshalJSON() ([]byte, error) {
	return json.Marshal(map[string]any{"id": b.id, "room": b.room, "bills": b.bills})
}

func readButcherBenches(ctx context.Context, h *na.Harness, identity map[string]any) ([]benchRow, error) {
	observed, err := readColonyFacts(ctx, h, identity, "butchering-after")
	if err != nil {
		return nil, err
	}
	var rows []benchRow
	for _, raw := range na.AsSlice(observed["butchering"]) {
		row, _ := na.AsMap(raw)
		bench, _ := na.AsMap(row["bench"])
		rows = append(rows, benchRow{id: na.AsString(bench["id"]), room: na.AsString(row["roomId"]), bills: len(na.AsSlice(row["bills"]))})
	}
	return rows, nil
}

func readColonyFacts(ctx context.Context, h *na.Harness, identity map[string]any, label string) (map[string]any, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	return observed, err
}

// storeWait bounds the journal polls below: the shared stall budget, and
// the service exiting on its own ends a wait at once.
func storeWait(service *na.ServiceProcess) na.Wait {
	return na.Wait{Stall: na.StallBudget(), Terminal: service.Exited}
}

// waitDirtyRoom polls until the review's upkeep latch lists room, returning
// the review and the latch's entry tick.
func waitDirtyRoom(ctx context.Context, s *store.Store, service *na.ServiceProcess, room string) (store.RoutineReview, domain.Tick, error) {
	var since domain.Tick
	review, err := na.WaitReview(ctx, s, storeWait(service), func(r store.RoutineReview) bool {
		for _, dirty := range r.Latches.Upkeep.DirtyRooms {
			if dirty.Key == room {
				since = dirty.Since
				return true
			}
		}
		return false
	})
	if err != nil {
		return review, 0, fmt.Errorf("review never latched room %s dirty (revision %d, latches %+v): %w", room, review.Revision, review.Latches.Upkeep.DirtyRooms, err)
	}
	return review, since, nil
}

func waitRelease(ctx context.Context, s *store.Store, service *na.ServiceProcess, room string) (store.RoutineReview, error) {
	review, err := na.WaitReview(ctx, s, storeWait(service), func(r store.RoutineReview) bool {
		for _, dirty := range r.Latches.Upkeep.DirtyRooms {
			if dirty.Key == room {
				return false
			}
		}
		return true
	})
	if err != nil {
		return review, fmt.Errorf("room %s still latched (revision %d): %w", room, review.Revision, err)
	}
	return review, nil
}

// waitAcceptedReceipt polls until every action of plan has an accepted
// receipt (or has completed); an unsuccessful or cancelled action fails.
func waitAcceptedReceipt(ctx context.Context, s *store.Store, service *na.ServiceProcess, planID domain.PlanID) error {
	_, err := na.WaitPlan(ctx, s, storeWait(service), planID, func(state store.PlanState) (string, bool, error) {
		accepted := len(state.Progress) > 0
		for _, progress := range state.Progress {
			view := progress.View()
			switch view.Stage {
			case domain.Unsuccessful, domain.Cancelled:
				return "", false, fmt.Errorf("plan %s reached %s before its receipt", planID, view.Stage)
			case domain.Completed:
			default:
				receipt, known := view.Receipt.Value()
				accepted = accepted && known && receipt == domain.ReceiptAccepted
			}
		}
		return na.PlanSignature(state), accepted, nil
	})
	return err
}
