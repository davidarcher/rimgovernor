package shelter

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// bunkWood is the WoodLog dropped beside the sleeping spots once they are
// placed: the baseline's 500 covers eight wooden beds (45 each) but not
// the hut ring after them, and the frames would otherwise hold for wood
// the colony has no routine to cut here.
const bunkWood = 200

func init() {
	cases.Register(cases.Case{
		Name: "shelter/bunks-first",
		Scope: "Issues #612 and #615: the one complete-construction path. From the tribal " + sustained.BaselineSave +
			" baseline, which houses nobody indoors at the start (asserted), the initial shelter places sleeping spots at " +
			"the first review, builds the wooden beds as its first construction and only then raises the whole shell around " +
			"them -- every wall by ordinary pawn work, nothing staged -- no bed on a ring corner; the game roofs the room and " +
			"the native census then holds one bed per colonist inside it.",
		Start:  cases.Save{Name: sustained.BaselineSave},
		Serve:  &cases.ServeSpec{Families: []string{families}, NativeTimeout: 60 * time.Second, Prefix: "bunks"},
		Budget: 25 * time.Minute,
		Reason: "one unstaged run of three sequential construction rungs: eight tribal builders raise eight beds (800 work each) and then a hut ring of some thirty cells, and the game roofs the room after; the ordering is the assertion, so no rung can be staged",
		Run:    bunksFirst,
	})
}

// bunk is one 1x2 bunk a rung placed, with its completion tick.
type bunk struct {
	cells []domain.Cell
	tick  domain.Tick
}

func bunksFirst(ctx context.Context, s cases.Session) error {
	report := s.Report()
	w := waits{build: buildWait, furnish: furnishWait, stall: na.StallBudget()}
	if err := allowSupplies(ctx, s.Harness(), "allow-supplies", report); err != nil {
		return err
	}
	// The precondition must not already satisfy the outcome: the colony
	// starts with no roofed room holding a bed, so every bed the final
	// census counts was built during this run (#615).
	colonists, err := unhousedColony(ctx, s, report)
	if err != nil {
		return err
	}
	service, err := start(ctx, s, nil)
	if err != nil {
		return err
	}
	st, err := na.OpenStoreWithRetry(ctx, service.StatePath)
	if err != nil {
		service.Stop()
		return err
	}
	defer st.Close()
	// 1. Sleeping spots at the first review, before any wall blueprint.
	spots, err := waitBunks(ctx, st, buildingruntime.ShelterSpotsMethod(), "SleepingSpot", false, w.wait(w.build, service))
	if err != nil {
		service.Stop()
		return err
	}
	report["spots"] = describeBunks(spots)
	if err := noShellYet(ctx, st, "sleeping spots"); err != nil {
		service.Stop()
		return err
	}
	// Wood for the ring, beside the site the spots mark out.
	dropped, err := s.Harness().Call(ctx, "drop-wood", "test/hut_shell_fixture", map[string]any{
		"action": "wood", "door": fmt.Sprintf("%d,%d", spots[0].cells[0].X, spots[0].cells[0].Z), "walls": cellArg(bunkCells(spots)), "wood": bunkWood,
	})
	if err != nil {
		service.Stop()
		return err
	}
	report["dropped_wood"] = dropped
	// 2. The beds are the first construction, completed on the site.
	beds, err := waitBunks(ctx, st, buildingruntime.ShelterBedsMethod(), "Bed", true, w.wait(w.build, service))
	if err != nil {
		service.Stop()
		return err
	}
	report["beds"] = describeBunks(beds)
	// 3. The shell is sited around the completed beds.
	sh, err := waitShell(ctx, st, w.wait(w.build, service))
	if err != nil {
		service.Stop()
		return err
	}
	report["shell"] = sh.describe()
	inside := map[domain.Cell]bool{}
	for _, c := range sh.footprint.Interior() {
		inside[c] = true
	}
	corner := map[domain.Cell]bool{}
	for _, c := range policy.ShellCornerCells(sh.footprint) {
		corner[c] = true
	}
	var bedCells []domain.Cell
	for _, b := range beds {
		for _, c := range b.cells {
			if !inside[c] {
				service.Stop()
				return fmt.Errorf("bed cell %v lies outside the sited shell %v", c, sh.footprint.Bounds())
			}
			if corner[c] {
				service.Stop()
				return fmt.Errorf("bed cell %v sits on a ring corner", c)
			}
			bedCells = append(bedCells, c)
		}
	}
	// The ring closes after the beds: no wall completes before the last
	// bed did.
	var lastBed domain.Tick
	for _, b := range beds {
		lastBed = max(lastBed, b.tick)
	}
	if err := waitLineage(ctx, st, sh, w.wait(w.build, service), func(l lineage) bool {
		return len(l.completed) == len(sh.cells)
	}); err != nil {
		service.Stop()
		return err
	}
	l, err := shellLineage(ctx, st, sh)
	if err != nil {
		service.Stop()
		return err
	}
	var firstWall domain.Tick
	for _, plan := range l.byID {
		for _, p := range plan.Progress {
			if v := p.View(); v.Stage == domain.Completed && (firstWall == 0 || v.Tick < firstWall) {
				firstWall = v.Tick
			}
		}
	}
	report["last_bed_tick"], report["first_wall_tick"] = lastBed, firstWall
	if firstWall < lastBed {
		service.Stop()
		return fmt.Errorf("a wall completed at tick %d before the last bed at %d", firstWall, lastBed)
	}
	report["keepalive"] = service.Stop()
	// 4. Roofed, every colonist housed: the native room holds a bed per
	// colonist, all inside.
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	if err := verifyNative(ctx, h, s.Identity(), sh, bedCells, report); err != nil {
		return err
	}
	listed, err := h.Call(ctx, "colonists-after", "home/list_pawns", map[string]any{"colonistsOnly": true})
	if err != nil {
		return err
	}
	after := len(na.AsSlice(listed["pawns"]))
	if after != colonists {
		return fmt.Errorf("the colony changed size during the run: %d colonists, %d before", after, colonists)
	}
	// Capacity for this fixture: one bed per colonist, all of them inside
	// the one roofed room verifyNative checked. A roofed room with a bed in
	// it is not shelter for eight tribals.
	housed, _ := report["native_beds"].([]map[string]any)
	report["colonists"], report["housed"] = colonists, len(housed)
	if colonists == 0 || len(housed) < colonists {
		return fmt.Errorf("%d beds in the roofed hut for %d colonists", len(housed), colonists)
	}
	return nil
}

