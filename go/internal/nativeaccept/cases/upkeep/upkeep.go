// Package upkeep holds issue #2's (B04h) colony-upkeep acceptance. The
// single-deficit cases (scattered, storage-missing, blocked, fire,
// medicine, feed, feed-delivered, sleeping, cold, stone-shell) replay as
// colony snapshots in internal/snapshot since #746; their scenarios here
// (medicine, feed, sleeping, cold) stage deficits for the cases that
// chain them:
//
//	campaign     -- issue #99: kitchen (test/cleanliness_prepare), feed,
//	                medicine and cold chained on one colony and one journal
//	                with a service restart between them; every goal
//	                recovered earlier must stay closed (campaign.go).
//	home-coverage, colony-extent -- the sleeping fixture's rooms.
//	takeover -- the feed fixture's pet.
//
// Every case opens on the tribal8 baseline save (the fixture stages its
// deficit on the loaded map) so a kept process serves the whole family.
// Needs the native mod built with -Fixture
// UpkeepFixture,ForecastFixture,RoutineSleepingFixture (the campaign adds
// CleanlinessFixture).
package upkeep

import (
	"context"
	"errors"
	"fmt"
	"sort"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const prefix = "upkeep"

// scenario stages one deficit, names the families that own it, follows the
// journal to recovery and audits the native outcome.
type scenario struct {
	name     string
	fixture  string
	families []string
	extra    []string
	// keep are the colonist needs the deficit's recovery depends on.
	keep    []string
	prepare func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error)
	watch   func(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error
	verify  func(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error
}

func scenarios() map[string]*scenario {
	s := map[string]*scenario{}

	// Every medicine stack destroyed and forty mature healroot within
	// fifteen cells: eight harvests, all admitted on one stop, recover the
	// reserve in about a minute of wall time (#129).
	s["medicine"] = &scenario{name: "medicine", fixture: "test/medicine_setup",
		families: []string{"medical", "resource", "bill", "acquisition", "work"},
		prepare: func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
			return callFixture(ctx, h, identity, "test/medicine_setup", map[string]any{})
		},
		watch:  watchMedicine,
		verify: verifyMedicine,
	}
	s["feed"] = &scenario{name: "feed", fixture: "test/feed_setup",
		families: []string{"animal-feed", "resource", "bill", "work"},
		prepare: func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
			return callFixture(ctx, h, identity, "test/feed_setup", map[string]any{})
		},
		watch:  watchFeed,
		verify: verifyFeed,
	}
	s["sleeping"] = &scenario{name: "sleeping", fixture: "test/sleeping_setup",
		families: []string{"sleeping", "work"},
		prepare: func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
			prepared, err := callFixture(ctx, h, identity, "test/sleeping_setup", map[string]any{})
			if err != nil {
				return nil, err
			}
			report["fixture_owned_beds"] = len(na.AsSlice(prepared["ownedBeds"]))
			report["fixture_room_temperature_c"] = prepared["roomTemperatureC"]
			return prepared, nil
		},
		watch:  watchSleeping,
		verify: verifySleeping,
		// Recovery is every colonist observed asleep in an owned bed.
		keep: []string{string(na.NeedRest)},
	}
	s["cold"] = &scenario{name: "cold", fixture: "test/routine_temperature_prepare",
		families: []string{"temperature", "work"},
		prepare:  prepareCold,
		watch:    watchCold,
		verify:   verifyCold,
	}
	return s
}

func callFixture(ctx context.Context, h *na.Harness, identity map[string]any, tool string, args map[string]any) (map[string]any, error) {
	prepared, err := h.Call(ctx, "prepare", tool, args)
	if err != nil {
		return nil, err
	}
	if success, _ := na.AsBool(prepared["success"]); !success {
		return nil, fmt.Errorf("%s refused: %#v", tool, prepared)
	}
	return prepared, nil
}

type itemRow struct {
	Definition string `json:"definition"`
	X          int    `json:"x"`
	Z          int    `json:"z"`
	Count      int64  `json:"count"`
	Roofed     bool   `json:"roofed"`
	InStorage  bool   `json:"inStorage"`
	Forbidden  bool   `json:"forbidden"`
	Medicine   bool   `json:"medicine"`
}

type structureRow struct {
	Definition string `json:"definition"`
	Home       bool   `json:"home"`
	HitPoints  int64  `json:"hitPoints"`
	Max        int64  `json:"maxHitPoints"`
}

