// Package light holds the MaintainLighting vertical (issue #6 slice 3) on
// the lab contract (#747): a live game and a live rimgovernor Go
// player-control service composed with the lighting family.
//
//	dark -- an enclosed roofed room on the blank lab holds a fuelled stove
//	        whose interaction cell native measures dark, with no lamp in
//	        reach. The service must latch the bench from the measured glow,
//	        admit exactly one affordable lamp (a TorchLamp: the colony has
//	        no power source) on a free cell of the room within the placement
//	        radius, the colonists build it, and the next measured census
//	        must release the latch. An independent native read then confirms
//	        the cell reads lit and the lamp stands where it was admitted.
//
// The outage, partial, fungus and repair decisions are snapshot tests over
// their recorded reviews (internal/buildingruntime, #747).
//
// Uses the private disposable test/lighting_prepare fixture
// (LightingFixture.cs). The case's own bridge session and the service's
// are used sequentially, never concurrently (one GABP client per game).
package light

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/routinefamily"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const prefix = "light-accept"

func init() {
	cases.Register(cases.Case{
		Name: "light/dark",
		Scope: "Native MaintainLighting vertical on the lab: a measured-dark stove interaction cell in an enclosed room " +
			"drives the live Go rounder/planner to admit one affordable lamp beside it; " +
			"the measured glow, not the receipt, releases the latch, confirmed by an independent native read.",
		Start:   cases.Fixture{Op: "test/lighting_prepare", Args: map[string]any{"scenario": "dark"}, On: cases.LabStart()},
		Service: true,
		// ~2x the measured healthy run (403eebbb): the lamp is admitted in
		// under a minute.
		Budget: 5 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Run: run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	var service *na.ServiceProcess
	// Failure evidence: the same independent lighting read a pass ends
	// with, taken once the service has let go of the game.
	defer func() {
		if _, hasAfter := report["lighting_after"]; hasAfter {
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
		if after, err := readLighting(stopCtx, ph, identity, "lighting-postmortem"); err == nil {
			report["lighting_postmortem"] = after.evidence()
		} else {
			report["lighting_postmortem_error"] = err.Error()
		}
	}()
	stoveID := na.AsString(prepared["stove"])
	interior, _ := na.AsMap(prepared["interior"])
	workCellMap, _ := na.AsMap(prepared["workCell"])
	workCell := domain.Cell{X: int32(na.AsNumber(workCellMap["x"])), Z: int32(na.AsNumber(workCellMap["z"]))}
	inside := func(c domain.Cell) bool {
		return float64(c.X) >= na.AsNumber(interior["minX"]) && float64(c.X) <= na.AsNumber(interior["maxX"]) &&
			float64(c.Z) >= na.AsNumber(interior["minZ"]) && float64(c.Z) <= na.AsNumber(interior["maxZ"])
	}

	// Before: the typed lighting census must list the stove's interaction
	// cell roofed and dark -- the exact facts the review latches on -- and,
	// for outage, the fixture lamp unlit and unpowered.
	lighting := policy.DefaultLightingPolicy()
	before, err := readLighting(ctx, h, identity, "lighting-before")
	if err != nil {
		return err
	}
	report["lighting_before"] = before.evidence()
	stove, ok := before.cells[stoveID]
	if !ok || !stove.roofed || stove.glow >= lighting.LitGlow || stove.cell != workCell {
		return fmt.Errorf("lighting-before: fixture stove is not a roofed dark work cell: %+v", stove)
	}
	if len(before.lamps) != 0 {
		return fmt.Errorf("lighting-before: %d lamps present before the controller acts", len(before.lamps))
	}
	if stove.lightSensitive {
		return fmt.Errorf("lighting-before: work cell marked light-sensitive without a cave plant: %+v", stove)
	}

	// "work" rides along because every building method's builder check
	// requires the colony's work priorities to match the controller's own
	// assignment, which only the work family applies.
	// The outage and fungus holds are attributed from the scheduler's
	// clock_step rows in the flight recording.
	service, err = s.Launch(ctx, na.ServiceLaunch{
		Families: []routinefamily.Family{routinefamily.Lighting, routinefamily.Work},
		Extra:    na.ClockSpeedArgs(),
	})
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
	// No anchor plan: a player construction project would commit the
	// colony's builder and starve the ranked lamp of construction labor.
	rootPlanID, err := service.Resume(prefix, identity, token, report)
	if err != nil {
		return err
	}
	report["root_plan"] = rootPlanID
	keepAlive := &na.AuthorityKeepAlive{Service: service, Prefix: prefix, Identity: identity, Token: token}
	stopKeepAlive := keepAlive.Start(ctx)
	// stopped is what each keep-alive counted once its service was stopped;
	// repair runs a second one for the restarted controller.
	stopped := map[string]any{}
	defer func() {
		if stopKeepAlive != nil {
			stopped["running"] = stopKeepAlive()
		}
		report["authority_reacquisitions"] = stopped
	}()

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

	// The review must latch the stove and bind MaintainLighting.
	waitCtx, waitCancel := context.WithTimeout(ctx, 3*time.Minute)
	latched, err := waitLatch(waitCtx, journal, service, stoveID)
	waitCancel()
	if err != nil {
		return fmt.Errorf("lighting latch: %w", err)
	}
	report["latched_review_revision"] = latched.Revision

	lamp, err := admitAndRelease(ctx, admission{journal: journal, service: service, stove: stoveID, work: workCell, inside: inside, radius: lighting.PlacementRadius, report: report})
	if err != nil {
		return err
	}
	if err := waitRunning(ctx, service.Get, storeWait(service)); err != nil {
		return err
	}

	// Independent native read after the service releases the game slot.
	journal.Close()
	stopped["first"], stopKeepAlive = stopKeepAlive(), nil
	service.Stop()
	if h, err = s.Reattach(ctx); err != nil {
		return fmt.Errorf("reopen harness session after service stop: %w", err)
	}
	if _, err := h.Call(ctx, "pause-after", "rimgovernor/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); err != nil {
		return err
	}
	after, err := readLighting(ctx, h, identity, "lighting-after")
	if err != nil {
		return err
	}
	report["lighting_after"] = after.evidence()
	if err := checkLit(after, stoveID, lamp, lighting.LitGlow); err != nil {
		return fmt.Errorf("lighting-after: %w", err)
	}
	return finish(s)
}

// checkLit is the independent read the case ends on: the stove cell lit
// and exactly the admitted lamp standing lit where it was placed.
func checkLit(after lightingSummary, stoveID string, lamp admitted, litGlow float64) error {
	stoveAfter, ok := after.cells[stoveID]
	if !ok || stoveAfter.glow < litGlow {
		return fmt.Errorf("stove cell glow %.2f is still under %.2f", stoveAfter.glow, litGlow)
	}
	if len(after.lamps) != 1 {
		return fmt.Errorf("expected exactly 1 lamp, observed %d: %+v", len(after.lamps), after.lamps)
	}
	for _, l := range after.lamps {
		if l.cell != lamp.cell || l.definition != lamp.definition || !l.lit {
			return fmt.Errorf("the surviving lamp %+v is not the lit %s admitted at %v", l, lamp.definition, lamp.cell)
		}
	}
	return nil
}

// admission is what admitAndRelease needs from the running case: the
// service's journal, the latched stove, the room and where to report.
type admission struct {
	journal *store.Store
	service *na.ServiceProcess
	stove   string
	work    domain.Cell
	inside  func(domain.Cell) bool
	radius  int32
	report  na.Report
	// previous, when set, is the plan of a lamp already admitted on the
	// same journal: the repair wait must see a newer method. keys prefixes
	// the report keys ("" on the first pass, "repair_" on the second).
	previous *domain.Method
	keys     string
}

// admitted is the lamp admitAndRelease saw built: its cell, definition
// and the method that placed it.
type admitted struct {
	cell       domain.Cell
	definition string
	method     domain.Method
	// released is the review revision that let go of the latch.
	released uint64
}

// admitAndRelease follows one lighting method from commitment through the
// build to the measured census releasing the latch: one lamp build on a
// free interior cell within the placement radius of the interaction cell,
// never on it, renewed across incidental cancellations.
func admitAndRelease(ctx context.Context, a admission) (admitted, error) {
	journal, service, report := a.journal, a.service, a.report
	key := func(name string) string { return a.keys + name }
	methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
	concernID, method, err := na.WaitMethod(methodCtx, journal, policy.MaintainLighting, a.previous)
	methodCancel()
	if err != nil {
		return admitted{}, fmt.Errorf("lighting method: %w", err)
	}
	report[key("concern_id")] = string(concernID)
	var builtCell domain.Cell
	var builtDefinition string
	for renewals := 0; ; renewals++ {
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return admitted{}, err
		}
		actions := plan.Spec.Actions()
		if len(actions) != 1 {
			return admitted{}, fmt.Errorf("lighting plan %s has %d actions, expected 1", method.Plan, len(actions))
		}
		b, ok := actions[0].Building()
		if !ok {
			return admitted{}, fmt.Errorf("lighting plan action is not a building: %#v", actions[0])
		}
		builtCell, builtDefinition = b.Cell(), b.Definition()
		if builtDefinition != "TorchLamp" {
			return admitted{}, fmt.Errorf("lighting admitted %s; a colony without a power source must choose the TorchLamp", builtDefinition)
		}
		if builtCell == a.work || !a.inside(builtCell) || max(abs(builtCell.X-a.work.X), abs(builtCell.Z-a.work.Z)) > a.radius {
			return admitted{}, fmt.Errorf("lamp placed at %v: not a free interior cell within %d of the work cell %v", builtCell, a.radius, a.work)
		}
		report[key("lamp_cell")] = map[string]any{"x": builtCell.X, "z": builtCell.Z, "definition": builtDefinition}
		doneCtx, doneCancel := context.WithTimeout(ctx, 10*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return admitted{}, fmt.Errorf("lighting plan: %w", err)
		}
		if !incidental {
			report[key("lighting_plan")] = string(method.Plan)
			report[key("lighting_completed_tick")] = int64(state.Progress[0].View().Tick)
			report[key("incidental_renewals")] = renewals
			break
		}
		renewCtx, renewCancel := context.WithTimeout(ctx, 5*time.Minute)
		_, method, err = na.WaitMethod(renewCtx, journal, policy.MaintainLighting, &method)
		renewCancel()
		if err != nil {
			return admitted{}, fmt.Errorf("renewed lighting method after incidental cancellation #%d: %w", renewals+1, err)
		}
	}

	// The measured census releases the latch once the cell reads lit.
	releaseCtx, releaseCancel := context.WithTimeout(ctx, 5*time.Minute)
	released, err := waitRelease(releaseCtx, journal, service, a.stove)
	releaseCancel()
	if err != nil {
		return admitted{}, fmt.Errorf("lighting release: %w", err)
	}
	report[key("released_review_revision")] = released.Revision
	methods, err := lightingMethods(ctx, journal)
	if err != nil {
		return admitted{}, err
	}
	report[key("lighting_methods")] = len(methods)
	return admitted{cell: builtCell, definition: builtDefinition, method: method, released: released.Revision}, nil
}

// finish checks the game's startup log, the way every scenario ends.
func finish(s cases.Session) error {
	return nil
}

func abs(v int32) int32 {
	if v < 0 {
		return -v
	}
	return v
}

type workCellRow struct {
	cell           domain.Cell
	glow           float64
	roofed         bool
	lightSensitive bool
}
type lampRow struct {
	definition string
	cell       domain.Cell
	radius     float64
	lit        bool
	powered    bool
}
type lightingSummary struct {
	cells map[string]workCellRow
	lamps map[string]lampRow
}

func (s lightingSummary) evidence() map[string]any {
	cells := map[string]any{}
	for id, c := range s.cells {
		cells[id] = map[string]any{"x": c.cell.X, "z": c.cell.Z, "glow": c.glow, "roofed": c.roofed, "light_sensitive": c.lightSensitive}
	}
	lamps := map[string]any{}
	for id, l := range s.lamps {
		lamps[id] = map[string]any{"definition": l.definition, "x": l.cell.X, "z": l.cell.Z, "glow_radius": l.radius, "lit": l.lit, "powered": l.powered}
	}
	return map[string]any{"work_cells": cells, "lamps": lamps}
}

// readLighting decodes the typed upkeep lighting section the way the Go
// projection does: work cells keyed by bench ID, lamps keyed by building ID.
func readLighting(ctx context.Context, h *na.Harness, identity map[string]any, label string) (lightingSummary, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false,
	})
	if err != nil {
		return lightingSummary{}, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return lightingSummary{}, err
	}
	upkeepSection, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(upkeepSection, "observed")
	if err != nil {
		return lightingSummary{}, fmt.Errorf("%s: upkeep unavailable: %w", label, err)
	}
	for _, raw := range na.AsSlice(upkeep["issues"]) {
		issue, _ := na.AsMap(raw)
		if na.AsString(issue["field"]) == "lighting" {
			return lightingSummary{}, fmt.Errorf("%s: lighting census unavailable: %#v", label, issue)
		}
	}
	section, _ := na.AsMap(upkeep["lighting"])
	_, facts, err := na.Outcome(section, "observed")
	if err != nil {
		return lightingSummary{}, fmt.Errorf("%s: lighting section unavailable: %w", label, err)
	}
	s := lightingSummary{cells: map[string]workCellRow{}, lamps: map[string]lampRow{}}
	for _, raw := range na.AsSlice(facts["workCells"]) {
		row, _ := na.AsMap(raw)
		bench, _ := na.AsMap(row["bench"])
		cell, _ := na.AsMap(row["cell"])
		roofed, _ := na.AsBool(row["roofed"])
		sensitive, _ := na.AsBool(row["lightSensitive"])
		s.cells[na.AsString(bench["id"])] = workCellRow{cell: domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))}, glow: na.AsNumber(row["glow"]), roofed: roofed, lightSensitive: sensitive}
	}
	rows, err := h.BuildingRows(ctx, label+"-buildings", identity)
	if err != nil {
		return lightingSummary{}, err
	}
	for _, raw := range na.AsSlice(facts["lamps"]) {
		row, _ := na.AsMap(raw)
		ref, _ := na.AsMap(row["building"])
		building, ok := rows[na.AsString(ref["id"])]
		if !ok {
			return lightingSummary{}, fmt.Errorf("%s: lamp %v is not in the building table", label, ref["id"])
		}
		head, _ := na.AsMap(building["building"])
		position, _ := na.AsMap(head["position"])
		service, _ := na.AsMap(building["service"])
		lit, _ := na.AsBool(row["lit"])
		powered, _ := na.AsBool(service["powerOn"])
		s.lamps[na.AsString(ref["id"])] = lampRow{definition: na.AsString(head["defName"]), cell: domain.Cell{X: int32(na.AsNumber(position["x"])), Z: int32(na.AsNumber(position["z"]))}, radius: na.AsNumber(row["glowRadius"]), lit: lit, powered: powered}
	}
	return s, nil
}

