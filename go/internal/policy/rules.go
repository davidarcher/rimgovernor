package policy

import "slices"

// RuleContext is what the Rules read: the routine review's control state
// and the emergency needs it found (EmergencyNeed), which the review keeps
// in its JSON record.
type RuleContext struct {
	Enabled   bool
	Emergency []GoalID
}

// RuleProposal is the routine goal a proposal would serve: its need and its
// current priority.
type RuleProposal struct {
	Need     GoalID
	Priority int
}

// A Rule vetoes proposals at Admission (#1017). Priority orders work only;
// suspending other work is a Rule's job. The planner runner asks the Rules
// before planning for a need, method admission asks them again as a
// backstop, and dispatch asks them before any prepared plan writes.
type Rule interface {
	// Veto returns the reason the proposal is refused, or "" to admit it.
	Veto(RuleContext, RuleProposal) string
}

// PauseRule vetoes every routine proposal while control is paused: native
// work already issued keeps progressing and the resumed goal adopts it.
type PauseRule struct{}

func (PauseRule) Veto(c RuleContext, _ RuleProposal) string {
	if !c.Enabled {
		return "control paused"
	}
	return ""
}

// EmergencyRule vetoes every proposal at priority 2 or above while the
// review found an emergency need; the emergency needs themselves and
// anything below priority 2 are exempt.
type EmergencyRule struct{}

func (EmergencyRule) Veto(c RuleContext, p RuleProposal) string {
	if len(c.Emergency) == 0 || p.Priority < 2 || slices.Contains(c.Emergency, p.Need) {
		return ""
	}
	return "emergency " + joinGoals(c.Emergency)
}

// Rules is every admission Rule, asked in order.
var Rules = []Rule{PauseRule{}, EmergencyRule{}}

// VetoProposal asks each Rule in turn and returns the first veto's reason,
// or "" when every Rule admits the proposal.
func VetoProposal(c RuleContext, p RuleProposal) string {
	for _, r := range Rules {
		if reason := r.Veto(c, p); reason != "" {
			return reason
		}
	}
	return ""
}

func joinGoals(ids []GoalID) string {
	out := ""
	for i, id := range ids {
		if i > 0 {
			out += ","
		}
		out += string(id)
	}
	return out
}
