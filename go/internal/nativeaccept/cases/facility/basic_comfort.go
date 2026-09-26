package facility

import (
	"context"
	"fmt"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/sustainedfood"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// basicComfortFamilies is the foothold comfort goal's own family plus the
// emergency responders: the fixture already stands the hut the startup
// ladder would otherwise build, so no other foothold family has to act, and
// the ranked ladder never gates a priority-2 goal.
const basicComfortFamilies = "comfort,work,supply,tend,rescue"

// basicComfortWindow is how long EnsureBasicComfort gets to furnish the hut
// (a table, a seat and a horseshoes pin from wood on hand); the watch ends
// when the goal recovers.
const basicComfortWindow = 12 * time.Minute

// mealStepTicks and mealSteps bound the settling the audit drives itself
// once the watch ends: the served families have no work left, so nothing
// else advances the clock, and three colonists share one seat, so the
// fixture re-seeds hunger one colonist at a time.
const (
	mealStepTicks = 500
	mealSteps     = 10
)

// mealGraceTicks is one meal: a colonist mid-meal when the seat landed may
// record AteWithoutTable that much later.
const mealGraceTicks = 1000

func init() {
	cases.Register(cases.Case{
		Name:  "facility/basic-comfort",
		Scope: "EnsureBasicComfort furnishes the roofed starter hut at foothold priority (one table, one seat, one recreation source, whatever room role the hut scores) and colonists then eat at the table: no AteWithoutTable memory is gained after it stands (#232).",
		// The hut is sited on the committed tribal baseline, whose open
		// ground near the colonists is audited: an unpinned debug start draws
		// a fresh world each run and may offer none (#674).
		Start: cases.Fixture{Op: "test/basic_comfort_prepare", On: cases.Save{Name: sustained.BaselineSave}},
		// The meal that proves the point and the pin's use need Food and
		// Joy live.
		Keep:   []string{string(na.NeedFood), string(na.NeedJoy)},
		Serve:  spec("facility", basicComfortFamilies),
		Budget: basicComfortWindow + 10*time.Minute,
		Reason: "the goal builds three items from wood on hand, then the audit advances the clock a meal at a time while three colonists take turns at the one seat; the fixture already stages the roofed hut",
		Run: func(ctx context.Context, s cases.Session) error {
			var people []string
			_, err := sustainedfood.Observe(ctx, s, sustainedfood.Observation{
				WatchConfig: sustainedfood.WatchConfig{Watch: basicComfortWindow, Goal: policy.EnsureBasicComfort,
					Extra: []policy.GoalID{policy.EnsureInitialShelter},
					Until: comfortRecovered},
				Prepare: func(ctx context.Context, h *na.Harness, report na.Report) error {
					prepared := s.Prepared()
					report["fixture"] = prepared
					for _, raw := range na.AsSlice(prepared["colonists"]) {
						people = append(people, na.AsString(raw))
					}
					if len(people) == 0 {
						return fmt.Errorf("fixture housed no colonist: %#v", prepared)
					}
					comfort, _ := na.AsMap(prepared["comfort"])
					if len(na.AsSlice(comfort["surfaces"])) != 0 || len(na.AsSlice(comfort["dining"])) != 0 || len(na.AsSlice(comfort["recreation"])) != 0 {
						return fmt.Errorf("fixture left a facility standing; the goal has nothing to prove: %#v", comfort)
					}
					return nil
				},
				Audit: func(ctx context.Context, h *na.Harness, report na.Report) error {
					journal, err := openJournal(ctx, s)
					if err != nil {
						return err
					}
					defer journal.Close()
					review, err := journal.LoadRoutineReview(ctx)
					if err != nil {
						return fmt.Errorf("load routine review: %w", err)
					}
					var goalID domain.GoalID
					for _, binding := range review.Goals {
						if binding.Need == policy.EnsureBasicComfort {
							goalID = binding.Goal
						}
					}
					if goalID == "" {
						return fmt.Errorf("EnsureBasicComfort was never bound in the routine review")
					}
					goal, err := journal.LoadGoal(ctx, goalID)
					if err != nil {
						return err
					}
					report["basic_comfort_goal"] = map[string]any{"status": string(goal.Goal.Status), "need": string(goal.Goal.Need), "priority": goal.Goal.Priority}
					// Once dining and recreation capacity stand, the goal
					// ranks at maintenance priority for variety (#494); a
					// goal still at foothold priority never built both.
					if goal.Goal.Priority != 3 {
						return fmt.Errorf("EnsureBasicComfort ranks at priority %d, not maintenance: capacity never recovered", goal.Goal.Priority)
					}
					if goal.Goal.Need != domain.NeedRecovered || goal.Goal.Status != domain.GoalSatisfied {
						return fmt.Errorf("EnsureBasicComfort did not recover within the watch window: need=%s status=%s", goal.Goal.Need, goal.Goal.Status)
					}
					return auditBasicComfort(ctx, s, h, report, people)
				},
			})
			return err
		},
	})
}

// auditBasicComfort compares the recovered goal with live native facts: a
// table, a seat and a recreation source every colonist can reach (the seat
// under a roof, hosted by whatever room the hut scores), hunger re-seeded
// once the table and seat stood, every colonist's next meal eaten at the
// table and no AteWithoutTable memory younger than that. The meals are
// driven here: the clock advances a meal at a time until everyone has eaten
// or the settling budget runs out.
func auditBasicComfort(ctx context.Context, s cases.Session, h *na.Harness, report na.Report, people []string) error {
	readAudit := func() (map[string]any, error) {
		audit, err := h.Call(ctx, "audit-basic-comfort", "test/basic_comfort_audit", map[string]any{})
		if err != nil {
			return nil, err
		}
		if success, _ := na.AsBool(audit["success"]); !success {
			return nil, fmt.Errorf("basic_comfort_audit refused: %#v", audit)
		}
		report["basic_comfort_audit"] = audit
		return audit, nil
	}
	allAte := func(audit map[string]any) bool {
		for _, raw := range na.AsSlice(audit["colonists"]) {
			row, _ := na.AsMap(raw)
			if ate, _ := na.AsBool(row["ateAtTable"]); !ate {
				return false
			}
		}
		return true
	}
	audit, err := readAudit()
	if err != nil {
		return err
	}
	if armed, _ := na.AsBool(audit["Armed"]); armed {
		return fmt.Errorf("no table and seat ever stood: the fixture never re-seeded hunger")
	}
	steps := 0
	for ; !allAte(audit) && steps < mealSteps; steps++ {
		if _, err = s.Advance(ctx, mealStepTicks); err != nil {
			return err
		}
		if audit, err = readAudit(); err != nil {
			return err
		}
	}
	report["meal_steps"] = steps
	now, trigger := na.AsNumber(audit["tick"]), na.AsNumber(audit["TriggerTick"])
	comfort, _ := na.AsMap(audit["comfort"])
	for _, kind := range []string{"dining", "recreation"} {
		accessible := map[string]bool{}
		for _, raw := range na.AsSlice(comfort[kind]) {
			f, _ := na.AsMap(raw)
			for _, p := range na.AsSlice(f["accessibleTo"]) {
				accessible[na.AsString(p)] = true
			}
		}
		for _, p := range people {
			if !accessible[p] {
				return fmt.Errorf("colonist %s has no accessible %s facility", p, kind)
			}
		}
	}
	rows := na.AsSlice(audit["colonists"])
	if len(rows) != len(people) {
		return fmt.Errorf("audit reports %d colonists, fixture housed %d", len(rows), len(people))
	}
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		id := na.AsString(row["id"])
		if ate, _ := na.AsBool(row["ateAtTable"]); !ate {
			return fmt.Errorf("colonist %s never ate at the table after it stood", id)
		}
		for _, age := range na.AsSlice(row["ateWithoutTableAges"]) {
			// A meal already under way when the seat landed finishes, and
			// records its memory, a few hundred ticks later; that is not a
			// meal taken away from a standing table.
			if na.AsNumber(age) < now-trigger-mealGraceTicks {
				return fmt.Errorf("colonist %s ate without a table %v ticks after one stood", id, now-trigger-na.AsNumber(age))
			}
		}
	}
	return nil
}
