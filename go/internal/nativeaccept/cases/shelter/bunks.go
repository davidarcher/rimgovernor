package shelter

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// bunkWood is the WoodLog dropped beside a colonist before the service
// starts: the baseline's 500 covers eight wooden beds (45 each) but not
// the hut ring after them, and the frames would otherwise hold for wood
// the colony has no routine to cut here.
const bunkWood = 200

func init() {
	cases.Register(cases.Case{
		Name: "shelter/bunks-first",
		Scope: "Issues #612, #615 and #2278: the one complete-construction path. From the tribal " + sustained.BaselineSave +
			" baseline, which houses nobody indoors at the start (asserted), the initial shelter places sleeping spots at " +
			"the first review on the planned shelter interior, admits the wooden beds next on the same slots and raises the ring (the planned room's waves) around them without waiting for the beds " +
			"to stand (#641) -- every wall and the door by ordinary pawn work, nothing staged; " +
			"the game roofs the room and the native census then holds one bed per colonist inside it.",
		Start:  cases.Save{Name: sustained.BaselineSave},
		Serve:  &cases.ServeSpec{Families: families, NativeTimeout: 60 * time.Second, Prefix: "bunks"},
		Budget: 25 * time.Minute,
		Crew:   cases.Crew{Size: 3}, Reason: "one unstaged run of three construction rungs: eight tribal builders raise eight beds (800 work each) and a hut ring of some thirty cells side by side, and the game roofs the room after; the rungs' layout and progress are the assertion, so no rung can be staged",
		Run: bunksFirst,
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
	// census counts was built during this run.
	colonists, beside, err := unhousedColony(ctx, s, report)
	if err != nil {
		return err
	}
	// Wood for the ring, dropped before the service takes the sole GABP
	// slot (no fixture op can run while it holds it): beside a
	// colonist, near where the spots will be sited, since a restart to
	// drop it later would cancel the shelter plan this run asserts on.
	dropped, err := s.Harness().Call(ctx, "drop-wood", "test/hut_shell_fixture", map[string]any{
		"action": "wood", "door": fmt.Sprintf("%d,%d", beside.X, beside.Z), "wood": bunkWood,
	})
	if err != nil {
		return err
	}
	report["dropped_wood"] = dropped
	// The baseline has not researched ComplexFurniture, so the beds rung
	// would be refused and the shell admitted without it.
	furniture, err := s.Harness().Call(ctx, "finish-furniture", "test/hut_shell_fixture", map[string]any{"action": "furniture"})
	if err != nil {
		return err
	}
	report["furniture"] = furniture
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
	// 2. Once the spots stand they are deleted (the native refuses a bed over
	// a standing spot), then the beds are admitted on the freed slots;
	// the shell is sited around them whether or not they stand yet.
	deleted, err := waitSpotsCleared(ctx, st, w.wait(w.build, service))
	if err != nil {
		service.Stop()
		return err
	}
	report["spots_deleted"] = deleted
	placed, err := waitBunks(ctx, st, buildingruntime.ShelterBedsMethod(), "Bed", false, w.wait(w.build, service))
	if err != nil {
		service.Stop()
		return err
	}
	if len(placed) != len(spots) {
		service.Stop()
		return fmt.Errorf("%d beds placed on %d spot slots", len(placed), len(spots))
	}
	for i := range placed {
		if fmt.Sprint(placed[i].cells) != fmt.Sprint(spots[i].cells) {
			service.Stop()
			return fmt.Errorf("bed %d on %v, not on the freed spot slot %v", i, placed[i].cells, spots[i].cells)
		}
	}
	sh, err := waitShell(ctx, st, w.wait(w.build, service))
	if err != nil {
		service.Stop()
		return err
	}
	report["shell"] = sh.describe()
	// The spots and beds were furnished from the plan before any wall: every
	// one stands on the planned interior the ring is raised around.
	planned := map[domain.Cell]bool{}
	for _, c := range sh.footprint.Interior() {
		planned[c] = true
	}
	for _, rung := range [][]bunk{spots, placed} {
		for _, b := range rung {
			for _, c := range b.cells {
				if !planned[c] {
					service.Stop()
					return fmt.Errorf("bunk cell %v lies outside the planned shelter interior %v", c, sh.footprint.Bounds())
				}
			}
		}
	}
	// 3. Every wall, the door and every bed is placed (built natively below).
	if err := waitLineage(ctx, st, sh, w.wait(w.build, service), func(l lineage) bool {
		return len(l.completed) == len(sh.cells)
	}); err != nil {
		service.Stop()
		return err
	}
	beds, err := waitBunks(ctx, st, buildingruntime.ShelterBedsMethod(), "Bed", true, w.wait(w.build, service))
	if err != nil {
		service.Stop()
		return err
	}
	report["beds"] = describeBunks(beds)
	inside := map[domain.Cell]bool{}
	for _, c := range sh.footprint.Interior() {
		inside[c] = true
	}
	var bedCells []domain.Cell
	for _, b := range beds {
		for _, c := range b.cells {
			if !inside[c] {
				service.Stop()
				return fmt.Errorf("bed cell %v lies outside the sited shell %v", c, sh.footprint.Bounds())
			}
			bedCells = append(bedCells, c)
		}
	}
	report["keepalive"] = service.Stop()
	// 4. Roofed, every colonist housed: the native room holds a bed per
	// colonist, all inside.
	h, err := s.Reattach(ctx)
	if err != nil {
		return err
	}
	if err := waitBuilt(ctx, h, s.Identity(), sh, bedCells, report); err != nil {
		return err
	}
	if err := verifyNative(ctx, h, s.Identity(), sh, bedCells, report); err != nil {
		return err
	}
	listed, err := na.ListColonists(ctx, h, "colonists-after", false, false)
	if err != nil {
		return err
	}
	after := len(listed)
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
// its colonist count and the first colonist's cell, refusing a baseline that already meets the outcome:
// no native room may be a proper roofed indoor room holding a bed. Without
// this the run could pass on shelter it never built.
func unhousedColony(ctx context.Context, s cases.Session, report na.Report) (int, domain.Cell, error) {
	h := s.Harness()
	reply, err := h.Wire(ctx, "rooms-before", "observations_list_rooms", map[string]any{"scope": map[string]any{"expectedIdentity": s.Identity()}})
	if err != nil {
		return 0, domain.Cell{}, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return 0, domain.Cell{}, err
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
	listed, err := na.ListColonists(ctx, h, "colonists-before", false, false)
	if err != nil {
		return 0, domain.Cell{}, err
	}
	colonists := len(listed)
	report["precondition"] = map[string]any{"colonists": colonists, "roofed_rooms": rooms, "indoor_beds": indoorBeds}
	if colonists == 0 {
		return 0, domain.Cell{}, errors.New("the baseline has no colonists to shelter")
	}
	if indoorBeds > 0 {
		return 0, domain.Cell{}, fmt.Errorf("the baseline already holds %d beds in %d roofed rooms: the precondition satisfies the outcome", indoorBeds, rooms)
	}
	first, _ := na.AsMap(listed[0]["pawn"])
	position, _ := na.AsMap(first["position"])
	return colonists, domain.Cell{X: int32(na.AsNumber(position["x"])), Z: int32(na.AsNumber(position["z"]))}, nil
}

// waitBunks polls the store until the shelter goal has bound the named
// bunk rung (live or already retired), and, when complete is set,
// until every bunk of it is completed.
func waitBunks(ctx context.Context, st *store.Store, method domain.MethodID, definition string, complete bool, w na.Wait) ([]bunk, error) {
	var found []bunk
	var seen string
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := st.LoadRounds(ctx)
		if err != nil {
			return na.Signature("no-review", err), false, nil
		}
		for _, binding := range review.Standards {
			if binding.Concern != policy.MaintainHousing {
				continue
			}
			// The latest binding in any epoch, retired or not: a bunk rung
			// completes on its placement receipt and retires at once, so
			// the goal's live methods no longer list it.
			id, err := st.LatestMethodPlan(ctx, binding.Standard, method)
			if errors.Is(err, store.ErrNotFound) {
				return na.Signature(binding.Standard, "unbound"), false, nil
			}
			if err != nil {
				return "", false, err
			}
			plan, err := st.LoadPlan(ctx, id)
			if err != nil {
				return "", false, err
			}
			var bunks []bunk
			done := true
			for i, a := range plan.Spec.Actions() {
				b, ok := a.Building()
				if !ok || b.Definition() != definition {
					return "", false, fmt.Errorf("%s places %v, want %s", method, a, definition)
				}
				f := policy.BunkCells(b.Cell(), b.Rotation())
				v := plan.Progress[i].View()
				seen = fmt.Sprintf("%s at %v stage %s", plan.Spec.ID(), b.Cell(), v.Stage)
				done = done && v.Stage == domain.Completed
				bunks = append(bunks, bunk{cells: f, tick: v.Tick})
			}
			if len(bunks) == 0 {
				return "", false, fmt.Errorf("%s admitted no bunk", method)
			}
			if complete && !done {
				return na.Signature(binding.Standard, seen), false, nil
			}
			found = bunks
			return "", true, nil
		}
		return na.Signature("unbound", len(review.Standards)), false, nil
	})
	if err != nil {
		return nil, fmt.Errorf("%s not admitted for MaintainHousing (last seen: %s): %w", method, seen, err)
	}
	return found, nil
}

// noShellYet fails when any shell plan is on the journal: the ring must
// wait for the bunks.
func noShellYet(ctx context.Context, st *store.Store, after string) error {
	plans, err := st.LoadPlans(ctx)
	if err != nil {
		return err
	}
	for _, plan := range plans {
		if buildingruntime.IsShellMethod(plan.Method) {
			return fmt.Errorf("shell plan %s admitted before the %s", plan.Spec.ID(), after)
		}
	}
	return nil
}

// waitSpotsCleared polls the store until the shelter goal has bound the
// spot deletion the bed rung runs first and returns the cells of
// the spots it deletes: the native refuses a bed over a standing spot, so
// the beds go on the freed slots.
func waitSpotsCleared(ctx context.Context, st *store.Store, w na.Wait) ([]domain.Cell, error) {
	var cells []domain.Cell
	method := buildingruntime.ShelterClearBedsMethod()
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := st.LoadRounds(ctx)
		if err != nil {
			return na.Signature("no-review", err), false, nil
		}
		for _, binding := range review.Standards {
			if binding.Concern != policy.MaintainHousing {
				continue
			}
			id, err := st.LatestMethodPlan(ctx, binding.Standard, method)
			if errors.Is(err, store.ErrNotFound) {
				return na.Signature(binding.Standard, "unbound"), false, nil
			}
			if err != nil {
				return "", false, err
			}
			plan, err := st.LoadPlan(ctx, id)
			if err != nil {
				return "", false, err
			}
			for _, a := range plan.Spec.Actions() {
				cut, ok := a.Deconstruction()
				if !ok || cut.Definition() != "SleepingSpot" {
					return "", false, fmt.Errorf("%s places %v, want a SleepingSpot deletion", method, a)
				}
				cells = append(cells, cut.Cell())
			}
			return "", true, nil
		}
		return na.Signature("unbound", len(review.Standards)), false, nil
	})
	if err != nil {
		return nil, fmt.Errorf("%s not admitted for MaintainHousing: %w", method, err)
	}
	return cells, nil
}

func describeBunks(bunks []bunk) []map[string]any {
	out := make([]map[string]any, 0, len(bunks))
	for _, b := range bunks {
		out = append(out, map[string]any{"cells": b.cells, "tick": b.tick})
	}
	return out
}
