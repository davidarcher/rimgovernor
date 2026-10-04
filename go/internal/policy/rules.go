package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RuleContext is what the Rules read: the routine review's control state,
// the emergency needs it found (EmergencyNeed), which the review keeps in its
// JSON record, and the loose things the last safety census reported unsafe
// to haul (fire, trap, hostile line of sight).
type RuleContext struct {
	Enabled   bool
	Emergency []GoalID
	Unsafe    []string
}

// RuleProposal is the routine goal a proposal would serve: its need and its
// current priority.
type RuleProposal struct {
	Need     GoalID
	Priority int
}

// Admission is what a Rule is asked to admit: a whole-goal proposal (#1017)
// or one action at dispatch (#1018). Exactly one is set.
type Admission struct {
	Proposal *RuleProposal
	Action   *domain.Action
}

// RuleRefusal is a veto: the Rule that refused and why.
type RuleRefusal struct {
	Rule   string
	Reason string
}

// A Rule is an admission veto (#1910), not a goal row. Priority orders work
// only; suspending other work is a Rule's job. The planner runner asks the
// Rules before planning for a need, method admission asks them again as a
// backstop, and dispatch asks them before any prepared plan writes. A Rule
// returns "" for an admission kind it does not govern.
type Rule interface {
	// Name identifies the Rule in a refusal.
	Name() string
	// Veto returns the reason the admission is refused, or "" to admit it.
	Veto(RuleContext, Admission) string
}

// PauseRule vetoes every routine proposal while control is paused: native
// work already issued keeps progressing and the resumed goal adopts it.
type PauseRule struct{}

func (PauseRule) Name() string { return "PauseRule" }

func (PauseRule) Veto(c RuleContext, a Admission) string {
	if a.Proposal == nil || c.Enabled {
		return ""
	}
	return "control paused"
}

// EmergencyRule vetoes every proposal at priority 2 or above while the
// review found an emergency need; the emergency needs themselves and
// anything below priority 2 are exempt.
type EmergencyRule struct{}

func (EmergencyRule) Name() string { return "EmergencyRule" }

func (EmergencyRule) Veto(c RuleContext, a Admission) string {
	p := a.Proposal
	if p == nil || len(c.Emergency) == 0 || p.Priority < 2 || slices.Contains(c.Emergency, p.Need) {
		return ""
	}
	return "emergency " + joinGoals(c.Emergency)
}

// UnsafeLootRule refuses allowing an item the safety census reported unsafe.
// Forbidding stays open: that is ManageSupplySafety's Standard work. A veto
// refuses only that action, and the rest of its plan dispatches.
type UnsafeLootRule struct{}

func (UnsafeLootRule) Name() string { return "UnsafeLootRule" }

func (UnsafeLootRule) Veto(c RuleContext, a Admission) string {
	if a.Action == nil {
		return ""
	}
	s, ok := (*a.Action).SupplyAllow()
	if !ok || s.Forbidden() || !slices.Contains(c.Unsafe, s.Thing()) {
		return ""
	}
	return "unsafe item " + s.Thing()
}

// Rules is the veto registry: every admission Rule, asked in order.
var Rules = []Rule{PauseRule{}, EmergencyRule{}, UnsafeLootRule{}}

// Refuse asks each Rule in turn and returns the first refusal, or false
// when every Rule admits.
func Refuse(c RuleContext, a Admission) (RuleRefusal, bool) {
	for _, r := range Rules {
		if reason := r.Veto(c, a); reason != "" {
			return RuleRefusal{Rule: r.Name(), Reason: reason}, true
		}
	}
	return RuleRefusal{}, false
}

// RefuseProposal is Refuse for a goal-level proposal.
func RefuseProposal(c RuleContext, p RuleProposal) (RuleRefusal, bool) {
	return Refuse(c, Admission{Proposal: &p})
}

// RefuseAction is Refuse for one action at dispatch.
func RefuseAction(c RuleContext, a domain.Action) (RuleRefusal, bool) {
	return Refuse(c, Admission{Action: &a})
}

// VetoProposal returns the first veto's reason, or "" when every Rule
// admits the proposal.
func VetoProposal(c RuleContext, p RuleProposal) string {
	r, _ := RefuseProposal(c, p)
	return r.Reason
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