// unhousedColony reads the colony before the controller starts and returns
// its colonist count, refusing a baseline that already meets the outcome:
// no native room may be a proper roofed indoor room holding a bed. Without
// this the run could pass on shelter it never built (#615).
func unhousedColony(ctx context.Context, s cases.Session, report na.Report) (int, error) {
	h := s.Harness()
	reply, err := h.Wire(ctx, "rooms-before", "observations_list_rooms", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return 0, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return 0, err
	}
	rooms, indoorBeds := 0, 0
	for _, raw := range na.AsSlice(observed["rooms"]) {
		row, _ := na.AsMap(raw)
		if !boolOf(row["properRoom"]) || boolOf(row["outdoors"]) || na.AsNumber(row["openRoofCount"]) != 0 {
			continue
		}
		rooms++
		indoorBeds += len(na.AsSlice(row["beds"]))
	}
	listed, err := h.Call(ctx, "colonists-before", "home/list_pawns", map[string]any{"colonistsOnly": true})
	if err != nil {
		return 0, err
	}
	colonists := len(na.AsSlice(listed["pawns"]))
	report["precondition"] = map[string]any{"colonists": colonists, "roofed_rooms": rooms, "indoor_beds": indoorBeds}
	if colonists == 0 {
		return 0, errors.New("the baseline has no colonists to shelter")
	}
	if indoorBeds > 0 {
		return 0, fmt.Errorf("the baseline already holds %d beds in %d roofed rooms: the precondition satisfies the outcome", indoorBeds, rooms)
	}
	return colonists, nil
}

// waitBunks polls the store until the shelter goal binds the named bunk
// rung, and, when complete is set, until every bunk of it is completed.
func waitBunks(ctx context.Context, st *store.Store, method domain.MethodID, definition string, complete bool, w na.Wait) ([]bunk, error) {
	var found []bunk
	var seen string
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := st.LoadRoutineReview(ctx)
		if err != nil {
			return na.Signature("no-review", err), false, nil
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.EnsureInitialShelter {
				continue
			}
			goal, err := st.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			for _, m := range goal.Methods {
				if m.Method != method {
					continue
				}
				plan, err := st.LoadPlan(ctx, m.Plan)
				if err != nil {
					return "", false, err
				}
				if !strings.HasPrefix(string(plan.Spec.ID()), buildingruntime.BunkPlanPrefix+"-") {
					return "", false, fmt.Errorf("%s bound to %s, not a bunk plan", method, plan.Spec.ID())
				}
				var bunks []bunk
				done := true
				for i, a := range plan.Spec.Actions() {
					b, ok := a.Building()
					if !ok || b.Definition() != definition {
						return "", false, fmt.Errorf("%s places %v, want %s", method, a, definition)
					}
					f := policy.BunkFootprint(b.Cell())
					v := plan.Progress[i].View()
					seen = fmt.Sprintf("%s at %v stage %s", plan.Spec.ID(), b.Cell(), v.Stage)
					done = done && v.Stage == domain.Completed
					bunks = append(bunks, bunk{cells: f[:], tick: v.Tick})
				}
				if len(bunks) == 0 {
					return "", false, fmt.Errorf("%s admitted no bunk", method)
				}
				if complete && !done {
					return na.Signature(binding.Goal, len(goal.Methods), seen), false, nil
				}
				found = bunks
				return "", true, nil
			}
			return na.Signature(binding.Goal, len(goal.Methods)), false, nil
		}
		return na.Signature("unbound", len(review.Goals)), false, nil
	})
	if err != nil {
		return nil, fmt.Errorf("%s not admitted for EnsureInitialShelter (last seen: %s): %w", method, seen, err)
	}
	return found, nil
}

// noShellYet fails when any shell plan is on the journal: the ring must
// wait for the bunks.
func noShellYet(ctx context.Context, st *store.Store, after string) error {
	plans, err := st.LoadPlans(ctx, 256)
	if err != nil {
		return err
	}
	for _, plan := range plans {
		if strings.HasPrefix(string(plan.Spec.ID()), "routine-shell-") {
			return fmt.Errorf("shell plan %s admitted before the %s", plan.Spec.ID(), after)
		}
	}
	return nil
}

func bunkCells(bunks []bunk) []domain.Cell {
	var cells []domain.Cell
	for _, b := range bunks {
		cells = append(cells, b.cells...)
	}
	return cells
}

func describeBunks(bunks []bunk) []map[string]any {
	out := make([]map[string]any, 0, len(bunks))
	for _, b := range bunks {
		out = append(out, map[string]any{"cells": b.cells, "tick": b.tick})
	}
	return out
}
