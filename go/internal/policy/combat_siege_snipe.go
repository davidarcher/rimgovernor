package policy

import (
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// siegeFrame is the frame a sortie's gunners shoot (#919): the job
// target of a live besieger on a FinishFrame job, a mortar frame (its
// load id names the def) first, then by id; "" with no builder.
func siegeFrame(view CombatView) domain.PawnID {
	besiegers := liveBesiegers(view)
	var frames []domain.PawnID
	for _, p := range view.Pawns {
		if _, ok := besiegers[p.ID]; ok && p.Job == "FinishFrame" && p.Target != "" {
			frames = append(frames, p.Target)
		}
	}
	sort.SliceStable(frames, func(i, j int) bool {
		mi, mj := strings.Contains(string(frames[i]), "Mortar"), strings.Contains(string(frames[j]), "Mortar")
		if mi != mj {
			return mi
		}
		return frames[i] < frames[j]
	})
	if len(frames) == 0 {
		return ""
	}
	return frames[0]
}

// siegeSnipe points a sortie's gunners at the frame the besiegers are
// building (#919), so the mortars never get finished; brawlers keep
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