type fireRow struct {
	Home bool    `json:"home"`
	Size float64 `json:"size"`
}

type upkeepCensus struct {
	Tick       int64                   `json:"tick"`
	Items      map[string]itemRow      `json:"items"`
	Structures map[string]structureRow `json:"structures"`
	Fires      map[string]fireRow      `json:"fires"`
	Filth      int                     `json:"filth"`
}

func readColonyFacts(ctx context.Context, h *na.Harness, identity map[string]any, label string) (map[string]any, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false,
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	return observed, err
}

// readUpkeep decodes the typed upkeep census the review reads: loose items
// with their storage state, damageable structures, fires and filth.
func readUpkeep(ctx context.Context, h *na.Harness, identity map[string]any, label string) (upkeepCensus, error) {
	c := upkeepCensus{Items: map[string]itemRow{}, Structures: map[string]structureRow{}, Fires: map[string]fireRow{}}
	observed, err := readColonyFacts(ctx, h, identity, label)
	if err != nil {
		return c, err
	}
	context, _ := na.AsMap(observed["context"])
	c.Tick = int64(na.AsNumber(context["tick"]))
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return c, fmt.Errorf("%s: upkeep facts unavailable: %w", label, err)
	}
	for _, raw := range na.AsSlice(upkeep["items"]) {
		row, _ := na.AsMap(raw)
		item, _ := na.AsMap(row["item"])
		position, _ := na.AsMap(item["position"])
		roofed, _ := na.AsBool(row["roofed"])
		inStorage, _ := na.AsBool(row["inStorage"])
		forbidden, _ := na.AsBool(row["forbidden"])
		medicine, _ := na.AsBool(row["medicine"])
		c.Items[na.AsString(item["id"])] = itemRow{Definition: na.AsString(item["defName"]), X: int(na.AsNumber(position["x"])), Z: int(na.AsNumber(position["z"])),
			Count: int64(na.AsNumber(row["count"])), Roofed: roofed, InStorage: inStorage, Forbidden: forbidden, Medicine: medicine}
	}
	rows, err := h.BuildingRows(ctx, label+"-buildings", identity)
	if err != nil {
		return c, err
	}
	for _, raw := range na.AsSlice(upkeep["structures"]) {
		row, _ := na.AsMap(raw)
		building, _ := na.AsMap(row["building"])
		state, ok := rows[na.AsString(building["id"])]
		if !ok {
			return c, fmt.Errorf("%s: structure %v is not in the building table", label, building["id"])
		}
		head, _ := na.AsMap(state["building"])
		home, _ := na.AsBool(row["home"])
		c.Structures[na.AsString(building["id"])] = structureRow{Definition: na.AsString(head["defName"]), Home: home,
			HitPoints: int64(na.AsNumber(state["hitPoints"])), Max: int64(na.AsNumber(state["maxHitPoints"]))}
	}
	for _, raw := range na.AsSlice(upkeep["fires"]) {
		row, _ := na.AsMap(raw)
		fire, _ := na.AsMap(row["fire"])
		home, _ := na.AsBool(row["home"])
		c.Fires[na.AsString(fire["id"])] = fireRow{Home: home, Size: na.AsNumber(row["size"])}
	}
	c.Filth = len(na.AsSlice(upkeep["filth"]))
	return c, nil
}

// ---- journal helpers -----------------------------------------------------

// waitNeed polls until need's goal binding reports state (deficit or
// recovered) and returns the goal.
func waitNeed(ctx context.Context, journal *store.Store, need policy.ConcernID, state domain.NeedState) (store.GoalState, error) {
	var found store.GoalState
	err := na.WaitProgress(ctx, na.Wait{Stall: needStall, Interval: time.Second}, func(ctx context.Context) (string, bool, error) {
		review, err := journal.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		for _, binding := range review.Goals {
			if binding.Need != need {
				continue
			}
			goal, err := journal.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			if err == nil && goal.Goal.Need == state {
				found = goal
				return "", true, nil
			}
		}
		// A parked or dead game stops producing reviews at new ticks.
		return fmt.Sprintf("tick=%d revision=%d", review.Tick, review.Revision), false, nil
	})
	if err != nil {
		return store.GoalState{}, fmt.Errorf("%s never reported %s: %w", need, state, err)
	}
	return found, nil
}

// needStall ends waitNeed once the rounds stops advancing: the
// governor parked the game (no work) or it died, and the need cannot move.
const needStall = 3 * time.Minute

