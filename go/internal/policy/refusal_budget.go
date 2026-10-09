package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Budget verdict codes. A planner journals the Code as the stable reason of
// its decision and the Reason beside it.
const (
	// BudgetSpent: native refused permanently; the subject is given up on.
	BudgetSpent = "retry_budget_spent"
	// BudgetWaiting: native refused transiently and the world has not
	// changed since, so the same request would meet the same refusal.
	BudgetWaiting = "retry_budget_waiting"
	// BudgetUnknown: native could not classify the refusal. The subject
	// stays allowed; the refusal stays visible.
	BudgetUnknown = "refusal_unknown"
)

// RefusalEntry is one ledger row: the newest refusal of one subject for one
// reason, with the world it was met in.
type RefusalEntry struct {
	Subject string
	Reason  string
	Class   domain.RefusalClass
	World   domain.GenerationSnapshot
}

// BudgetVerdict says why Allowed answered as it did. It is the zero value
// when the subject has no refusal on record.
type BudgetVerdict struct {
	Code   string
	Reason string
	Class  domain.RefusalClass
}

// RefusalBudget is the one reason-aware retry budget every planner shares.
// It replaces per-planner counters keyed on method-ID prefixes: a permanent
// refusal gives its subject up under its real reason, a transient one waits
// for the world to change, and an unclassified one never bans anything. The
// ledger is a plain value; the store that owns the subject persists Entries.
type RefusalBudget struct {
	Entries []RefusalEntry
}

// Record returns the budget with the refusal noted. A second refusal of the
// same subject for the same reason replaces the first. A class outside the
// three is unknown.
func (b RefusalBudget) Record(subject, reason string, class domain.RefusalClass, world domain.GenerationSnapshot) RefusalBudget {
	if !class.Valid() {
		class = domain.RefusalUnknown
	}
	out := RefusalBudget{Entries: make([]RefusalEntry, 0, len(b.Entries)+1)}
	for _, e := range b.Entries {
		if e.Subject != subject || e.Reason != reason {
			out.Entries = append(out.Entries, e)
		}
	}
	out.Entries = append(out.Entries, RefusalEntry{Subject: subject, Reason: reason, Class: class, World: world})
	return out
}

// Allowed reports whether subject may be tried in world. A permanent
// refusal on record blocks for good and names its real reason; a transient
// one blocks only while world is the world it was met in; an unknown one
// never blocks but is returned so the caller journals it.
func (b RefusalBudget) Allowed(subject string, world domain.GenerationSnapshot) (bool, BudgetVerdict) {
	var unknown, waiting BudgetVerdict
	for _, e := range b.Entries {
		if e.Subject != subject {
			continue
		}
		switch e.Class {
		case domain.RefusalPermanent:
			return false, BudgetVerdict{Code: BudgetSpent, Reason: e.Reason, Class: e.Class}
		case domain.RefusalTransient:
			if e.World == world && waiting.Code == "" {
				waiting = BudgetVerdict{Code: BudgetWaiting, Reason: e.Reason, Class: e.Class}
			}
		default:
			unknown = BudgetVerdict{Code: BudgetUnknown, Reason: e.Reason, Class: domain.RefusalUnknown}
		}
	}
	if waiting.Code != "" {
		return false, waiting
	}
	return true, unknown
}