func latchedOn(review store.Rounds, bench string) bool {
	for _, id := range review.Latches.Lighting {
		if id == bench {
			return true
		}
	}
	return false
}

// storeWait bounds the journal polls below: the shared stall budget, and
// the service exiting on its own ends a wait at once.
func storeWait(service *na.ServiceProcess) na.Wait {
	return na.Wait{Stall: na.StallBudget(), Terminal: service.Exited}
}

func waitLatch(ctx context.Context, s *store.Store, service *na.ServiceProcess, bench string) (store.Rounds, error) {
	review, err := na.WaitReview(ctx, s, storeWait(service), func(r store.Rounds) bool {
		if !latchedOn(r, bench) {
			return false
		}
		for _, binding := range r.Standards {
			if binding.Concern == policy.MaintainLighting {
				return true
			}
		}
		return false
	})
	if err != nil {
		return review, fmt.Errorf("review never latched %s with a bound MaintainLighting standard (revision %d, latches %+v): %w", bench, review.Revision, review.Latches.Lighting, err)
	}
	return review, nil
}

func waitRelease(ctx context.Context, s *store.Store, service *na.ServiceProcess, bench string) (store.Rounds, error) {
	review, err := na.WaitReview(ctx, s, storeWait(service), func(r store.Rounds) bool { return lightingReleased(r, bench) })
	if err != nil {
		return review, fmt.Errorf("review never released the lighting latch on %s (revision %d): %w", bench, review.Revision, err)
	}
	return review, nil
}

