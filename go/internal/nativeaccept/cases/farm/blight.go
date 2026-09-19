package farm

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const blightPrefix = "blight-accept"

// farm/blight is the blight responder vertical (#245): a live game and a
// live rimgovernor service composed with the blight family. The private
// test/blight_prepare fixture sows a small rice zone near the colonists and
// blights a few plants. The typed colony read's blighted_plants census must
// list exactly those plants undesignated; the routine review opens
// RemoveBlight on the census, the planner admits one plan of CutPlant
// designations on the blighted plants only, the colonist cuts them within a
// stall-bounded window, and the goal settles on the census emptying, never
// on the receipt. After the service releases the game slot an independent
// native census confirms no blighted plant stands.
func init() {
	cases.Register(cases.Case{
		Name: "farm/blight",
		Scope: "Native blight responder: the blighted_plants census drives the live Go routine reviewer/planner to open " +
			"RemoveBlight and admit CutPlant designations on the blighted plants only; the colonists cut them within a " +
			"stall-bounded window and the goal settles on the census emptying, confirmed by an independent native read (#245).",
		Start:   cases.Fixture{Op: "test/blight_prepare"},
		Service: true,
		Budget:  6 * time.Minute,
		Run:     runBlight,
	})
}

func runBlight(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	var service *na.ServiceProcess
	// Failure evidence: the same independent census a pass ends with.
	defer func() {
		if _, hasAfter := report["blight_after"]; hasAfter {
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
		if census, err := ph.Call(stopCtx, "blight-postmortem", "test/blight_census", map[string]any{}); err == nil {
			report["blight_postmortem"] = census
		} else {
			report["blight_postmortem_error"] = err.Error()
		}
	}()
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	fixture := map[string]bool{}
	for _, raw := range na.AsSlice(prepared["plants"]) {
		row, _ := na.AsMap(raw)
		fixture[na.AsString(row["id"])] = true
	}
	if len(fixture) == 0 {
		return fmt.Errorf("fixture blighted no plants: %#v", prepared)
	}
	report["fixture_plants"] = sortedKeys(fixture)
	report["fixture_zone"] = prepared["zoneId"]
	report["fixture_cutter"] = prepared["cutter"]

	// Before: the typed census lists every fixture plant, undesignated, and
	// nothing else blighted stands on the map.
	before, err := readBlightCensus(ctx, h, identity, "blight-before")
	if err != nil {
		return err
	}
	report["blight_before"] = before.evidence()
	if len(before.rows) != len(fixture) {
		return fmt.Errorf("blight-before: census lists %d blighted plants, the fixture blighted %d", len(before.rows), len(fixture))
	}
	for id, row := range before.rows {
		if !fixture[id] || row.designated {
			return fmt.Errorf("blight-before: census row %s designated=%v is not an undesignated fixture plant", id, row.designated)
		}
	}

	// "work" rides along so the colony's work priorities match the
	// controller's own assignment, which keeps plant cutting enabled on the
	// fixture's cutter.
	service, err = s.Launch(ctx, na.ServiceLaunch{Families: []string{"blight", "work", "field"}, Extra: na.ClockSpeedArgs()})
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
	rootPlanID, err := service.Resume(blightPrefix, identity, token, report)
	if err != nil {
		return err
	}
	report["root_plan"] = rootPlanID
	keepAlive := &na.AuthorityKeepAlive{Service: service, Prefix: blightPrefix, Identity: identity, Token: token}
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

	// The methods: each plan is CutPlant designations on fixture plants only,
	// each plant once. Vanilla growers also cut blighted crops in a growing
	// zone once the window runs, so a plant can vanish before its own
	// designation dispatches (an absent cancel, incidental) and the goal can
	// settle on the emptied census before a second method is needed; the
	// vertical is proven by at least one designation the executor observed
	// through to completion and the goal settling on the census.
	var previous *domain.GoalMethod
	var plans []string
	cut, seen := map[string]bool{}, map[string]bool{}
	var settled store.GoalState
	for renewals := 0; len(plans) < 4; {
		methodCtx, methodCancel := context.WithTimeout(ctx, 3*time.Minute)
		goalID, method, goal, done, err := waitBlightMethodOrSettled(methodCtx, journal, service, previous)
		methodCancel()
		if err != nil {
			return fmt.Errorf("blight method: %w", err)
		}
		report["goal_id"] = string(goalID)
		if done {
			settled = goal
			break
		}
		previous = &method
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		actions := plan.Spec.Actions()
		if len(actions) == 0 || len(actions) > 8 {
			return fmt.Errorf("blight plan %s has %d actions", method.Plan, len(actions))
		}
		batch := map[string]bool{}
		for _, action := range actions {
			target, ok := action.CutPlant()
			if !ok {
				return fmt.Errorf("blight plan action is not a cut: %#v", action)
			}
			if !fixture[target.Plant()] || batch[target.Plant()] || cut[target.Plant()] {
				return fmt.Errorf("cut of %s: not a unique uncut fixture plant", target.Plant())
			}
			batch[target.Plant()] = true
			seen[target.Plant()] = true
		}
		// The plan settles only on the native observation of every plant
		// gone; a standing designated plant holds the attempt pending.
		doneCtx, doneCancel := context.WithTimeout(ctx, 4*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return fmt.Errorf("blight plan: %w", err)
		}
		plans = append(plans, string(method.Plan))
		report["blight_plans"] = plans
		for _, progress := range state.Progress {
			view := progress.View()
			target, _ := progress.Action().CutPlant()
			receipt, known := view.Receipt.Value()
			if view.Stage == domain.Completed && known && receipt != "" {
				cut[target.Plant()] = true
				report["blight_completed_tick"] = int64(view.Tick)
			}
		}
		report["plants_cut"] = len(cut)
		if incidental {
			renewals++
			report["incidental_renewals"] = renewals
		}
	}
	if len(cut) == 0 {
		return fmt.Errorf("no cut designation completed under the executor across %d plans", len(plans))
	}
	if settled.Goal.ID == "" {
		// The measured census, not the receipt, settles the goal.
		settleCtx, settleCancel := context.WithTimeout(ctx, 3*time.Minute)
		settled, err = waitBlightSettled(settleCtx, journal, service)
		settleCancel()
		if err != nil {
			return err
		}
	}
	report["blight_goal"] = map[string]any{"status": string(settled.Goal.Status), "need": string(settled.Goal.Need), "methods": len(settled.Methods)}
	report["plants_designated"] = sortedKeys(seen)
	if err := na.AssertRoutineRunning(service.Get); err != nil {
		return err
	}

	// Independent native read after the service releases the game slot.
	fieldCtx, fieldCancel := context.WithTimeout(ctx, 2*time.Minute)
	err = na.WaitProgress(fieldCtx, na.Wait{Stall: na.StallBudget(), Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		plans, err := journal.LoadPlans(ctx, 256)
		if err != nil {
			return "", false, err
		}
		for _, plan := range plans {
			for _, progress := range plan.Progress {
				if _, zone := progress.Action().ZoneCreate(); zone && progress.View().Stage == domain.Completed {
					return "", true, nil
				}
			}
		}
		return na.Signature(len(plans)), false, nil
	})
	fieldCancel()
	if err != nil {
		return fmt.Errorf("second field was not created: %w", err)
	}
	journal.Close()
	service.Stop()
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen harness session after service stop: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	// Ordinary growers must sow the new zone; its creation receipt is not
	// proof of planting. Advance a bounded window before the native audit.
	if _, err := s.Advance(ctx, 12000); err != nil {
		return err
	}
	native, err := h.Call(ctx, "blight-after-native", "test/blight_census", map[string]any{})
	if err != nil {
		return err
	}
	report["blight_after_native"] = native
	if err := separatedPlantedField(native, prepared); err != nil {
		return err
	}
	if rows := na.AsSlice(native["blighted"]); len(rows) != 0 {
		return fmt.Errorf("blight-after: %d blighted plants still stand natively: %#v", len(rows), rows)
	}
	after, err := readBlightCensus(ctx, h, identity, "blight-after")
	if err != nil {
		return err
	}
	report["blight_after"] = after.evidence()
	if len(after.rows) != 0 {
		return fmt.Errorf("blight-after: census still lists %d plants", len(after.rows))
	}
	logData, err := os.ReadFile(s.Config().StartupLogPath())
	if err != nil {
		return fmt.Errorf("read startup log: %w", err)
	}
	return na.CheckStartupLog(string(logData), s.Config().Headless)
}

func separatedPlantedField(native, prepared map[string]any) error {
	old := map[domain.Cell]bool{}
	for _, raw := range na.AsSlice(prepared["zoneCells"]) {
		c, _ := na.AsMap(raw)
		old[domain.Cell{X: int32(na.AsNumber(c["x"])), Z: int32(na.AsNumber(c["z"]))}] = true
	}
	if len(old) == 0 {
		return fmt.Errorf("fixture omitted field cells")
	}
	planted := false
	for _, raw := range na.AsSlice(native["zones"]) {
		zone, _ := na.AsMap(raw)
		if na.AsNumber(zone["id"]) == na.AsNumber(prepared["zoneId"]) {
			continue
		}
		for _, raw := range na.AsSlice(zone["cells"]) {
			c, _ := na.AsMap(raw)
			cell := domain.Cell{X: int32(na.AsNumber(c["x"])), Z: int32(na.AsNumber(c["z"]))}
			for _, n := range []domain.Cell{{X: cell.X - 1, Z: cell.Z}, {X: cell.X + 1, Z: cell.Z}, {X: cell.X, Z: cell.Z - 1}, {X: cell.X, Z: cell.Z + 1}} {
				if old[n] {
					return fmt.Errorf("second field at %v shares an edge with the original field", cell)
				}
			}
		}
		planted = planted || na.AsNumber(zone["planted"]) > 0
	}
	if !planted {
		return fmt.Errorf("no separate second field contains a natively sown crop")
	}
	return nil
}

type blightRow struct {
	cell       domain.Cell
	zone       string
	designated bool
}
type blightSummary struct {
	tick int64
	rows map[string]blightRow
}

func (s blightSummary) evidence() map[string]any {
	rows := make([]map[string]any, 0, len(s.rows))
	for _, id := range sortedKeys(s.rows) {
		row := s.rows[id]
		rows = append(rows, map[string]any{"id": id, "x": row.cell.X, "z": row.cell.Z, "zone": row.zone, "designated": row.designated})
	}
	return map[string]any{"tick": s.tick, "plants": rows}
}

func sortedKeys[V any](m map[string]V) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}

