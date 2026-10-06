package policy

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A hunter's weapon is the one prerequisite of a hunt that the plan prices as
// its own produce step (#2162): craft one bow, at an estimated work and lead.
// The butcher bill and the butcher spot are owed on the food runway alone
// (their executors, ButcherFood and MaintainButcherSpot, never wait on a hunt
// row), so they are not repriced here.
const (
	// HunterWeaponCraftTicks is the work of crafting one hunting weapon.
	HunterWeaponCraftTicks = 6000.0
	// HunterWeaponLeadDays is how long the craft takes before the hunt can
	// start; reported as the craft_lead_days term.
	HunterWeaponLeadDays = 0.1
	// termNeedsWeapon marks a hunt candidate that waits on a hunter's weapon.
	termNeedsWeapon = "needs_weapon"
)

// needsHunterWeapon reports a no_hunter hold where some colonist is only
// missing the hunting work or its weapon: arming one of them opens the row.
func (h HuntHold) needsHunterWeapon() bool {
	if h.Reason != HuntHoldNoHunter {
		return false
	}
	for _, d := range h.Detail {
		if strings.HasSuffix(d, " "+huntHunterInactive) || strings.HasSuffix(d, " "+huntHunterNoWeapon) {
			return true
		}
	}
	return false
}

// HuntPrerequisiteCandidates are the lone hunt channels of the food prey whose
// only blocker is a hunter's weapon: each is priced as the hunt plus the craft
// (upfront HunterWeaponCraftTicks, craft_lead_days term) and carries a
// needs_weapon term. The plan opens one only when the hunt is worth it; an
// opened one is HuntArming.
func HuntPrerequisiteCandidates(holds []HuntHold) []SupplyCandidate {
	var out []SupplyCandidate
	for _, h := range holds {
		if !h.needsHunterWeapon() || !h.Source.Food || h.Source.Tree {
			continue
		}
		c := huntChannel(h.Source.ID, []AcquisitionSource{h.Source}, false, 1, 0)
		// The craft's lead is a term, not the candidate's lead: a starving
		// colony has no runway, and any positive lead exceeds it, so the one
		// step that lets it hunt could never open.
		c.UpfrontCost.LaborTicks = domain.Known(HunterWeaponCraftTicks)
		c.Terms = append(c.Terms, CandidateTerm{termNeedsWeapon, 1}, CandidateTerm{"craft_lead_days", HunterWeaponLeadDays})
		out = append(out, c)
	}
	return out
}

// HuntArming reports whether the plan opened a hunt that waits on a hunter's
// weapon: the colonist who will carry it needs Hunting before it exists.
func HuntArming(plan domain.Fact[FoodPlan]) bool {
	p, known := plan.Value()
	if !known {
		return false
	}
	for _, e := range p.Portfolio {
		if e.Channel.Kind != CandidateHunt || e.Decision != FoodPlanOpen {
			continue
		}
		for _, t := range e.Channel.Terms {
			if t.Name == termNeedsWeapon {
				return true
			}
		}
	}
	return false
}
