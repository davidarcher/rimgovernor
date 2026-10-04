package sleeping

// sleeping/suites (#1221, epic #1200) starts from the same layout/grid
// fixture as sleeping/bedrooms with one hut colonist turned Greedy, on a
// Royalty profile. Phase one: the sleeping planner builds a standard
// bedroom wing, gives the Greedy colonist (whose standard room cannot
// reach slightly impressive on space) a suite and moves them into it.
// Then the fixture grants them Baron, whose bedroom floor (70) needs more
// floor than the suite has. Phase two: a suite never grows (#1951), so the
// planner sites a second, larger suite and shells it, and the first
// suite's plan row stays exactly as it was.

import (
	"context"
	"fmt"
	"path/filepath"
	"slices"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/startersite"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const (
	suiteTitleOp = "test/layout_grid_title"
	// suiteTitle's bedroom floor (70) outgrows a suite sized for Greedy
	// (50): 7x7 then, 7x8 after (layout_suite.go SuiteSize).
	suiteTitle = "Baron"
)

func init() {
	cases.Register(cases.Case{
		Name: "sleeping/suites",
		Scope: "Issue #1221: on the tribal " + sustained.BaselineSave + " colony at Masonry with a fixture hut and a Greedy colonist, " +
			"the sleeping planner builds a standard bedroom wing, gives the Greedy colonist a suite and moves them into it; " +
			"once a Baron title raises their target a second, larger suite is planned and shelled and the first suite " +
			"is left as it is. A snapshot test cannot cover it: the order is proven against real enclosure, roofing and native bed ownership.",
		Start: cases.Fixture{Op: "test/layout_grid_prepare", ArgsFrom: startersite.Args,
			Args: map[string]any{"sleepingSpots": 8, "stoneBlocks": 1500, "builders": true, "greedy": true},
			On:   cases.Save{Name: sustained.BaselineSave}},
		RequiredOps: []string{suiteTitleOp},
		Keep:        []string{string(na.NeedFood)},
		Serve:       &cases.ServeSpec{Families: []string{"shelter", "expansion", "sleeping"}, NativeTimeout: 30 * time.Second, Prefix: "sleeping-suites"},
		Budget:      30 * time.Minute,
		Reason:      "two watches on a Royalty profile: the standard wing goes up one room per review (four rooms in ~236k ticks, 7 min, on the first run) before the suite and move, then the title-driven second suite",
		Run:         suites,
	})
}

// suiteMove is the Greedy colonist's completed move into a suite.
type suiteMove struct {
	assign domain.Assign
	room   policy.LayoutRoom
	// standard counts the completed standard-wing shells before it.
	standard int
}

// completed reports every action of plan completed, and the newest
// completion tick.
func completed(plan store.PlanState) (domain.Tick, bool) {
	var last domain.Tick
	if len(plan.Progress) == 0 || len(plan.Progress) != len(plan.Spec.Actions()) {
		return 0, false
	}
	for _, p := range plan.Progress {
		v := p.View()
		if v.Stage != domain.Completed {
			return 0, false
		}
		last = max(last, v.Tick)
	}
	return last, true
}

// history is the journal's bedroom plans, oldest first.
func history(ctx context.Context, journal *store.Store) ([]store.PlanState, error) {
	plans, err := journal.PlanHistoryWithMethods(ctx, 256, "bedroom-*", "sleeping-assign-*")
	if err != nil {
		return nil, err
	}
	slices.Reverse(plans)
	return plans, nil
}

// roomAt is the wing room of purpose whose interior starts at x,z.
func roomAt(plan policy.LayoutPlan, purpose policy.WingPurpose, x, z int32) (policy.LayoutRoom, bool) {
	for _, w := range plan.Wings {
		if w.Purpose != purpose {
			continue
		}
		for _, r := range w.Rooms {
			if r.Interior.X == x && r.Interior.Z == z {
				return r, true
			}
		}
	}
	return policy.LayoutRoom{}, false
}

