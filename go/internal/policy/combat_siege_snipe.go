package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// siegeFrame is the frame a sortie's gunners shoot: the job
// target of a live besieger on a FinishFrame job, the least id; "" with
// no builder. The first mortar frame ends the sortie, so these
// are the sandbags.
func siegeFrame(view CombatView) domain.PawnID {
	besiegers := liveBesiegers(view)
	var frame domain.PawnID
	for _, p := range view.Pawns {
		if _, ok := besiegers[p.ID]; ok && p.Job == "FinishFrame" && p.Target != "" && (frame == "" || p.Target < frame) {
			frame = p.Target
		}
	}
	return frame
}

// siegeSnipe points a sortie's gunners at the frame the besiegers are
// building, so the mortars never get finished; brawlers keep
// their besieger. The attack order takes a hostile building.
func siegeSnipe(view CombatView, m *CombatMemory) {
	if m.Tactic != TacticSiege || m.SiegeMode != SiegeSortie {
		return
	}
	frame := siegeFrame(view)
	if frame == "" {
		return
	}
	for i := range m.Roles {
		if m.Roles[i].Ranged {
			m.Roles[i].Target = frame
		}
	}
}