// followMethods waits for methods on need whose plan's single action
// satisfies accept, following the goal's method lineage across incidental
// (authority-discontinuity) cancellations until one plan completes. The
// controller's order supplements ordinary colonist work rather than replacing
// it: when the goal recovers on the native postcondition before the
// controller's plan completes (a colonist hauled or repaired it on their own
// priorities), the review invalidates the goal and cancels its never-dispatched
// plan; that counts as recovery too and is recorded as
// <label>_recovered_by=ordinary_work instead of <label>_completed_tick. Either
// way the scenario's verify step confirms the postcondition natively.
func followMethods(ctx context.Context, journal *store.Store, need policy.ConcernID, label string, accept func(domain.Action) error, report na.Report) (store.PlanState, error) {
	return followMethodsExcluding(ctx, journal, need, label, map[domain.PlanID]bool{}, accept, report)
}

// followMethodsExcluding is followMethods over a caller-owned seen set, so a
// goal whose deficit needs two successive methods (a bed built, then that
// bed assigned) is followed method by method without revisiting the first.
func followMethodsExcluding(ctx context.Context, journal *store.Store, need policy.ConcernID, label string, seen map[domain.PlanID]bool, accept func(domain.Action) error, report na.Report) (store.PlanState, error) {
	renewals, rejected := 0, 0
	recovered := func() (bool, error) {
		review, err := journal.LoadRounds(ctx)
		if err != nil {
			return false, err
		}
		for _, binding := range review.Goals {
			if binding.Need != need {
				continue
			}
			goal, err := journal.LoadGoal(ctx, binding.Goal)
			if err != nil {
				if errors.Is(err, store.ErrNotFound) {
					continue
				}
				return false, err
			}
			if goal.Goal.Need == domain.NeedRecovered {
				return true, nil
			}
		}
		return false, nil
	}
	deadline := time.Now().Add(10 * time.Minute)
	for {
		var goalID domain.ConcernID
		var method domain.GoalMethod
		for {
			if time.Now().After(deadline) {
				return store.PlanState{}, fmt.Errorf("%s method: no method within 10 minutes", label)
			}
			methodCtx, methodCancel := context.WithTimeout(ctx, 15*time.Second)
			var err error
			goalID, method, err = na.WaitGoalMethodExcluding(methodCtx, journal, need, seen)
			methodCancel()
			if err == nil {
				break
			}
			if ctx.Err() != nil {
				return store.PlanState{}, fmt.Errorf("%s method: %w", label, ctx.Err())
			}
			if done, rerr := recovered(); rerr != nil {
				return store.PlanState{}, rerr
			} else if done {
				report[label+"_recovered_by"] = "ordinary_work"
				return store.PlanState{}, nil
			}
		}
		seen[method.Plan] = true
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return store.PlanState{}, err
		}
		// A harvest acquisition designates one action per plant, so a plan
		// may carry several actions; every one must be acceptable.
		actions := plan.Spec.Actions()
		if len(actions) == 0 {
			return store.PlanState{}, fmt.Errorf("%s plan %s has no actions", label, method.Plan)
		}
		var rejectedErr error
		for _, action := range actions {
			if rejectedErr = accept(action); rejectedErr != nil {
				break
			}
		}
		if err := rejectedErr; err != nil {
			rejected++
			report[label+"_rejected_methods"] = rejected
			if rejected > 16 {
				return store.PlanState{}, fmt.Errorf("%s: %d methods without an acceptable action; last: %v", label, rejected, err)
			}
			continue
		}
		report[label+"_goal_id"] = string(goalID)
		report[label+"_method"] = string(method.Method)
		report[label+"_plan"] = string(method.Plan)
		// The plan is followed to a terminal stage, but a need that recovers
		// on its own while the plan is still undispatched ends the follow:
		// a recovered routine goal never authorizes that write again
		// (store.AuthorizeRoutinePlan), it merely keeps the plan for a
		// returning deficit, so no terminal stage is coming.
		state, incidental, undispatched, err := waitPlanOrRecovery(ctx, journal, method.Plan, recovered)
		if err != nil {
			if feedBillNeedsRecovery(need, state) {
				report[label+"_unsuccessful_bill_plan"] = string(method.Plan)
				return state, nil
			}
			return state, fmt.Errorf("%s plan %s: %w", label, method.Plan, err)
		}
		if undispatched {
			report[label+"_recovered_by"] = "ordinary_work"
			report[label+"_plan_undispatched"] = true
			return state, nil
		}
		if incidental {
			if done, rerr := recovered(); rerr != nil {
				return state, rerr
			} else if done {
				report[label+"_recovered_by"] = "ordinary_work"
				return state, nil
			}
			renewals++
			report[label+"_incidental_renewals"] = renewals
			if renewals > 12 {
				return state, fmt.Errorf("%s: %d incidental renewals without completion", label, renewals)
			}
			deadline = time.Now().Add(10 * time.Minute)
			continue
		}
		completed := int64(0)
		for _, progress := range state.Progress {
			completed = max(completed, int64(progress.View().Tick))
		}
		report[label+"_completed_tick"] = completed
		report[label+"_actions"] = len(actions)
		report[label+"_recovered_by"] = "controller_order"
		return state, nil
	}
}

