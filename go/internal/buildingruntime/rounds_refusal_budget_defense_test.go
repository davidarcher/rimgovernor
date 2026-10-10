package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// projectOf lists the goal's journaled methods as a Project's history.
func projectOf(goal store.StandardState) store.ProjectState {
	var project store.ProjectState
	for _, m := range goal.History {
		project.History = append(project.History, store.ProjectMethod{Method: m.Method, Plan: m.Plan})
	}
	return project
}

// The defense layout planner (tier, rearm and cover methods) and the
// firebreak planner keep no attempt constant: a permanent refusal gives the
// subject up under native's real reason, a transient one waits until the
// native generation changes, and another subject is unaffected. Methods that
// were not refused spend nothing however many there are.
func TestDefenseAndFirebreakFollowTheSharedRefusalBudget(t *testing.T) {
	reopened := store.DefenseTierRecord{Name: policy.TierTurrets, Reopened: 1}
	for _, tc := range []struct {
		name    string
		subject string
		other   string
		method  func(i int) domain.MethodID
		class   domain.RefusalClass
		reason  string
		spent   bool
	}{
		{"permanent tier refusal gives the tier up", defenseTierSubject(store.DefenseTierRecord{Name: policy.TierFunnel}), defenseTierSubject(store.DefenseTierRecord{Name: policy.TierTurrets}),
			func(i int) domain.MethodID {
				return defenseTierMethodID(store.DefenseTierRecord{Name: policy.TierFunnel, Attempts: i})
			}, domain.RefusalPermanent, "cannot place", true},
		{"a refusal in one repair does not bar the next", defenseTierSubject(store.DefenseTierRecord{Name: policy.TierTurrets}), defenseTierSubject(reopened),
			func(i int) domain.MethodID {
				return defenseTierMethodID(store.DefenseTierRecord{Name: policy.TierTurrets, Attempts: i})
			}, domain.RefusalPermanent, "cannot place", true},
		{"transient rearm waits for the world", defenseRearmPrefix("T1"), defenseRearmPrefix("T2"),
			func(i int) domain.MethodID {
				return domain.MethodID(defenseRearmPrefix("T1")+"100") + domain.MethodID(rune('a'+i))
			}, domain.RefusalTransient, "no fuel", false},
		{"permanent cover refusal", defenseCoverPrefix, defenseTierPrefix(policy.TierFunnel),
			func(i int) domain.MethodID { return domain.MethodID(defenseCoverPrefix + "1000" + string(rune('a'+i))) }, domain.RefusalPermanent, "cannot designate", true},
		{"permanent firebreak refusal", firebreakPrefix, defenseCoverPrefix,
			func(i int) domain.MethodID { return domain.MethodID(firebreakPrefix + "1000" + string(rune('a'+i))) }, domain.RefusalPermanent, "cannot designate", true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			reviewer, db, _, _, _ := roundsFixture(t)
			ctx := context.Background()
			world := reviewer.player.session.State().Snapshot
			goal := store.StandardState{Standard: domain.Standard{Episode: 1}}
			for i := 0; i < 9; i++ {
				goal = journalMethod(t, db, goal, tc.method(i))
			}
			goal = journalRefusedMethod(t, db, world, goal, tc.method(9), tc.class, tc.reason)
			project := projectOf(goal)
			verdict, ok, err := admitProjectSubject(ctx, db, project, tc.subject, world)
			if err != nil || ok {
				t.Fatal("a refusal must bar the subject in this world", verdict, ok, err)
			}
			if tc.spent && (!verdict.Is(policy.CauseRetriesSpent) || verdict.Refusal.Detail == "") {
				t.Fatal(verdict)
			}
			if !tc.spent && !verdict.Is(policy.CauseRetryBudgetWait) {
				t.Fatal(verdict)
			}
			if _, ok, err = admitProjectSubject(ctx, db, project, tc.other, world); err != nil || !ok {
				t.Fatal("another subject must stay allowed", ok, err)
			}
			// The standard-owner form (firebreak) reads the same ledger.
			if verdict, ok, err = admitSubject(ctx, db, tc.subject, standardMethodPlans(goal.History, 1, tc.subject), world); err != nil || ok {
				t.Fatal(verdict, ok, err)
			}
			if !tc.spent {
				moved := world
				moved.Native++
				if _, ok, err = admitProjectSubject(ctx, db, project, tc.subject, moved); err != nil || !ok {
					t.Fatal("a changed world must re-arm a transient refusal", ok, err)
				}
			}
		})
	}
}
