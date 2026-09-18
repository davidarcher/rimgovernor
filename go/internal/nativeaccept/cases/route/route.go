// Package route holds the MaintainRoutes vertical (issue #6 slice 5): a
// live game and a live rimgovernor Go player-control service composed with
// the routes family. A stockpile zone sits in a roofed room walled on every
// side with no door. The native reachability census (the game's own pathing
// per colonist, never a flood fill) must read it unreachable and list
// breach walls; the service must latch the facility, admit exactly one door
// on a listed breach wall, the colonists build it, and the next measured
// census must read the stockpile reachable with a measured path and release
// the latch. An independent native read then confirms the reachability and
// that observed traffic samples exist (actual travel evidence), reporting
// the door cell's own.
//
// Uses the private disposable test/routes_prepare fixture
// (RoutesFixture.cs). The case's own bridge session and the service's are
// used sequentially, never concurrently (one GABP client per game).
package route

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

const prefix = "route-accept"

func init() {
	cases.Register(cases.Case{
		Name: "route/stockpile",
		Scope: "Native MaintainRoutes vertical: a walled-in stockpile the native reachability census reads unreachable " +
			"drives the live Go routine reviewer/planner to admit exactly one door on a listed breach wall; the colonists " +
			"build it and the measured census, not the receipt, releases the latch, confirmed by an independent native read " +
			"with observed traffic samples.",
		Start:   cases.Fixture{Op: "test/routes_prepare"},
		Service: true,
		Budget:  6 * time.Minute,
		Run:     run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity, prepared := s.Harness(), s.Identity(), s.Prepared()
	var service *na.ServiceProcess
	facility := ""
	// Failure evidence: the same independent routes read a pass ends with.
	defer func() {
		if _, hasAfter := report["routes_after"]; hasAfter {
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
		if after, err := readRoutes(stopCtx, ph, identity, "routes-postmortem", facility); err == nil {
			report["routes_postmortem"] = after.evidence()
		} else {
			report["routes_postmortem_error"] = err.Error()
		}
	}()
	if _, err := na.ConfirmColonyNames(ctx, h, report); err != nil {
		return err
	}
	facility = na.AsString(prepared["facility"])
	rect, _ := na.AsMap(prepared["room"])
	room := cellRect{minX: int32(na.AsNumber(rect["minX"])), minZ: int32(na.AsNumber(rect["minZ"])), maxX: int32(na.AsNumber(rect["maxX"])), maxZ: int32(na.AsNumber(rect["maxZ"]))}
	rect, _ = na.AsMap(prepared["interior"])
	interior := cellRect{minX: int32(na.AsNumber(rect["minX"])), minZ: int32(na.AsNumber(rect["minZ"])), maxX: int32(na.AsNumber(rect["maxX"])), maxZ: int32(na.AsNumber(rect["maxZ"]))}
	colonists := int(na.AsNumber(prepared["colonists"]))
	routes := policy.DefaultRoutesPolicy()

	// Before: the typed routes census must list the stockpile with every
	// mobile colonist's reachability false and at least one breach wall on
	// the room's border -- the exact facts the review latches on.
	before, err := readRoutes(ctx, h, identity, "routes-before", facility)
	if err != nil {
		return err
	}
	report["routes_before"] = before.evidence()
	if before.kind != "stockpile" || len(before.travel) != colonists || before.reachable() != 0 {
		return fmt.Errorf("routes-before: %s reads kind %s, %d of %d travel rows, %d reachable", facility, before.kind, len(before.travel), colonists, before.reachable())
	}
	if len(before.breaches) == 0 {
		return fmt.Errorf("routes-before: %s lists no breach wall", facility)
	}
	for _, b := range before.breaches {
		if !room.contains(b.cell) || interior.contains(b.cell) || b.edifice != "Wall" {
			return fmt.Errorf("routes-before: breach %+v is not a wall of the fixture room %+v", b, room)
		}
	}

	// "work" rides along because every building method's builder check
	// requires the colony's work priorities to match the controller's own
	// assignment, which only the work family applies.
	service, err = s.Launch(ctx, na.ServiceLaunch{Families: []string{"routes", "work"}, Extra: na.ClockSpeedArgs()})
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

	// The review must latch the facility and bind MaintainRoutes.
	waitCtx, waitCancel := context.WithTimeout(ctx, 3*time.Minute)
	latched, err := waitLatch(waitCtx, journal, service, facility)
	waitCancel()
	if err != nil {
		return fmt.Errorf("routes latch: %w", err)
	}
	report["latched_review_revision"] = latched.Revision

	// The routes method: one door of the policy's material on a breach wall
	// of the room, never inside it. Incidental cancellations renew the
	// method; the measured census releases the facility once reachable.
	var previous *domain.GoalMethod
	var plans []string
	var door domain.Cell
	released := false
	for renewals := 0; !released && len(plans) < 4; {
		methodCtx, methodCancel := context.WithTimeout(ctx, 8*time.Minute)
		goalID, method, err := na.WaitGoalMethod(methodCtx, journal, policy.MaintainRoutes, previous)
		methodCancel()
		if err != nil {
			return fmt.Errorf("routes method: %w", err)
		}
		report["goal_id"] = string(goalID)
		previous = &method
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return err
		}
		actions := plan.Spec.Actions()
		if len(actions) != 1 {
			return fmt.Errorf("routes plan %s has %d actions; expected one door", method.Plan, len(actions))
		}
		b, ok := actions[0].Building()
		if !ok || b.Definition() != routes.Door || b.Stuff() != routes.Stuff {
			return fmt.Errorf("routes admitted %#v; expected a %s of %s", actions[0], routes.Door, routes.Stuff)
		}
		listed := false
		for _, breach := range before.breaches {
			listed = listed || breach.cell == b.Cell()
		}
		if !listed || !room.contains(b.Cell()) || interior.contains(b.Cell()) {
			return fmt.Errorf("door placed at %v: not a listed breach wall of %+v (breaches %+v)", b.Cell(), room, before.breaches)
		}
		door = b.Cell()
		doneCtx, doneCancel := context.WithTimeout(ctx, 10*time.Minute)
		state, incidental, err := na.WaitPlanTerminal(doneCtx, journal, method.Plan)
		doneCancel()
		if err != nil {
			return fmt.Errorf("routes plan: %w", err)
		}
		if incidental {
			renewals++
			report["incidental_renewals"] = renewals
			continue
		}
		plans = append(plans, string(method.Plan))
		report["routes_plans"] = plans
		report["routes_completed_tick"] = int64(state.Progress[0].View().Tick)
		report["door_cell"] = map[string]any{"x": door.X, "z": door.Z}
		releaseCtx, releaseCancel := context.WithTimeout(ctx, 4*time.Minute)
		releasedReview, err := waitRelease(releaseCtx, journal, service, facility)
		releaseCancel()
		if err != nil {
			return fmt.Errorf("routes release: %w", err)
		}
		released = true
		report["released_review_revision"] = releasedReview.Revision
	}
	if !released {
		return fmt.Errorf("facility %s still latched after %d plans", facility, len(plans))
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
	after, err := readRoutes(ctx, h, identity, "routes-after", facility)
	if err != nil {
		return err
	}
	report["routes_after"] = after.evidence()
	if after.reachable() == 0 {
		return fmt.Errorf("routes-after: %s still reads unreachable by every colonist", facility)
	}
	measured := false
	for _, t := range after.travel {
		measured = measured || t.reachable && t.pathCost > 0 && t.pathCells > 0
	}
	if !measured {
		return fmt.Errorf("routes-after: no colonist reports a measured path to %s: %+v", facility, after.travel)
	}
	if len(after.breaches) != 0 {
		return fmt.Errorf("routes-after: a reachable facility still lists breaches: %+v", after.breaches)
	}
	if after.trafficSamples == 0 {
		return fmt.Errorf("routes-after: no observed traffic samples")
	}
	report["door_traffic_samples"] = after.samplesAt(door)
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

type travelRow struct {
	pawn                string
	reachable           bool
	pathCost, pathCells int32
}
type breachRow struct {
	cell             domain.Cell
	edifice, pending string
	distance         int32
}
type routesSummary struct {
	kind, roomID   string
	cell           domain.Cell
	travel         []travelRow
	breaches       []breachRow
	traffic        map[domain.Cell]uint32
	trafficSamples uint32
}

func (s routesSummary) reachable() int {
	n := 0
	for _, t := range s.travel {
		if t.reachable {
			n++
		}
	}
	return n
}

func (s routesSummary) samplesAt(c domain.Cell) uint32 { return s.traffic[c] }

func (s routesSummary) evidence() map[string]any {
	travel := make([]map[string]any, 0, len(s.travel))
	for _, t := range s.travel {
		travel = append(travel, map[string]any{"pawn": t.pawn, "reachable": t.reachable, "path_cost": t.pathCost, "path_cells": t.pathCells})
	}
	breaches := make([]map[string]any, 0, len(s.breaches))
	for _, b := range s.breaches {
		breaches = append(breaches, map[string]any{"x": b.cell.X, "z": b.cell.Z, "edifice": b.edifice, "pending": b.pending, "distance": b.distance})
	}
	keys := make([]domain.Cell, 0, len(s.traffic))
	for c := range s.traffic {
		keys = append(keys, c)
	}
	sort.Slice(keys, func(i, j int) bool {
		return s.traffic[keys[i]] > s.traffic[keys[j]] || s.traffic[keys[i]] == s.traffic[keys[j]] && (keys[i].Z < keys[j].Z || keys[i].Z == keys[j].Z && keys[i].X < keys[j].X)
	})
	traffic := make([]map[string]any, 0, len(keys))
	for _, c := range keys {
		traffic = append(traffic, map[string]any{"x": c.X, "z": c.Z, "samples": s.traffic[c]})
	}
	return map[string]any{"kind": s.kind, "room_id": s.roomID, "cell": map[string]any{"x": s.cell.X, "z": s.cell.Z}, "travel": travel, "reachable": s.reachable(), "breaches": breaches, "traffic": traffic, "traffic_samples": s.trafficSamples}
}

// readRoutes decodes the typed upkeep routes section the way the Go
// projection does, keeping the fixture facility's row and the traffic census.
func readRoutes(ctx context.Context, h *na.Harness, identity map[string]any, label, facility string) (routesSummary, error) {
	reply, err := h.Wire(ctx, label, "observations_read_colony_facts", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "planning": false, "page": map[string]any{"limit": 256},
	})
	if err != nil {
		return routesSummary{}, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return routesSummary{}, err
	}
	upkeepSection, _ := na.AsMap(observed["upkeep"])
	_, upkeep, err := na.Outcome(upkeepSection, "observed")
	if err != nil {
		return routesSummary{}, fmt.Errorf("%s: upkeep unavailable: %w", label, err)
	}
	for _, raw := range na.AsSlice(upkeep["issues"]) {
		issue, _ := na.AsMap(raw)
		if na.AsString(issue["field"]) == "routes" {
			return routesSummary{}, fmt.Errorf("%s: routes census unavailable: %#v", label, issue)
		}
	}
	section, _ := na.AsMap(upkeep["routes"])
	_, facts, err := na.Outcome(section, "observed")
	if err != nil {
		return routesSummary{}, fmt.Errorf("%s: routes section unavailable: %w", label, err)
	}
	s := routesSummary{traffic: map[domain.Cell]uint32{}, trafficSamples: uint32(na.AsNumber(facts["trafficSamples"]))}
	for _, raw := range na.AsSlice(facts["traffic"]) {
		row, _ := na.AsMap(raw)
		cell, _ := na.AsMap(row["cell"])
		s.traffic[domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))}] = uint32(na.AsNumber(row["samples"]))
	}
	for _, raw := range na.AsSlice(facts["facilities"]) {
		row, _ := na.AsMap(raw)
		ref, _ := na.AsMap(row["facility"])
		if na.AsString(ref["id"]) != facility {
			continue
		}
		cell, _ := na.AsMap(row["cell"])
		s.kind = na.AsString(row["kind"])
		s.roomID = na.AsString(row["roomId"])
		s.cell = domain.Cell{X: int32(na.AsNumber(cell["x"])), Z: int32(na.AsNumber(cell["z"]))}
		for _, rawTravel := range na.AsSlice(row["travel"]) {
			t, _ := na.AsMap(rawTravel)
			reachable, _ := na.AsBool(t["reachable"])
			s.travel = append(s.travel, travelRow{pawn: na.AsString(t["pawnId"]), reachable: reachable, pathCost: int32(na.AsNumber(t["pathCost"])), pathCells: int32(na.AsNumber(t["pathCells"]))})
		}
		for _, rawBreach := range na.AsSlice(row["breaches"]) {
			b, _ := na.AsMap(rawBreach)
			bc, _ := na.AsMap(b["cell"])
			s.breaches = append(s.breaches, breachRow{cell: domain.Cell{X: int32(na.AsNumber(bc["x"])), Z: int32(na.AsNumber(bc["z"]))}, edifice: na.AsString(b["edifice"]), pending: na.AsString(b["pending"]), distance: int32(na.AsNumber(b["distance"]))})
		}
		return s, nil
	}
	return routesSummary{}, fmt.Errorf("%s: routes census lists no facility %s", label, facility)
}

func latchedOn(review store.RoutineReview, key string) bool {
	for _, k := range review.Latches.Routes {
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
			if binding.Need == policy.MaintainRoutes {
				return true
			}
		}
		return false
	})
	if err != nil {
		return review, fmt.Errorf("review never latched facility %s with a bound MaintainRoutes goal (revision %d, latches %+v): %w", key, review.Revision, review.Latches.Routes, err)
	}
	return review, nil
}

func waitRelease(ctx context.Context, s *store.Store, service *na.ServiceProcess, key string) (store.RoutineReview, error) {
	review, err := na.WaitReview(ctx, s, storeWait(service), func(r store.RoutineReview) bool { return !latchedOn(r, key) })
	if err != nil {
		return review, fmt.Errorf("review never released the routes latch on facility %s (revision %d): %w", key, review.Revision, err)
	}
	return review, nil
}