// findSuiteMove reads the journal for pawn's completed move into a suite.
func findSuiteMove(ctx context.Context, journal *store.Store, pawn domain.PawnID) (suiteMove, bool, error) {
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return suiteMove{}, false, nil
	}
	layout, laid, err := journal.LayoutPlan(ctx, review.Snapshot, review.Tick)
	if err != nil || !laid {
		return suiteMove{}, false, err
	}
	plans, err := history(ctx, journal)
	if err != nil {
		return suiteMove{}, false, err
	}
	var suite policy.LayoutRoom
	for _, w := range layout.Plan.Wings {
		if w.Purpose == policy.WingSuites && len(w.Rooms) > 0 {
			suite = w.Rooms[0]
		}
	}
	if suite.Role == "" {
		return suiteMove{}, false, nil
	}
	// Every move is a sleeping-assign plan: the pawn's second completed
	// one, out of the standard bed its first gave it, is the suite move.
	standard := 0
	moved := map[string]bool{}
	for _, plan := range plans {
		var x, z int32
		method := string(plan.Method)
		if _, err := fmt.Sscanf(method, "bedroom-shell-%d-%d", &x, &z); err == nil {
			if _, ok := roomAt(layout.Plan, policy.WingBedrooms, x, z); ok {
				if _, done := completed(plan); done {
					standard++
				}
			}
			continue
		}
		if !strings.HasPrefix(method, "sleeping-assign-") {
			continue
		}
		if _, done := completed(plan); !done {
			continue
		}
		for _, a := range plan.Spec.Actions() {
			assign, ok := a.Assign()
			if !ok || assign.Pawn() != pawn {
				continue
			}
			if moved[assign.Previous().ID()] {
				return suiteMove{assign: assign, room: suite, standard: standard}, true, nil
			}
			moved[assign.Thing()] = true
		}
	}
	return suiteMove{}, false, nil
}

func suites(ctx context.Context, s cases.Session) error {
	report := s.Report()
	prepared := s.Prepared()
	report["fixture"] = prepared
	pawn := domain.PawnID(na.AsString(prepared["greedyPawn"]))
	if pawn == "" {
		return fmt.Errorf("fixture turned no colonist Greedy: %#v", prepared)
	}
	path := filepath.Join(s.Config().Output, "service.sqlite")
	var move suiteMove
	var found bool
	// Phase one: the wing, the suite and the move.
	var live *store.Store
	defer func() {
		if live != nil {
			live.Close()
		}
	}()
	_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: 16 * time.Minute, Extra: []policy.GoalID{policy.MaintainHousing}, Until: func(map[string]any) bool {
			if live == nil {
				j, err := store.Open(ctx, path)
				if err != nil {
					return false
				}
				live = j
			}
			m, ok, err := findSuiteMove(ctx, live, pawn)
			return err == nil && ok && m.standard > 0
		}},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			if live != nil {
				live.Close()
				live = nil
			}
			journal, err := store.Open(ctx, path)
			if err != nil {
				return fmt.Errorf("reopen journal: %w", err)
			}
			defer journal.Close()
			if move, found, err = findSuiteMove(ctx, journal, pawn); err != nil {
				return err
			}
			if !found {
				return fmt.Errorf("the Greedy colonist %s was never moved into a suite", pawn)
			}
			if move.standard == 0 {
				return fmt.Errorf("no standard bedroom-wing shell completed before the suite move")
			}
			in := move.room.Interior
			report["suite_move"] = map[string]any{"pawn": string(pawn), "bed": move.assign.Thing(), "previous": move.assign.Previous().ID(),
				"suite": fmt.Sprintf("%d,%d %dx%d", in.X, in.Z, in.Width, in.Height), "standard_shells": move.standard}
			return ownsBed(ctx, h, s.Identity(), pawn, move.assign.Thing(), "after the move")
		},
	})
	if err != nil {
		return err
	}
	// Phase two: the title raises the target; a second suite is planned and
	// shelled, and the first one is left as it is (#1951).
	_, err = sustainedfood.Observe(ctx, s, sustainedfood.Observation{
		WatchConfig: sustainedfood.WatchConfig{Watch: 8 * time.Minute, Extra: []policy.GoalID{policy.MaintainHousing}, Until: func(map[string]any) bool {
			if live == nil {
				j, err := store.Open(ctx, path)
				if err != nil {
					return false
				}
				live = j
			}
			_, ok, err := newSuiteShelled(ctx, live, move.room)
			return err == nil && ok
		}},
		Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
			granted, err := h.Call(ctx, "suite-title", suiteTitleOp, map[string]any{"pawn": string(pawn), "title": suiteTitle})
			if err != nil {
				return err
			}
			report["title"] = granted
			if na.AsString(granted["title"]) != suiteTitle {
				return fmt.Errorf("fixture did not grant %s: %#v", suiteTitle, granted)
			}
			return nil
		},
		Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
			if live != nil {
				live.Close()
				live = nil
			}
			journal, err := store.Open(ctx, path)
			if err != nil {
				return fmt.Errorf("reopen journal: %w", err)
			}
			defer journal.Close()
			if err := auditNewSuite(ctx, journal, report, move); err != nil {
				return err
			}
			return nil
		},
	})
	return err
}

