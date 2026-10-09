package buildingruntime

import (
	"context"
	"fmt"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The tend, rescue, repair, clean, equip, mood relief and cast, exhausted
// drill, ash cleaning, clearance and husbandry planners share one retry
// budget (policy.RefusalBudget). It is derived from the journal on every
// step, never stored: each method's plan carries the refusals native
// returned for its actions (Progress.view.Refusal), and the ledger folds
// them by subject. A method that was accepted, interrupted or never
// dispatched spends nothing; only a native refusal does, and what it spends
// depends on its class.

// budgetWorld is the world a refusal was met in. The plan and revision a
// snapshot carries belong to the plan that was dispatched, not to the world,
// so they are dropped; the colony, map, load and native generation remain.
func budgetWorld(s domain.GenerationSnapshot) domain.GenerationSnapshot {
	s.Plan, s.Revision = "", 0
	return s
}

// standardMethodPlans lists the plans of a Standard's methods in the episode whose
// ID starts with prefix, retired plans included.
func standardMethodPlans(history []domain.Method, episode uint64, prefix string) []domain.PlanID {
	var plans []domain.PlanID
	for _, m := range history {
		if m.Episode == episode && strings.HasPrefix(string(m.Method), prefix) {
			plans = append(plans, m.Plan)
		}
	}
	return plans
}

// incidentPlans lists the plans of an occurrence's methods whose ID starts
// with prefix; each occurrence is its own episode.
func incidentPlans(methods []store.IncidentMethod, prefix string) []domain.PlanID {
	var plans []domain.PlanID
	for _, m := range methods {
		if strings.HasPrefix(string(m.Method), prefix) {
			plans = append(plans, m.Plan)
		}
	}
	return plans
}

// refusalLedger folds the native refusals journaled on plans into a budget.
// subjectOf names the subject a progress row was refused for; "" skips it.
// A refused receipt that carries no native account is recorded unknown, which
// never bans its subject.
func refusalLedger(ctx context.Context, journal *store.Store, plans []domain.PlanID, subjectOf func(domain.Progress) string) (policy.RefusalBudget, error) {
	var budget policy.RefusalBudget
	for _, id := range plans {
		plan, err := journal.LoadPlan(ctx, id)
		if err != nil {
			return policy.RefusalBudget{}, err
		}
		for _, progress := range plan.Progress {
			view := progress.View()
			if receipt, known := view.Receipt.Value(); !known || receipt != domain.ReceiptRefused {
				continue
			}
			subject := subjectOf(progress)
			if subject == "" {
				continue
			}
			refusal, known := view.Refusal.Value()
			if !known {
				refusal.Reason, refusal.Class = "refused", domain.RefusalUnknown
			}
			budget = budget.Record(subject, refusal.Reason, refusal.Class, budgetWorld(view.Snapshot))
		}
	}
	return budget, nil
}

// budgetVerdict answers whether subject may be tried in world: ok when it
// may, otherwise the verdict that says why not. A permanent refusal is a
// retry_budget_spent refusal carrying native's real reason; a transient one
// waits for the world to change. An unclassified refusal never blocks.
func budgetVerdict(budget policy.RefusalBudget, subject string, world domain.GenerationSnapshot) (Verdict, bool) {
	ok, why := budget.Allowed(subject, budgetWorld(world))
	if ok {
		return Verdict{}, true
	}
	if why.Code == policy.BudgetWaiting {
		return retryBudgetWait(subject, reasonToken(why.Reason)), false
	}
	return refuse(RefusalRetriesSpent, subject, reasonToken(why.Reason)), false
}

// reasonToken is native's reason text as one verdict token: no spaces.
func reasonToken(reason string) string { return strings.Join(strings.Fields(reason), "_") }

// admitSubject is the planner's single question before it commits a method
// for subject, whose every refusal sits on plans: the verdict that says why
// not, or ok.
func admitSubject(ctx context.Context, journal *store.Store, subject string, plans []domain.PlanID, world domain.GenerationSnapshot) (Verdict, bool, error) {
	subject = strings.TrimSuffix(subject, "-")
	budget, err := refusalLedger(ctx, journal, plans, func(domain.Progress) string { return subject })
	if err != nil {
		return Verdict{}, false, err
	}
	verdict, ok := budgetVerdict(budget, subject, world)
	return verdict, ok, nil
}

// nextMethodID names the next method under prefix: the first ordinal no
// listed method uses. Identity is independent of the budget; successful and
// interrupted methods take an ordinal and spend nothing.
func nextMethodID(prefix string, taken []domain.MethodID) domain.MethodID {
	used := make(map[domain.MethodID]bool, len(taken))
	for _, m := range taken {
		used[m] = true
	}
	for i := 0; ; i++ {
		if id := domain.MethodID(fmt.Sprintf("%s%d", prefix, i)); !used[id] {
			return id
		}
	}
}

func historyMethodIDs(history []domain.Method, episode uint64) []domain.MethodID {
	ids := make([]domain.MethodID, 0, len(history))
	for _, m := range history {
		if m.Episode == episode {
			ids = append(ids, m.Method)
		}
	}
	return ids
}

func incidentMethodIDs(methods []store.IncidentMethod) []domain.MethodID {
	ids := make([]domain.MethodID, 0, len(methods))
	for _, m := range methods {
		ids = append(ids, m.Method)
	}
	return ids
}
