package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// rushRange is how close to the breach, in cells, a hostile sets the
// posted brawlers on it.
const rushRange = 4

// sapperRush is the sapper tactic's melee rush. The brawlers posted
// just inside the breach hold with no target, so none walks out to fight
// in the open, until a live hostile comes within rushRange of the breach
// cell or a breach stop arrives. Then every posted brawler leaves its cell
// and attacks the hostile nearest the breach, and keeps rushing until the
// next formation.
func sapperRush(view CombatView, stop StopEvent, m *CombatMemory) {
	if m.Tactic != TacticSapper || m.SapperBreach == nil {
		m.Rushing = false
		return
	}
	breach := *m.SapperBreach
	var nearest domain.PawnID
	near := int64(-1)
	for _, h := range rankThreats(view) {
		if c, ok := h.Cell.Value(); ok && (near < 0 || distance2(c, breach) < near) {
			nearest, near = h.ID, distance2(c, breach)
		}
	}
	if nearest == "" {
		return
	}
	if near <= rushRange*rushRange || stop.Kind == StopBreach {
		m.Rushing = true
	}
	if !m.Rushing {
		return
	}
	for i := range m.Roles {
		if r := &m.Roles[i]; r.Duty == DutyBlocker && !r.Ranged {
			r.Cell, r.Target = nil, nearest
		}
	}
}
