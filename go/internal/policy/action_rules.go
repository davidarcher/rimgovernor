package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ActionContext is what the action Rules read from the routine review.
// Unsafe lists the loose things the last safety census reported unsafe to
// haul (fire, trap, hostile line of sight).
type ActionContext struct {
	Unsafe []string
}

// An ActionRule vetoes one action at dispatch (#1018), whatever planner
// proposed it; the goal-level Rules stay whole-goal. A veto refuses only
// that action, and the rest of its plan dispatches.
type ActionRule interface {
	// Veto returns the reason the action is refused, or "" to admit it.
	Veto(ActionContext, domain.Action) string
}

// UnsafeLootRule refuses allowing an item the safety census reported unsafe.
// Forbidding stays open: that is ManageSupplySafety's Standard work.
type UnsafeLootRule struct{}

func (UnsafeLootRule) Veto(c ActionContext, a domain.Action) string {
	s, ok := a.SupplyAllow()
	if !ok || s.Forbidden() || !slices.Contains(c.Unsafe, s.Thing()) {
		return ""
	}
	return "unsafe item " + s.Thing()
}

// ActionRules is every action Rule, asked in order.
var ActionRules = []ActionRule{UnsafeLootRule{}}

// VetoAction returns the first action Rule's veto reason, or "".
func VetoAction(c ActionContext, a domain.Action) string {
	for _, r := range ActionRules {
		if reason := r.Veto(c, a); reason != "" {
			return reason
		}
	}
	return ""
}

// UnsafeLoot lists the things a census reports unsafe to haul, sorted.
func UnsafeLoot(rows []LootItem) []string {
	var out []string
	for _, row := range rows {
		if row.SafetyKnown && !row.SafeToHaul {
			out = append(out, row.Supply.Thing)
		}
	}
	slices.Sort(out)
	return out
}