// newSuiteShelled finds a suite other than first whose shell plan
// completed, and the plan's layout.
func newSuiteShelled(ctx context.Context, journal *store.Store, first policy.LayoutRoom) (policy.LayoutRoom, bool, error) {
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return policy.LayoutRoom{}, false, nil
	}
	layout, laid, err := journal.LayoutPlan(ctx, review.Snapshot, review.Tick)
	if err != nil || !laid {
		return policy.LayoutRoom{}, false, err
	}
	plans, err := history(ctx, journal)
	if err != nil {
		return policy.LayoutRoom{}, false, err
	}
	for _, plan := range plans {
		var x, z int32
		if _, err := fmt.Sscanf(string(plan.Method), "bedroom-shell-%d-%d", &x, &z); err != nil || (x == first.Interior.X && z == first.Interior.Z) {
			continue
		}
		if room, ok := roomAt(layout.Plan, policy.WingSuites, x, z); ok {
			if _, done := completed(plan); done {
				return room, true, nil
			}
		}
	}
	return policy.LayoutRoom{}, false, nil
}

// auditNewSuite checks the journal and the plan: a second suite was
// planned and shelled, the first suite's plan row is exactly as it was
// when the colonist moved in, and nothing deepened a suite shell.
func auditNewSuite(ctx context.Context, journal *store.Store, report na.Report, first suiteMove) error {
	second, ok, err := newSuiteShelled(ctx, journal, first.room)
	if err != nil {
		return err
	}
	if !ok {
		return fmt.Errorf("the title raised the colonist's target but no new suite was shelled")
	}
	review, err := journal.LoadRoutineReview(ctx)
	if err != nil {
		return err
	}
	layout, _, err := journal.LayoutPlan(ctx, review.Snapshot, review.Tick)
	if err != nil {
		return err
	}
	kept, ok := roomAt(layout.Plan, policy.WingSuites, first.room.Interior.X, first.room.Interior.Z)
	if !ok || !kept.Same(first.room) {
		return fmt.Errorf("the first suite %+v changed or left the plan (now %+v)", first.room.Interior, kept.Interior)
	}
	plans, err := history(ctx, journal)
	if err != nil {
		return err
	}
	for _, plan := range plans {
		if strings.HasPrefix(string(plan.Method), "bedroom-grow-") {
			return fmt.Errorf("a suite was grown in place: %s", plan.Method)
		}
	}
	in := second.Interior
	report["new_suite"] = fmt.Sprintf("%d,%d %dx%d", in.X, in.Z, in.Width, in.Height)
	return nil
}

// ownsBed checks natively that pawn owns bed.
func ownsBed(ctx context.Context, h *na.Harness, identity any, pawn domain.PawnID, bed, when string) error {
	reply, err := h.Wire(ctx, "pawn", "observations_list_pawns", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "filter": map[string]any{"ids": []string{string(pawn)}},
		"details": map[string]any{},
	})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	rows := na.AsSlice(observed["pawns"])
	if len(rows) != 1 {
		return fmt.Errorf("pawn read: %#v", observed)
	}
	row, _ := na.AsMap(rows[0])
	if owned := na.RefID(row["ownedBed"]); owned != bed {
		return fmt.Errorf("%s: colonist %s owns %q, not the suite bed %q", when, pawn, owned, bed)
	}
	return nil
}
