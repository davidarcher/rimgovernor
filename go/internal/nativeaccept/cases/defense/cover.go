package defense

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

// defense/cover (#581): on the committed layout the fixture stages raider
// cover ahead of Entry inside the firing line's engagement zone (three
// mature oaks and two granite chunks, ordinary map things), the service is
// pointed at the complete layout, and the layout planner must recompute the
// approaches from the fresh census, identify every staged thing as cover
// and order its clearance (cut for the trees, haul for the chunks) as one
// cover_clearance method under EnsureDefensiveLayout. The run waits for the
// method's plan to settle with every clearance completed, then asserts
// natively that no staged thing still stands on its cell.
const coverTimeout = 15 * time.Minute

// coverPrefix is the layout planner's cover method prefix
// (buildingruntime.defenseCoverPrefix).
const coverPrefix = "defense-cover-"

func awayFromHome(toward domain.Rotation) (int, int) {
	switch toward {
	case domain.North:
		return 0, -1
	case domain.East:
		return -1, 0
	case domain.South:
		return 0, 1
	}
	return 1, 0
}

func runCover(ctx context.Context, closeClient func() error, reopenFixture func() error,
	fixture func(string, map[string]any) (map[string]any, error), launch func(string) (*service, error),
	layout store.DefenseLayoutRecord, report na.Report) error {
	// The checkpoint carries a wound that creeps into a CriticalMedical
	// hold, which suspends the priority-3 layout goal; the colony is healed
	// before the cover is staged, as the hostile-building cases do.
	healed, err := fixture("heal-before-cover", map[string]any{"op": "heal"})
	if err != nil {
		return err
	}
	report["healed_before_cover"] = healed
	dx, dz := awayFromHome(layout.Toward)
	staged, err := fixture("cover", map[string]any{"op": "cover", "x": int(layout.Entry.X), "z": int(layout.Entry.Z), "dx": dx, "dz": dz})
	if err != nil {
		return err
	}
	report["cover_staged"] = staged
	want := map[string]string{}
	stagedAt := map[string][2]int64{}
	for _, row := range na.AsSlice(staged["cover"]) {
		m, _ := na.AsMap(row)
		want[na.AsString(m["id"])] = na.AsString(m["def"])
		x := int64(na.AsNumber(m["x"]))
		z := int64(na.AsNumber(m["z"]))
		stagedAt[na.AsString(m["id"])] = [2]int64{x, z}
	}
	if len(want) != 5 {
		return fmt.Errorf("fixture staged %d cover things, want 5: %#v", len(want), staged)
	}
	if err := closeClient(); err != nil {
		return err
	}
	svc, err := launch("cover")
	if err != nil {
		return err
	}
	defer svc.stop()
	method, err := waitCoverMethod(ctx, svc.store, svc.wait(coverTimeout), report)
	if err != nil {
		return err
	}
	cleared, err := waitCoverCleared(ctx, svc.store, method, want, svc.wait(coverTimeout))
	report["cover_plans"] = cleared
	if err != nil {
		return err
	}
	svc.stop()
	report["cover_authority"] = svc.keepAlive.snapshot()
	if err := reopenFixture(); err != nil {
		return err
	}
	after, err := fixture("inspect-after-cover", map[string]any{"op": "inspect"})
	if err != nil {
		return err
	}
	report["inspect_after_cover"] = after
	// A cut tree is destroyed; a hauled chunk still exists in the dump
	// stockpile, so the assertion is that nothing stands on its staged cell.
	var standing []string
	for _, row := range na.AsSlice(after["cover"]) {
		m, _ := na.AsMap(row)
		destroyed, _ := na.AsBool(m["destroyed"])
		spawned, _ := na.AsBool(m["spawned"])
		x := int64(na.AsNumber(m["x"]))
		z := int64(na.AsNumber(m["z"]))
		if !destroyed && spawned && stagedAt[na.AsString(m["id"])] == [2]int64{x, z} {
			standing = append(standing, na.AsString(m["id"]))
		}
	}
	if len(standing) > 0 {
		return fmt.Errorf("staged cover still standing after clearance: %v", standing)
	}
	if drafted := draftedColonists(after); len(drafted) > 0 {
		return fmt.Errorf("colonists left drafted: %v", drafted)
	}
	return nil
}