// waitPlanOrRecovery waits up to ten minutes for plan to reach a terminal
// stage (na.WaitPlanTerminal), returning undispatched=true instead when the
// goal recovers while every action of the plan is still at attempt 0.
func waitPlanOrRecovery(ctx context.Context, journal *store.Store, planID domain.PlanID, recovered func() (bool, error)) (state store.PlanState, incidental, undispatched bool, err error) {
	doneCtx, doneCancel := context.WithTimeout(ctx, 10*time.Minute)
	defer doneCancel()
	type outcome struct {
		state      store.PlanState
		incidental bool
		err        error
	}
	done := make(chan outcome, 1)
	go func() {
		s, i, e := na.WaitPlanTerminal(doneCtx, journal, planID)
		done <- outcome{s, i, e}
	}()
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case o := <-done:
			return o.state, o.incidental, false, o.err
		case <-ticker.C:
			ok, rerr := recovered()
			if rerr != nil || !ok {
				continue
			}
			current, lerr := journal.LoadPlan(ctx, planID)
			if lerr != nil {
				continue
			}
			pending := len(current.Progress) > 0
			for _, progress := range current.Progress {
				v := progress.View()
				if v.Attempt != 0 || v.Stage != domain.Pending && v.Stage != domain.Prepared {
					pending = false
				}
			}
			if pending {
				doneCancel()
				<-done
				return current, false, true, nil
			}
		}
	}
}

// developmentRow returns the current review's development row for goal.
// ---- medicine ------------------------------------------------------------

func watchMedicine(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.MaintainMedicalReserves, domain.NeedDeficit); err != nil {
		return err
	}
	// The method is an ordinary resource acquisition of the native
	// herbal-medicine definition; follow it to completion, then require the
	// observed reserve rather than the receipt.
	if _, err := followMethods(ctx, journal, policy.MaintainMedicalReserves, "replenish", func(a domain.Action) error {
		switch a.Kind() {
		case domain.AcquisitionAction, domain.ProductionBillAction, domain.MineAcquisitionAction:
			return nil
		}
		return fmt.Errorf("unexpected %s action", a.Kind())
	}, report); err != nil {
		return err
	}
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 12*time.Minute)
	defer recoverCancel()
	goal, err := waitNeed(recoverCtx, journal, policy.MaintainMedicalReserves, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["medicine_recovered_tick"] = int64(goal.Goal.Tick)
	return nil
}

func verifyMedicine(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	after := report["upkeep_after"].(upkeepCensus)
	units := int64(0)
	for _, item := range after.Items {
		if item.Medicine && !item.Forbidden {
			units += item.Count
		}
	}
	colonists, err := countColonists(ctx, h, identity)
	if err != nil {
		return err
	}
	reserve := policy.DefaultMedicalReservePolicy()
	report["medicine_units_after"] = units
	report["colonists"] = colonists
	if units < reserve.TargetPerColonist*int64(colonists) {
		return fmt.Errorf("%d medicine units for %d colonists is under the recovery reserve of %d each", units, colonists, reserve.TargetPerColonist)
	}
	return nil
}

func countColonists(ctx context.Context, h *na.Harness, identity map[string]any) (int, error) {
	observed, err := readColonyFacts(ctx, h, identity, "colonists-after")
	if err != nil {
		return 0, err
	}
	return int(na.AsNumber(observed["colonistCount"])), nil
}

// ---- feed ----------------------------------------------------------------