// readBlightCensus decodes the typed colony facts' blighted_plants rows the
// way the Go projection does.
func readBlightCensus(ctx context.Context, h *na.Harness, identity map[string]any, label string) (blightSummary, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return blightSummary{}, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return blightSummary{}, err
	}
	for _, raw := range na.AsSlice(observed["issues"]) {
		issue, _ := na.AsMap(raw)
		if na.AsString(issue["field"]) == "blighted_plants" {
			return blightSummary{}, fmt.Errorf("%s: blight census unavailable: %#v", label, issue)
		}
	}
	context, _ := na.AsMap(observed["context"])
	s := blightSummary{tick: int64(na.AsNumber(context["tick"])), rows: map[string]blightRow{}}
	for _, raw := range na.AsSlice(observed["blightedPlants"]) {
		row, _ := na.AsMap(raw)
		plant, _ := na.AsMap(row["plant"])
		cell, _ := na.AsMap(plant["position"])
		designated, _ := na.AsBool(row["designated"])
		id := na.AsString(plant["id"])
		if id == "" {
			return blightSummary{}, fmt.Errorf("%s: blighted plant row without a thing id: %#v", label, row)
		}
		s.rows[id] = blightRow{cell: domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))}, zone: na.AsString(row["zoneId"]), designated: designated}
	}
	return s, nil
}