// waitCoverMethod polls the journal for the layout goal's first cover
// clearance method.
func waitCoverMethod(ctx context.Context, s *store.Store, w na.Wait, report na.Report) (domain.GoalMethod, error) {
	var found domain.GoalMethod
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		review, err := s.LoadRoutineReview(ctx)
		if err != nil {
			return na.Signature("no-review"), false, nil
		}
		for _, binding := range review.Goals {
			if binding.Need != policy.EnsureDefensiveLayout {
				continue
			}
			goal, err := s.LoadGoal(ctx, binding.Goal)
			if err != nil && !errors.Is(err, store.ErrNotFound) {
				return "", false, err
			}
			var methods []string
			for _, m := range goal.Methods {
				methods = append(methods, string(m.Method))
				if strings.HasPrefix(string(m.Method), coverPrefix) {
					report["cover_goal"] = string(binding.Goal)
					report["cover_method"] = string(m.Method)
					found = m
					return "", true, nil
				}
			}
			return na.Signature("bound", binding.Goal, goal.Goal.Epoch, methods), false, nil
		}
		return na.Signature("unbound", len(review.Goals)), false, nil
	})
	if err != nil {
		return domain.GoalMethod{}, fmt.Errorf("no cover clearance method: %w", err)
	}
	return found, nil
}

// waitCoverCleared waits until every staged thing has a completed
// clearance under the goal's cover methods, each with the designation its
// kind takes. The map's own cover inside the engagement zone rides along in
// the same batches and is not asserted. The first method found is followed
// by its plan: once the deficit closes the goal recovers and retires its
// methods, so the goal's list alone would lose the plan that did the work.
func waitCoverCleared(ctx context.Context, s *store.Store, first domain.GoalMethod, want map[string]string, w na.Wait) (map[string]any, error) {
	var out map[string]any
	err := na.WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		goal, err := s.LoadGoal(ctx, first.Goal)
		if err != nil && !errors.Is(err, store.ErrNotFound) {
			return "", false, err
		}
		plans := map[domain.PlanID]domain.GoalMethod{first.Plan: first}
		for _, m := range goal.Methods {
			if strings.HasPrefix(string(m.Method), coverPrefix) {
				plans[m.Plan] = m
			}
		}
		ids := make([]string, 0, len(plans))
		for id := range plans {
			ids = append(ids, string(id))
		}
		sort.Strings(ids)
		staged := map[string]string{}
		var methods []string
		var signature []string
		for _, id := range ids {
			m := plans[domain.PlanID(id)]
			methods = append(methods, string(m.Method))
			state, err := s.LoadPlan(ctx, m.Plan)
			if err != nil {
				return "", false, err
			}
			signature = append(signature, na.PlanSignature(state))
			for _, p := range state.Progress {
				clearance, ok := p.Action().CoverClearance()
				if !ok {
					return "", false, fmt.Errorf("cover plan %s carries a %s action", m.Plan, p.Action().Kind())
				}
				def, isStaged := want[clearance.Thing()]
				if !isStaged {
					continue
				}
				wantDesignation := domain.CoverClearanceCutPlant
				if strings.HasPrefix(def, "Chunk") {
					wantDesignation = domain.CoverClearanceHaul
				}
				if clearance.Definition() != def || clearance.Designation() != wantDesignation {
					return "", false, fmt.Errorf("cover plan orders %s on %s (%s), want %s on %s", clearance.Designation(), clearance.Thing(), clearance.Definition(), wantDesignation, def)
				}
				v := p.View()
				if v.Stage == domain.Unsuccessful || v.Stage == domain.Cancelled {
					return "", false, fmt.Errorf("clearance of %s settled %s: %v", clearance.Thing(), v.Stage, stages(state.Progress))
				}
				staged[clearance.Thing()] = string(v.Stage)
			}
		}
		out = map[string]any{"methods": methods, "staged": staged}
		done := len(staged) == len(want)
		for _, stage := range staged {
			done = done && stage == string(domain.Completed)
		}
		return na.Signature("cover", methods, staged, signature), done, nil
	})
	if err != nil {
		return out, fmt.Errorf("cover not cleared: %w", err)
	}
	return out, nil
}

func draftedColonists(inspect map[string]any) []string {
	var drafted []string
	for _, row := range na.AsSlice(inspect["colonists"]) {
		m, _ := na.AsMap(row)
		if d, _ := na.AsBool(m["drafted"]); d {
			drafted = append(drafted, na.AsString(m["id"]))
		}
	}
	return drafted
}