// lightingMethods lists every method ever committed on a MaintainLighting
// goal, across Episodes, from the journal.
func lightingMethods(ctx context.Context, s *store.Store) ([]domain.Method, error) {
	review, err := s.LoadRounds(ctx)
	if err != nil {
		return nil, err
	}
	var out []domain.Method
	for _, binding := range review.Standards {
		if binding.Concern != policy.MaintainLighting {
			continue
		}
		goal, err := s.LoadStandard(ctx, binding.Standard)
		if err != nil {
			return nil, err
		}
		out = append(out, goal.Methods...)
	}
	return out, nil
}

// A disabled review after a transport timeout cannot prove measured light.
// Keep waiting while the case's authority keep-alive resumes the controller.
func lightingReleased(r store.Rounds, bench string) bool {
	return r.Enabled && !latchedOn(r, bench)
}

// The case explicitly renews authority; a momentary Manual state is not a
// terminal failure. Persistent loss still fails within the shared stall budget.
func waitRunning(ctx context.Context, get na.HTTPGet, w na.Wait) error {
	var last error
	err := na.WaitProgress(ctx, w, func(context.Context) (string, bool, error) {
		last = na.AssertRoundsRunning(get)
		return "authority", last == nil, nil
	})
	if err != nil {
		return fmt.Errorf("lighting authority recovery: %w (last state: %v)", err, last)
	}
	return nil
}