func blightSettled(goal store.GoalState) bool {
	return goal.Goal.Need == domain.NeedRecovered && goal.Goal.Status == domain.GoalSatisfied
}

// waitBlightMethodOrSettled polls the journal for RemoveBlight's goal
// binding and either a committed method on it other than previous (the
// goal's live methods, then the epoch's bounded history) or the goal
// settled: recovered and satisfied on the emptied census.
func waitBlightMethodOrSettled(ctx context.Context, s *store.Store, service *na.ServiceProcess, previous *domain.GoalMethod) (domain.GoalID, domain.GoalMethod, store.GoalState, bool, error) {
	var goal store.GoalState
	var found domain.GoalMethod
	settled := false
	err := na.WaitProgress(ctx, na.Wait{Stall: na.StallBudget(), Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.RemoveBlight {
				continue
			}
			if goal, err = s.LoadGoal(ctx, binding.Goal); err != nil {
				return "", false, err
			}
			methods := goal.Methods
			if len(methods) == 0 {
				if methods, err = s.LoadGoalMethods(ctx, binding.Goal, goal.Goal.Epoch); err != nil {
					return "", false, err
				}
			}
			for _, method := range methods {
				if previous == nil || method.Plan != previous.Plan {
					found = method
					return "", true, nil
				}
			}
			if blightSettled(goal) {
				settled = true
				return "", true, nil
			}
			return na.Signature(goal.Goal.Need, goal.Goal.Status, len(methods)), false, nil
		}
		return na.Signature("unbound", review.Revision), false, nil
	})
	if err != nil {
		return "", domain.GoalMethod{}, goal, false, fmt.Errorf("RemoveBlight neither admitted a new method nor settled (need=%s status=%s): %w", goal.Goal.Need, goal.Goal.Status, err)
	}
	return goal.Goal.ID, found, goal, settled, nil
}

// waitBlightSettled polls the journal until RemoveBlight's goal reads
// recovered and satisfied: the census emptied under the review, which is
// what settles the goal.
func waitBlightSettled(ctx context.Context, s *store.Store, service *na.ServiceProcess) (store.GoalState, error) {
	var goal store.GoalState
	err := na.WaitProgress(ctx, na.Wait{Stall: na.StallBudget(), Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return "", false, err
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.RemoveBlight {
				continue
			}
			if goal, err = s.LoadGoal(ctx, binding.Goal); err != nil {
				return "", false, err
			}
			if blightSettled(goal) {
				return "", true, nil
			}
			return na.Signature(goal.Goal.Need, goal.Goal.Status, goal.Revision), false, nil
		}
		return na.Signature("unbound", review.Revision), false, nil
	})
	if err != nil {
		return goal, fmt.Errorf("RemoveBlight never settled on the emptied census (need=%s status=%s): %w", goal.Goal.Need, goal.Goal.Status, err)
	}
	return goal, nil
}
