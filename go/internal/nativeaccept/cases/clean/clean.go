// Package clean holds MaintainCleanFacilities' bounded cleaning response
// and the kitchen/butcher separation rule (issue #6 slice 2): a live game
// and a live rimgovernor Go player-control service, one case per scenario:
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
// (CleanlinessFixture.cs). The case's own bridge session and the
// service's are used sequentially, never concurrently.
package clean

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const prefix = "clean-accept"

func init() {
	// Passing runs hold a journal signature up to 39s (filthy) and 124s
	// (separation) while the routine cleans or rebuilds (#353).
	for scenario, stall := range map[string]time.Duration{"filthy": 90 * time.Second, "separation": 3 * time.Minute} {
		scenario := scenario
		cases.Register(cases.Case{
			Name: "clean/" + scenario,
			Scope: "Native MaintainCleanFacilities vertical (" + scenario + "): blood filth in a kitchen with no cleaners " +
				"drives the live Go routine reviewer/planner to latch the kitchen alone and order its filth cleaned one " +
				"target at a time until the measured cleanliness releases the latch (filthy), or a co-located butcher spot " +
				"has the food-supply family admit a separated ButcherSpot that takes the bill (separation); " +
				"confirmed by an independent native read.",
			Start:   cases.Fixture{Op: "test/cleanliness_prepare", Args: map[string]any{"scenario": scenario, "filthPerRoom": 3}},
			Service: true,
			Budget:  5 * time.Minute,
			Stall:   stall,
			Run:     func(ctx context.Context, s cases.Session) error { return run(ctx, s, scenario) },
		})
	}
}

func run(ctx context.Context, s cases.Session, scenario string) error {
	report := s.Report()
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	var service *na.ServiceProcess
	// Failure evidence: the same independent room and filth reads a pass
	// ends with, and the emergency reviewer's own threat census, so an
	// unsafe_colony hold names the pawns behind it.
	defer func() {
		if _, hasAfter := report["filth_after"]; hasAfter {
			return
		}
		if service != nil {
			service.Stop()
		}
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 90*time.Second)
		defer stopCancel()
		ph, err := s.Reattach(stopCtx)
		if err != nil {
			report["postmortem_error"] = "reopen session for the postmortem: " + err.Error()
			return
		}
		if rooms, err := readRooms(stopCtx, ph, identity, "rooms-postmortem"); err == nil {
			report["rooms_postmortem"] = rooms
		} else {
			report["rooms_postmortem_error"] = err.Error()
		}
		if filth, err := readFilth(stopCtx, ph, identity, "filth-postmortem"); err == nil {
			report["filth_postmortem"] = filth
		} else {
			report["filth_postmortem_error"] = err.Error()
		}
		if reply, err := ph.Wire(stopCtx, "threats-postmortem", "observations_read_status", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "colonists": false, "threats": true, "colonistDetail": false, "page": map[string]any{"limit": 256},
		}); err == nil {
			if _, observed, err := na.Outcome(reply, "observed"); err == nil {
				report["threats_postmortem"] = observed["threats"]
			}
		}
	}()
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
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

	families := []string{"clean"}
	if scenario == "separation" {
		// "work" rides along because every building method's builder check
		// requires the colony's work priorities to match the controller's own
		// assignment, which only the work family applies.
		families = []string{"bill", "work"}
	}
	service, err = s.Launch(ctx, na.ServiceLaunch{Families: families, Extra: na.ClockSpeedArgs()})
	if err != nil {
		return err
	}
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
	return checkStartupLog(s)
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

// checkStartupLog is the run's last assertion: no native error in the
// game's startup log.
func checkStartupLog(s cases.Session) error {
	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
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
