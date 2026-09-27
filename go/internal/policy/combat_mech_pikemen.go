package policy

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// pikemenCharge is the pikemen's last charge (#926): once every live
// hostile is a pikeman (Mech_Pikeman), every defender wearing a shield belt
// leaves its cell and attacks the nearest pikeman in melee; the belt stops
// the pikemen's long shots on the way in. Before that nothing changes.
func pikemenCharge(view CombatView, m *CombatMemory) {
	pikemen := rankThreats(view)
	for _, h := range pikemen {
		if !strings.HasPrefix(h.Kind, "Mech_Pikeman") {
			return
		}
	}
	if len(pikemen) == 0 {
		return
	}
	state := map[domain.PawnID]CombatPawnState{}
	for _, p := range view.Pawns {
		state[p.ID] = p
	}
	for i, r := range m.Roles {
		s := state[r.Pawn]
		if !s.ShieldBelt {
			continue
		}
		m.Roles[i].Cell, m.Roles[i].Home, m.Roles[i].Ranged = nil, nil, false
		m.Roles[i].Target = pikemen[0].ID
		if at, ok := s.Cell.Value(); ok {
			if id := nearestHostile(view, at); id != "" {
				m.Roles[i].Target = id
			}
		}
	}
}