func watchFeed(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.MaintainAnimalFeed, domain.NeedDeficit); err != nil {
		return err
	}
	reachableBench := na.AsString(prepared["bench"])
	if _, err := followMethods(ctx, journal, policy.MaintainAnimalFeed, "feed", func(a domain.Action) error {
		switch a.Kind() {
		case domain.AcquisitionAction, domain.MineAcquisitionAction:
			return nil
		case domain.ProductionBillAction:
			// The product drops at the bench, so the bill belongs on the
			// one inside the pet's area, not the earlier one outside it.
			if bill, ok := a.ProductionBill(); ok && bill.Bench() != reachableBench {
				return fmt.Errorf("kibble bill placed on %s outside the pet's area, not %s", bill.Bench(), reachableBench)
			}
			report["feed_bill_bench"] = reachableBench
			return nil
		}
		return fmt.Errorf("unexpected %s action", a.Kind())
	}, report); err != nil {
		return err
	}
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 12*time.Minute)
	defer recoverCancel()
	goal, err := waitNeed(recoverCtx, journal, policy.MaintainAnimalFeed, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["feed_recovered_tick"] = int64(goal.Goal.Tick)
	return nil
}

func verifyFeed(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	pet := na.AsString(prepared["pet"])
	observed, err := readColonyFacts(ctx, h, identity, "animals-after")
	if err != nil {
		return err
	}
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return err
	}
	for _, raw := range na.AsSlice(upkeep["animals"]) {
		row, _ := na.AsMap(raw)
		if na.PawnRef(row) != pet {
			continue
		}
		feed := na.AsSlice(row["reachableStoredFeed"])
		report["pet_reachable_feed_rows"] = len(feed)
		nutrition := 0.0
		for _, rawStock := range feed {
			stock, _ := na.AsMap(rawStock)
			nutrition += na.AsNumber(stock["nutrition"])
		}
		report["pet_reachable_nutrition"] = nutrition
		if len(feed) == 0 || nutrition <= 0 {
			return fmt.Errorf("pet %s has no reachable stored feed natively after recovery", pet)
		}
		return nil
	}
	return fmt.Errorf("pet %s missing from the native animal feed census", pet)
}

// ---- sleeping ------------------------------------------------------------

func watchSleeping(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.MaintainHousing, domain.NeedDeficit); err != nil {
		return err
	}
	// The fixture leaves no vacant suitable bed, so the first method builds
	// one (a Building action). Ownership then comes either from the
	// controller's AssignIntent (a bed_assign action, followed as its own
	// method) or from the colonist claiming the new bed on their own; the
	// goal recovers only on observed sleep in an owned suitable bed, which
	// waitNeed below confirms and verifySleeping checks natively.
	seen := map[domain.PlanID]bool{}
	kinds := []string{}
	if _, err := followMethodsExcluding(ctx, journal, policy.MaintainHousing, "bed", seen, func(a domain.Action) error {
		if _, ok := a.Building(); ok {
			kinds = append(kinds, string(a.Kind()))
			return nil
		}
		return fmt.Errorf("unexpected %s action before a bed was built", a.Kind())
	}, report); err != nil {
		return err
	}
	if report["bed_recovered_by"] != "ordinary_work" {
		if _, err := followMethodsExcluding(ctx, journal, policy.MaintainHousing, "assign", seen, func(a domain.Action) error {
			if _, ok := a.Assign(); ok {
				kinds = append(kinds, string(a.Kind()))
				return nil
			}
			return fmt.Errorf("unexpected %s action after the bed was built", a.Kind())
		}, report); err != nil {
			return err
		}
	}
	report["sleeping_action_kinds"] = kinds
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 15*time.Minute)
	defer recoverCancel()
	goal, err := waitNeed(recoverCtx, journal, policy.MaintainHousing, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["sleeping_recovered_tick"] = int64(goal.Goal.Tick)
	return nil
}

func verifySleeping(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	observed, err := readColonyFacts(ctx, h, identity, "beds-after")
	if err != nil {
		return err
	}
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return err
	}
	beds := na.AsSlice(upkeep["beds"])
	report["beds_after"] = len(beds)
	// Recovery needs an owned humanlike bed that is a real bed (a sleeping
	// spot never satisfies ReviewSleeping) under a roof.
	owned, suitable := 0, 0
	for _, raw := range beds {
		row, _ := na.AsMap(raw)
		if len(na.AsSlice(row["owners"])) == 0 {
			continue
		}
		owned++
		ref, _ := na.AsMap(row["bed"])
		if definition, _ := ref["defName"].(string); definition != "SleepingSpot" && row["roofed"] == true && row["humanlike"] == true {
			suitable++
		}
	}
	report["owned_beds_after"] = owned
	report["owned_suitable_beds_after"] = suitable
	colonists := int(na.AsNumber(observed["colonistCount"]))
	report["colonists"] = colonists
	if colonists == 0 || suitable < colonists {
		return fmt.Errorf("%d of %d colonists own a roofed bed after MaintainHousing recovered", suitable, colonists)
	}
	return nil
}

