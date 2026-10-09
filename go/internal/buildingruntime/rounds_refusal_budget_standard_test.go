package buildingruntime

import (
	"context"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// journalRefusedMethod creates a one-write plan for the method, journals a
// native refusal of class on it and returns the goal that lists the method.
func journalRefusedMethod(t *testing.T, db *store.Store, world domain.GenerationSnapshot, goal store.StandardState, method domain.MethodID, class domain.RefusalClass, reason string) store.StandardState {
	t.Helper()
	goal = journalMethod(t, db, goal, method)
	refuseMethodPlan(t, db, world, goal.History[len(goal.History)-1].Plan, class, reason)
	return goal
}

// journalMethod creates a one-write plan for the method that is never
// dispatched and returns the goal that lists the method.
func journalMethod(t *testing.T, db *store.Store, goal store.StandardState, method domain.MethodID) store.StandardState {
	t.Helper()
	ctx := context.Background()
	id := domain.PlanID("plan-" + string(method))
	value, err := domain.NewHusbandry("warg", domain.HusbandryTrain, "Release")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewHusbandryAction(domain.ActionID(string(id)+"-0"), value)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	goal.History = append(goal.History, domain.Method{Episode: goal.Standard.Episode, Method: method, Plan: id})
	return goal
}

// The population, quest, ideology, ritual and permit planners ask the same
// question of the same ledger: a permanent refusal gives the subject up under
// native's real reason, a transient one waits until the native generation
// changes, an unclassified one retries, and another subject is unaffected.
// Eleven earlier methods that were not refused spend nothing.
func TestStandardMethodsFollowTheSharedRefusalBudget(t *testing.T) {
	for _, tc := range []struct {
		name   string
		prefix string
		class  domain.RefusalClass
		reason string
		check  func(Verdict, domain.MethodID, bool) bool
	}{
		{"permanent population write", "population-tend-alice-", domain.RefusalPermanent, "not downed", func(v Verdict, _ domain.MethodID, ok bool) bool {
			return !ok && v.Is(RefusalRetriesSpent) && v.Refusal.Subject == "population-tend-alice" && v.Refusal.Detail == "not_downed"
		}},
		{"permanent quest write", "quest-expedition-q1-site-", domain.RefusalPermanent, "quest expired", func(v Verdict, _ domain.MethodID, ok bool) bool {
			return !ok && v.Is(RefusalRetriesSpent) && v.Refusal.Detail == "quest_expired"
		}},
		{"transient ideology role waits", "ideorole-alice-leader-", domain.RefusalTransient, "no slot", func(v Verdict, _ domain.MethodID, ok bool) bool {
			return !ok && v.Is(WaitRetryBudget) && v.Refusal.Detail == "no_slot"
		}},
		{"transient ritual waits", "ritual-Funeral-3-4-", domain.RefusalTransient, "not ready", func(v Verdict, _ domain.MethodID, ok bool) bool {
			return !ok && v.Is(WaitRetryBudget) && v.Refusal.Detail == "not_ready"
		}},
		{"unknown permit retries with the next ordinal", "permit-alice-TradeSettlement-", domain.RefusalUnknown, "odd", func(v Verdict, m domain.MethodID, ok bool) bool {
			return ok && v == (Verdict{}) && m == "permit-alice-TradeSettlement-12"
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			reviewer, db, _, _, _ := roundsFixture(t)
			ctx := context.Background()
			world := reviewer.player.session.State().Snapshot
			goal := store.StandardState{Standard: domain.Standard{Episode: 1}}
			for i := 0; i < 11; i++ {
				goal = journalMethod(t, db, goal, domain.MethodID(fmt.Sprintf("%s%d", tc.prefix, i)))
			}
			goal = journalRefusedMethod(t, db, world, goal, domain.MethodID(tc.prefix+"11"), tc.class, tc.reason)
			method, verdict, ok, err := admitStandardMethod(ctx, db, goal, tc.prefix, world)
			if err != nil || !tc.check(verdict, method, ok) {
				t.Fatal(method, verdict, ok, err)
			}
			if _, _, other, err := admitStandardMethod(ctx, db, goal, tc.prefix+"other-", world); err != nil || !other {
				t.Fatal("another subject must stay allowed", other, err)
			}
			if tc.class == domain.RefusalTransient {
				moved := world
				moved.Native++
				if _, _, ok, err = admitStandardMethod(ctx, db, goal, tc.prefix, moved); err != nil || !ok {
					t.Fatal("a changed world must re-arm a transient refusal", ok, err)
				}
			}
		})
	}
}

// A Project (the shrine planner) has no epochs; the same ledger reads its
// history.
func TestProjectMethodsFollowTheSharedRefusalBudget(t *testing.T) {
	reviewer, db, _, _, _ := roundsFixture(t)
	ctx := context.Background()
	world := reviewer.player.session.State().Snapshot
	goal := journalRefusedMethod(t, db, world, store.StandardState{}, "breach-s1-w1-0", domain.RefusalPermanent, "not diggable")
	project := store.ProjectState{History: []store.ProjectMethod{{Method: goal.History[0].Method, Plan: goal.History[0].Plan}}}
	_, verdict, ok, err := admitProjectMethod(ctx, db, project, "breach-s1-w1-", world)
	if err != nil || ok || !verdict.Is(RefusalRetriesSpent) || verdict.Refusal.Detail != "not_diggable" {
		t.Fatal(verdict, ok, err)
	}
	method, _, ok, err := admitProjectMethod(ctx, db, project, "breach-s1-w2-", world)
	if err != nil || !ok || method != "breach-s1-w2-0" {
		t.Fatal(method, ok, err)
	}
}
