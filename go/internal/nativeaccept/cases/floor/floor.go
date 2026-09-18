// Package floor holds the MaintainFlooring vertical (issue #6 slice 4): a
// live game and a live rimgovernor Go player-control service composed with
// the flooring family. An enclosed roofed kitchen (a fuelled stove) stands
// on bare soil, whose terrain cleanliness native measures negative. The
// service must latch the room from the measured census, admit an
// affordable floor from the policy's list on the deficient interior cells
// only, the colonists lay it, and the next measured census must release
// the latch. An independent native read then confirms every interior cell
// reads a laid, non-negative-cleanliness terrain.
//
// Uses the private disposable test/flooring_prepare fixture
// (FlooringFixture.cs). The case's own bridge session and the service's
// are used sequentially, never concurrently (one GABP client per game).
package floor

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

const prefix = "floor-accept"

func init() {
	cases.Register(cases.Case{
		Name: "floor/kitchen",
		Scope: "Native MaintainFlooring vertical: a measured-deficient kitchen interior on bare soil drives the live Go " +
			"routine reviewer/planner to admit an affordable floor on the deficient cells only; the colonists lay it and " +
			"the measured census, not the receipt, releases the latch, confirmed by an independent native read.",
		Start:   cases.Fixture{Op: "test/flooring_prepare"},
		Service: true,
		Budget:  6 * time.Minute,
		Run:     run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	var service *na.ServiceProcess
	var interior cellRect
	// Failure evidence: the same independent flooring read a pass ends with.
	defer func() {
		if _, hasAfter := report["flooring_after"]; hasAfter {
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
		if after, err := readFlooring(stopCtx, ph, identity, "flooring-postmortem", interior); err == nil {
			report["flooring_postmortem"] = after.evidence()
		} else {
			report["flooring_postmortem_error"] = err.Error()
		}
	}()
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	roomID := na.AsString(prepared["roomId"])
	rect, _ := na.AsMap(prepared["interior"])
	interior = cellRect{minX: int32(na.AsNumber(rect["minX"])), minZ: int32(na.AsNumber(rect["minZ"])), maxX: int32(na.AsNumber(rect["maxX"])), maxZ: int32(na.AsNumber(rect["maxZ"]))}
	// The review keys a room by its lowest-sorted cell, the interior's
	// south-west corner here.
	roomKey := fmt.Sprintf("%d,%d", interior.minX, interior.minZ)
	flooring := policy.DefaultFlooringPolicy()
	allowed := map[string]bool{}
	for _, f := range flooring.Floors {
		allowed[f] = true
	}

	// Before: the typed flooring census must list the fixture room as a
	// kitchen whose every interior cell stands on natural ground with a
	// negative terrain cleanliness -- the exact facts the review latches on.
	before, err := readFlooring(ctx, h, identity, "flooring-before", interior)
	if err != nil {
		return err
	}
	report["flooring_before"] = before.evidence()
	if before.roomID != roomID || before.role != string(policy.RoomRoleKitchen) {
		return fmt.Errorf("flooring-before: fixture room %s/%s is not the prepared kitchen %s", before.roomID, before.role, roomID)
	}
	if len(before.cells) != interior.area() || before.deficient() != interior.area() || before.pending != 0 {
		return fmt.Errorf("flooring-before: %d of %d interior cells read deficient, %d pending", before.deficient(), len(before.cells), before.pending)
	}

	// "work" rides along because every building method's builder check
	// requires the colony's work priorities to match the controller's own
	// assignment, which only the work family applies.
	service, err = s.Launch(ctx, na.ServiceLaunch{Families: []string{"flooring", "work"}, Extra: na.ClockSpeedArgs()})
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
	// colony's builder and starve the ranked floor of construction labor.
	rootPlanID, err := service.Resume(prefix, identity, token, report)
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

	// The review must latch the room and bind MaintainFlooring.
	waitCtx, waitCancel := context.WithTimeout(ctx, 3*time.Minute)
	latched, err := waitLatch(waitCtx, journal, service, roomKey)
	waitCancel()
	if err != nil {
		return fmt.Errorf("flooring latch: %w", err)
	}
	report["latched_review_revision"] = latched.Revision

	// The flooring methods: each plan lays one policy floor on deficient
	// interior cells only, never twice on a cell, until the measured census
	// releases the room. Incidental cancellations renew the method.
	var previous *domain.GoalMethod
	var plans []string
	laid := map[domain.Cell]string{}
	definition := ""
	released := false
	for renewals := 0; !released && len(plans) < 8; {
		methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
		goalID, method, err := na.WaitGoalMethod(methodCtx, journal, policy.MaintainFlooring, previous)
		methodCancel()
		if err != nil {
			return fmt.Errorf("flooring method: %w", err)
		}
		report["goal_id"] = string(goalID)
		previous = &method
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		actions := plan.Spec.Actions()
		if len(actions) == 0 || len(actions) > flooring.MaxCellsPerPlan {
			return fmt.Errorf("flooring plan %s has %d actions", method.Plan, len(actions))
		}
		batch := map[domain.Cell]bool{}
		for _, action := range actions {
			b, ok := action.Building()
			if !ok {
				return fmt.Errorf("flooring plan action is not a building: %#v", action)
			}
			if !allowed[b.Definition()] || definition != "" && b.Definition() != definition {
				return fmt.Errorf("flooring admitted %s; expected one floor from %v", b.Definition(), flooring.Floors)
			}
			definition = b.Definition()
			if !interior.contains(b.Cell()) || batch[b.Cell()] {
				return fmt.Errorf("floor placed at %v: not a unique interior cell of %+v", b.Cell(), interior)
			}
			batch[b.Cell()] = true
		}
		doneCtx, doneCancel := context.WithTimeout(ctx, 10*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return fmt.Errorf("flooring plan: %w", err)
		}
		if incidental {
			renewals++
			report["incidental_renewals"] = renewals
			continue
		}
		for cell := range batch {
			if laid[cell] != "" {
				return fmt.Errorf("floor at %v ordered twice (%s then %s)", cell, laid[cell], definition)
			}
			laid[cell] = definition
		}
		plans = append(plans, string(method.Plan))
		report["flooring_plans"] = plans
		report["flooring_completed_tick"] = int64(state.Progress[0].View().Tick)
		report["cells_laid"] = len(laid)
		// The measured census releases the latch once no cell reads
		// deficient; a bounded wait tells a partial batch from a stall.
		releaseCtx, releaseCancel := context.WithTimeout(ctx, 4*time.Minute)
		releasedReview, err := waitRelease(releaseCtx, journal, service, roomKey)
		releaseCancel()
		if err == nil {
			released = true
			report["released_review_revision"] = releasedReview.Revision
		} else if len(laid) >= interior.area() {
			return fmt.Errorf("flooring release: %w", err)
		}
	}
	if !released {
		return fmt.Errorf("room %s still latched after %d plans laid %d cells", roomKey, len(plans), len(laid))
	}
	report["floor_definition"] = definition
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
	after, err := readFlooring(ctx, h, identity, "flooring-after", interior)
	if err != nil {
		return err
	}
	report["flooring_after"] = after.evidence()
	if len(after.cells) != interior.area() || after.deficient() != 0 || after.pending != 0 {
		return fmt.Errorf("flooring-after: %d of %d interior cells still deficient, %d pending", after.deficient(), len(after.cells), after.pending)
	}
	for cell, row := range after.cells {
		if row.terrain != definition || laid[cell] != definition {
			return fmt.Errorf("flooring-after: cell %v reads %s; the admitted %s was laid on %v", cell, row.terrain, definition, laid[cell])
		}
	}
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

type cellRect struct{ minX, minZ, maxX, maxZ int32 }

func (r cellRect) contains(c domain.Cell) bool {
	return c.X >= r.minX && c.X <= r.maxX && c.Z >= r.minZ && c.Z <= r.maxZ
}
func (r cellRect) area() int { return int(r.maxX-r.minX+1) * int(r.maxZ-r.minZ+1) }

type floorCellRow struct {
	terrain     string
	cleanliness float64
	natural     bool
	pending     string
}
type flooringSummary struct {
	roomID, role string
	cells        map[domain.Cell]floorCellRow
	pending      int
}

// deficient counts the interior cells short of the clean tier: negative
// terrain cleanliness, the kitchen's requirement.
func (s flooringSummary) deficient() int {
	n := 0
	for _, row := range s.cells {
		if row.cleanliness < 0 {
			n++
		}
	}
	return n
}

func (s flooringSummary) evidence() map[string]any {
	keys := make([]domain.Cell, 0, len(s.cells))
	for c := range s.cells {
		keys = append(keys, c)
	}
	sort.Slice(keys, func(i, j int) bool { return keys[i].Z < keys[j].Z || keys[i].Z == keys[j].Z && keys[i].X < keys[j].X })
	cells := make([]map[string]any, 0, len(keys))
	for _, c := range keys {
		row := s.cells[c]
		cells = append(cells, map[string]any{"x": c.X, "z": c.Z, "terrain": row.terrain, "cleanliness": row.cleanliness, "natural": row.natural, "pending": row.pending})
	}
	return map[string]any{"room_id": s.roomID, "role": s.role, "cells": cells, "pending": s.pending, "deficient": s.deficient()}
}

// readFlooring decodes the typed upkeep flooring section the way the Go
// projection does, keeping the room that holds the fixture interior with
// each of its interior cells joined to its terrain's census stats.
func readFlooring(ctx context.Context, h *na.Harness, identity map[string]any, label string, interior cellRect) (flooringSummary, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return flooringSummary{}, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return flooringSummary{}, err
	}
	upkeepSection, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(upkeepSection, "observed")
	if err != nil {
		return flooringSummary{}, fmt.Errorf("%s: upkeep unavailable: %w", label, err)
	}
	for _, raw := range na.AsSlice(upkeep["issues"]) {
		issue, _ := na.AsMap(raw)
		if na.AsString(issue["field"]) == "flooring" {
			return flooringSummary{}, fmt.Errorf("%s: flooring census unavailable: %#v", label, issue)
		}
	}
	section, _ := na.AsMap(upkeep["flooring"])
	_, facts, err := na.Outcome(section, "observed")
	if err != nil {
		return flooringSummary{}, fmt.Errorf("%s: flooring section unavailable: %w", label, err)
	}
	type terrainRow struct {
		cleanliness float64
		natural     bool
	}
	terrains := map[string]terrainRow{}
	for _, raw := range na.AsSlice(facts["terrains"]) {
		row, _ := na.AsMap(raw)
		natural, _ := na.AsBool(row["natural"])
		terrains[na.AsString(row["defName"])] = terrainRow{cleanliness: na.AsNumber(row["cleanliness"]), natural: natural}
	}
	for _, raw := range na.AsSlice(facts["rooms"]) {
		room, _ := na.AsMap(raw)
		s := flooringSummary{roomID: na.AsString(room["roomId"]), role: na.AsString(room["role"]), cells: map[domain.Cell]floorCellRow{}}
		for _, rawCell := range na.AsSlice(room["cells"]) {
			row, _ := na.AsMap(rawCell)
			cell, _ := na.AsMap(row["cell"])
			c := domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))}
			if !interior.contains(c) {
				continue
			}
			name := na.AsString(row["terrain"])
			t, ok := terrains[name]
			if !ok {
				return flooringSummary{}, fmt.Errorf("%s: cell %v names terrain %q missing from the census table", label, c, name)
			}
			pending := na.AsString(row["pending"])
			if pending != "" {
				s.pending++
			}
			s.cells[c] = floorCellRow{terrain: name, cleanliness: t.cleanliness, natural: t.natural, pending: pending}
		}
		if len(s.cells) > 0 {
			return s, nil
		}
	}
	return flooringSummary{}, fmt.Errorf("%s: no flooring room holds the fixture interior %+v", label, interior)
}

func latchedOn(review store.RoutineReview, key string) bool {
	for _, k := range review.Latches.Flooring {
		if k == key {
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

func waitLatch(ctx context.Context, s *store.Store, service *na.ServiceProcess, key string) (store.RoutineReview, error) {
	review, err := na.WaitReview(ctx, s, storeWait(service), func(r store.RoutineReview) bool {
		if !latchedOn(r, key) {
			return false
		}
		for _, binding := range r.Goals {
			if binding.Need == policy.MaintainFlooring {
				return true
			}
		}
		return false
	})
	if err != nil {
		return review, fmt.Errorf("review never latched room %s with a bound MaintainFlooring goal (revision %d, latches %+v): %w", key, review.Revision, review.Latches.Flooring, err)
	}
	return review, nil
}

func waitRelease(ctx context.Context, s *store.Store, service *na.ServiceProcess, key string) (store.RoutineReview, error) {
	review, err := na.WaitReview(ctx, s, storeWait(service), func(r store.RoutineReview) bool { return !latchedOn(r, key) })
	if err != nil {
		return review, fmt.Errorf("review never released the flooring latch on room %s (revision %d): %w", key, review.Revision, err)
	}
	return review, nil
}