// ---- cold ----------------------------------------------------------------

func prepareCold(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
	return prepareColdWith(false)(ctx, h, identity, report)
}

// prepareColdWith stages the cold room; coldSnap lets the fixture register
// ordinary ColdSnap conditions when the map has warmed past the campfire
// threshold (a colony that has already played for days, as in the campaign)
// instead of failing fast.
func prepareColdWith(coldSnap bool) func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
	return func(ctx context.Context, h *na.Harness, identity map[string]any, report na.Report) (map[string]any, error) {
		room, err := callFixture(ctx, h, identity, "test/routine_sleeping_prepare", map[string]any{"outdoorSite": false})
		if err != nil {
			return nil, err
		}
		report["room"] = room
		center, _ := na.AsMap(room["center"])
		prepared, err := h.Call(ctx, "prepare-temperature", "test/routine_temperature_prepare", map[string]any{
			"x": int(na.AsNumber(center["x"])), "z": int(na.AsNumber(center["z"])), "hot": false, "coldSnap": coldSnap,
		})
		if err != nil {
			if strings.Contains(err.Error(), "outdoor temperature") {
				return nil, fmt.Errorf("rolled map is not cold enough for the campfire fixture; rerun as a fresh process: %w", err)
			}
			return nil, err
		}
		if success, _ := na.AsBool(prepared["success"]); !success {
			return nil, fmt.Errorf("routine_temperature_prepare refused: %#v", prepared)
		}
		report["cold_snaps"] = prepared["coldSnaps"]
		return prepared, nil
	}
}

func watchCold(ctx context.Context, journal *store.Store, prepared map[string]any, report na.Report) error {
	deficitCtx, deficitCancel := context.WithTimeout(ctx, 4*time.Minute)
	defer deficitCancel()
	if _, err := waitNeed(deficitCtx, journal, policy.EnsureTemperatureSafety, domain.NeedDeficit); err != nil {
		return err
	}
	definition := na.AsString(prepared["definition"])
	if _, err := followMethods(ctx, journal, policy.EnsureTemperatureSafety, "heat", func(a domain.Action) error {
		b, ok := a.Building()
		if !ok {
			return fmt.Errorf("unexpected %s action", a.Kind())
		}
		if b.Definition() != definition {
			return fmt.Errorf("placed %s, expected the fixture's %s", b.Definition(), definition)
		}
		return nil
	}, report); err != nil {
		return err
	}
	recoverCtx, recoverCancel := context.WithTimeout(ctx, 12*time.Minute)
	defer recoverCancel()
	goal, err := waitNeed(recoverCtx, journal, policy.EnsureTemperatureSafety, domain.NeedRecovered)
	if err != nil {
		return err
	}
	report["temperature_recovered_tick"] = int64(goal.Goal.Tick)
	return nil
}

func verifyCold(ctx context.Context, h *na.Harness, identity, prepared map[string]any, report na.Report) error {
	observed, err := readColonyFacts(ctx, h, identity, "temperature-after")
	if err != nil {
		return err
	}
	minC, hasMin := observed["sleepingTemperatureMinC"]
	report["sleeping_temperature_min_c"] = minC
	report["outdoor_temperature_c"] = observed["outdoorTemperatureC"]
	if !hasMin {
		return errors.New("native sleeping temperature unknown after recovery")
	}
	section, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(section, "observed")
	if err != nil {
		return err
	}
	var ids []string
	for _, raw := range na.AsSlice(upkeep["beds"]) {
		row, _ := na.AsMap(raw)
		bed, _ := na.AsMap(row["bed"])
		ids = append(ids, na.AsString(bed["id"]))
	}
	sort.Strings(ids)
	report["beds_after"] = ids
	if na.AsNumber(minC) <= na.AsNumber(observed["outdoorTemperatureC"]) {
		return fmt.Errorf("sleeping minimum %.1f C is no warmer than outdoors %.1f C", na.AsNumber(minC), na.AsNumber(observed["outdoorTemperatureC"]))
	}
	return nil
}
