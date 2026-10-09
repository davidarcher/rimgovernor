package farm

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"sort"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const blightPrefix = "blight-accept"

// farm/blight is the blight responder vertical: a live game and a
// live rimgovernor service composed with the blight family. The private
// test/blight_prepare fixture sows a small rice zone near the colonists and
// blights a few plants. The Go projection's blight census (plant things in the planning window) must
// list exactly those plants undesignated; the rounds opens
// RemoveBlight on the census, the planner admits one plan of CutPlant
// designations on the blighted plants only, the colonist cuts them within a
// stall-bounded window, and the goal settles on the census emptying, never
// on the receipt. After the service releases the game slot an independent
// native census confirms no blighted plant stands.
func init() {
	cases.Register(cases.Case{
		Name: "farm/blight",
		Scope: "Native blight responder: the planning-window blight census drives the live Go rounder/planner to open " +
			"RemoveBlight and admit CutPlant designations on the blighted plants only; the colonists cut them within a " +
			"stall-bounded window and the standard settles on the census emptying, confirmed by an independent native read (#245).",
		Start:   cases.Fixture{Op: "test/blight_prepare", On: cases.LabStart()},
		Service: true,
		Budget:  6 * time.Minute,
		Crew:    cases.Crew{Size: 3}, Run: runBlight,
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
	service, err = s.Launch(ctx, na.ServiceLaunch{Families: []routinefamily.Family{routinefamily.Blight, routinefamily.Work, routinefamily.Field}, Extra: na.ClockSpeedArgs()})
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
	review, diagnostics, err := service.WaitRounds(ctx, journal, 90*time.Second)
	report["diagnostic_post_acquire"] = diagnostics
	if err != nil {
		return err
	}
	reviewData, _ := json.Marshal(review)
	report["rounds_review_first"] = json.RawMessage(reviewData)

	// The methods: each plan is CutPlant designations on fixture plants only,
	// each plant once. Vanilla never cuts blight undesignated, yet on the lab
	// the other blighted plants leave the census within a few thousand
	// ticks of the first designation, so a plant can vanish before its own
	// designation dispatches (an absent cancel, incidental) and the goal can
	// settle on the emptied census before a second method is needed; the
	// vertical is proven by at least one designation the executor observed
	// through to completion and the goal settling on the census.
	var previous *domain.Method
	var plans []string
	cut, seen := map[string]bool{}, map[string]bool{}
	var settled store.StandardState
	for renewals := 0; len(plans) < 4; {
		methodCtx, methodCancel := context.WithTimeout(ctx, 3*time.Minute)
		concernID, method, goal, done, err := waitBlightMethodOrSettled(methodCtx, journal, service, previous)
		methodCancel()
		if err != nil {
			return fmt.Errorf("blight method: %w", err)
		}
		report["concern_id"] = string(concernID)
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
		// Each cut is a DesignateIntent: the plan completes on its applied
		// receipts; the goal settles when the census empties.
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
	if settled.Standard.ID == "" {
		// The measured census, not the receipt, settles the goal.
		settleCtx, settleCancel := context.WithTimeout(ctx, 3*time.Minute)
		settled, err = waitBlightSettled(settleCtx, journal, service)
		settleCancel()
		if err != nil {
			return err
		}
	}
	report["blight_standard"] = map[string]any{"status": string(settled.Standard.Status), "need": string(settled.Standard.Finding), "methods": len(settled.Methods)}
	report["plants_designated"] = sortedKeys(seen)
	if err := na.AssertRoundsRunning(service.Get); err != nil {
		return err
	}

	// Independent native read after the service releases the game slot.
	fieldCtx, fieldCancel := context.WithTimeout(ctx, 2*time.Minute)
	err = na.WaitProgress(fieldCtx, na.Wait{Stall: na.StallBudget(), Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		plans, err := journal.LoadPlans(ctx)
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
	if _, err := h.Call(ctx, "pause-after", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	// Ordinary growers must sow the new zone; its creation receipt is not
	// proof of planting. Advance a bounded window before the native audit,
	// in steps: an advance never renews its lease, and a Superfast window
	// under box load (338 tps) outran the 30 s lease on 12000 ticks.
	for range 4 {
		if _, err := s.Advance(ctx, 3000); err != nil {
			return err
		}
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
	return nil
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

// readBlightCensus reads the Go projection's blight census, built from the
// planning window's plant things, the way the planner reads it.
func readBlightCensus(ctx context.Context, h *na.Harness, _ map[string]any, label string) (blightSummary, error) {
	facts, _, err := startersite.Survey(ctx, h)
	if err != nil {
		return blightSummary{}, fmt.Errorf("%s: %w", label, err)
	}
	census, known := facts.Facts.Blight.Value()
	if !known {
		return blightSummary{}, fmt.Errorf("%s: blight census unavailable", label)
	}
	s := blightSummary{tick: int64(facts.Identity.Tick), rows: map[string]blightRow{}}
	for _, plant := range census {
		s.rows[plant.ID] = blightRow{cell: plant.Cell, zone: plant.Zone, designated: plant.Designated}
	}
	return s, nil
}

func blightSettled(goal store.StandardState) bool {
	return goal.Standard.Finding == domain.FindingMet && goal.Standard.Status == domain.StandardSettled
}

// waitBlightMethodOrSettled polls the journal for RemoveBlight's goal
// binding and either a committed method on it other than previous (the
// goal's live methods, then the epoch's bounded history) or the goal
// settled: recovered and satisfied on the emptied census.
func waitBlightMethodOrSettled(ctx context.Context, s *store.Store, service *na.ServiceProcess, previous *domain.Method) (domain.ConcernID, domain.Method, store.StandardState, bool, error) {
	var goal store.StandardState
	var found domain.Method
	settled := false
	err := na.WaitProgress(ctx, na.Wait{Stall: na.StallBudget(), Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		for _, binding := range review.Standards {
			if binding.Concern != policy.RemoveBlight {
				continue
			}
			if goal, err = s.LoadStandard(ctx, binding.Standard); err != nil {
				return "", false, err
			}
			methods := goal.Methods
			if len(methods) == 0 {
				if methods, err = s.LoadMethods(ctx, binding.Standard, goal.Standard.Episode); err != nil {
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
			return na.Signature(goal.Standard.Finding, goal.Standard.Status, len(methods)), false, nil
		}
		return na.Signature("unbound", review.Revision), false, nil
	})
	if err != nil {
		return "", domain.Method{}, goal, false, fmt.Errorf("RemoveBlight neither admitted a new method nor settled (need=%s status=%s): %w", goal.Standard.Finding, goal.Standard.Status, err)
	}
	return goal.Standard.ID, found, goal, settled, nil
}

// waitBlightSettled polls the journal until RemoveBlight's goal reads
// recovered and satisfied: the census emptied under the review, which is
// what settles the goal.
func waitBlightSettled(ctx context.Context, s *store.Store, service *na.ServiceProcess) (store.StandardState, error) {
	var goal store.StandardState
	err := na.WaitProgress(ctx, na.Wait{Stall: na.StallBudget(), Interval: time.Second, Terminal: service.Exited}, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRounds(ctx)
		if err != nil {
			return "", false, err
		}
		for _, binding := range review.Standards {
			if binding.Concern != policy.RemoveBlight {
				continue
			}
			if goal, err = s.LoadStandard(ctx, binding.Standard); err != nil {
				return "", false, err
			}
			if blightSettled(goal) {
				return "", true, nil
			}
			return na.Signature(goal.Standard.Finding, goal.Standard.Status, goal.Revision), false, nil
		}
		return na.Signature("unbound", review.Revision), false, nil
	})
	if err != nil {
		return goal, fmt.Errorf("RemoveBlight never settled on the emptied census (need=%s status=%s): %w", goal.Standard.Finding, goal.Standard.Status, err)
	}
	return goal, nil
}
