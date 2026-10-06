package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SafeguardContext is what the Safeguards read: the rounds's control state,
// the emergency needs it found (EmergencyNeed), which the review keeps in its
// JSON record, and the loose things the last safety census reported unsafe
// to haul (fire, trap, hostile line of sight).
type SafeguardContext struct {
	Enabled   bool
	Emergency []ConcernID
	Unsafe    []string
}

// SafeguardProposal is the routine goal a proposal would serve: its need and its
// current priority.
type SafeguardProposal struct {
	Need     ConcernID
	Priority int
	// HunterWeapons marks the one proposal an emergency does not suspend:
	// EnsureFoodSupply's craft of the hunters' weapons (HunterWeaponCraft).
	HunterWeapons bool
}

// HunterWeaponCraft reports whether a method of need is the hunters' weapon
// craft: a gear-batch bill (only the armory writes those) owned by
// EnsureFoodSupply. Food's hunt waits on that weapon, and EnsureFoodSupply sits at
// priority 2, so it is no emergency need that ownership alone could exempt.
func HunterWeaponCraft(need ConcernID, actions []domain.Action) bool {
	if need != EnsureFoodSupply || len(actions) == 0 {
		return false
	}
	for _, a := range actions {
		if bill, ok := a.ProductionBill(); !ok || bill.Mode() != domain.GearBatch {
			return false
		}
	}
	return true
}

// Admission is what a Safeguard is asked to admit: a whole-goal proposal (#1017)
// or one action at dispatch (#1018). Exactly one is set.
type Admission struct {
	Proposal *SafeguardProposal
	Action   *domain.Action
}

// SafeguardRefusal is a veto: the Safeguard that refused and why.
type SafeguardRefusal struct {
	Safeguard string
	Reason    string
}

// A Safeguard is an admission veto (#1910), not a goal row. Priority orders work
// only; suspending other work is a Safeguard's job. The planner runner asks the
// Safeguards before planning for a need, method admission asks them again as a
// backstop, and dispatch asks them before any prepared plan writes. A Safeguard
// returns "" for an admission kind it does not govern.
type Safeguard interface {
	// Name identifies the Safeguard in a refusal.
	Name() string
	// Veto returns the reason the admission is refused, or "" to admit it.
	Veto(SafeguardContext, Admission) string
}

// PauseSafeguard vetoes every routine proposal while control is paused: native
// work already issued keeps progressing and the resumed goal adopts it.
type PauseSafeguard struct{}

func (PauseSafeguard) Name() string { return "PauseSafeguard" }

func (PauseSafeguard) Veto(c SafeguardContext, a Admission) string {
	if a.Proposal == nil || c.Enabled {
		return ""
	}
	return "control paused"
}

// EmergencySafeguard vetoes every proposal at priority 2 or above while the
// review found an emergency need; the emergency needs themselves and
// anything below priority 2 are exempt, and so is the hunters' weapon craft.
type EmergencySafeguard struct{}

func (EmergencySafeguard) Name() string { return "EmergencySafeguard" }

func (EmergencySafeguard) Veto(c SafeguardContext, a Admission) string {
	p := a.Proposal
	if p == nil || len(c.Emergency) == 0 || p.Priority < 2 || slices.Contains(c.Emergency, p.Need) || p.HunterWeapons {
		return ""
	}
	return "emergency " + joinConcerns(c.Emergency)
}

// UnsafeLootSafeguard refuses allowing an item the safety census reported unsafe.
// Forbidding stays open: that is ManageSupplySafety's Standard work. A veto
// refuses only that action, and the rest of its plan dispatches.
type UnsafeLootSafeguard struct{}

func (UnsafeLootSafeguard) Name() string { return "UnsafeLootSafeguard" }

func (UnsafeLootSafeguard) Veto(c SafeguardContext, a Admission) string {
	if a.Action == nil {
		return ""
	}
	s, ok := (*a.Action).SupplyAllow()
	if !ok || s.Forbidden() || !slices.Contains(c.Unsafe, s.Thing()) {
		return ""
	}
	return "unsafe item " + s.Thing()
}

// Safeguards is the veto registry: every admission Safeguard, asked in order.
var Safeguards = []Safeguard{PauseSafeguard{}, EmergencySafeguard{}, UnsafeLootSafeguard{}}

// Refuse asks each Safeguard in turn and returns the first refusal, or false
// when every Safeguard admits.
func Refuse(c SafeguardContext, a Admission) (SafeguardRefusal, bool) {
	for _, r := range Safeguards {
		if reason := r.Veto(c, a); reason != "" {
			return SafeguardRefusal{Safeguard: r.Name(), Reason: reason}, true
		}
	}
	return SafeguardRefusal{}, false
}

// RefuseProposal is Refuse for a goal-level proposal.
func RefuseProposal(c SafeguardContext, p SafeguardProposal) (SafeguardRefusal, bool) {
	return Refuse(c, Admission{Proposal: &p})
}

// RefuseAction is Refuse for one action at dispatch.
func RefuseAction(c SafeguardContext, a domain.Action) (SafeguardRefusal, bool) {
	return Refuse(c, Admission{Action: &a})
}

// VetoProposal returns the first veto's reason, or "" when every Safeguard
// admits the proposal.
func VetoProposal(c SafeguardContext, p SafeguardProposal) string {
	r, _ := RefuseProposal(c, p)
	return r.Reason
}

func joinConcerns(ids []ConcernID) string {
	out := ""
	for i, id := range ids {
		if i > 0 {
			out += ","
		}
		out += string(id)
	}
	return out
}
