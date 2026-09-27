package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// TacticSiege answers a siege (#776): the raid camps at range and shells
// the base, so the fight sorties onto the camp only in the window after
// its supplies land and before its sandbags are up, and otherwise stays
// home.
const TacticSiege CombatTactic = "siege"

// SiegeMode is what the siege tactic does at a stop.
type SiegeMode string

const (
	// SiegeHold keeps everyone home: before the supplies land (attacking
	// then makes them flee), or once the sandbags are up.
	SiegeHold SiegeMode = "hold"
	// SiegeSortie attacks the camp while it builds (#776).
	SiegeSortie SiegeMode = "sortie"
	// SiegeHarass sends the gunners that outrange the camp to shoot it
	// once the sandbags are up, until the lord assaults (#920).
	SiegeHarass SiegeMode = "harass"
)

// siegeSortieWindow is how long after the camp is first seen, in ticks,
// the fight sorties: two in-game hours, roughly the builders' time to put
// the sandbags up before they start on the mortars.
const siegeSortieWindow domain.Tick = 5000

// Siege lord classes.
const (
	siegeLordJob  = "LordJob_Siege"
	siegeCampToil = "LordToil_Siege"
	siegeTravel   = "LordToil_Travel"
)

// liveBesiegers are the live hostiles of a siege lord still travelling or
// camped (not yet assaulting), with their toil.
func liveBesiegers(view CombatView) map[domain.PawnID]string {
	down := downPawns(view)
	out := map[domain.PawnID]string{}
	for _, t := range view.Positional {
		job, _ := t.LordJobClass.Value()
		toil, _ := t.LordToilClass.Value()
		id := domain.PawnID(t.ID)
		if job != siegeLordJob || positive(t.Dead) || positive(t.Downed) || down[id] {
			continue
		}
		if toil == siegeCampToil || toil == siegeTravel {
			out[id] = toil
		}
	}
	return out
}

// siegeTurn records the tick the siege camp is first seen (#776).
func siegeTurn(view CombatView, m *CombatMemory) {
	if m.SiegeCamp != 0 {
		return
	}
	for _, toil := range liveBesiegers(view) {
		if toil == siegeCampToil {
			m.SiegeCamp = view.Tick
			return
		}
	}
}

// siegeMode is the siege tactic's mode at this stop, "" when no siege
// lord is travelling or camped: hold before the camp is set, sortie for
// siegeSortieWindow after it, then harass.
func siegeMode(view CombatView, m CombatMemory) SiegeMode {
	if len(liveBesiegers(view)) == 0 {
		return ""
	}
	switch {
	case m.SiegeCamp == 0:
		return SiegeHold
	case view.Tick-m.SiegeCamp > siegeSortieWindow:
		return SiegeHarass
	}
	return SiegeSortie
}

// siegeFormation assigns the siege roles for mode. Hold: gunners to the
// layout's firing cells (when there is a layout) with no target, brawlers
// nothing, so nobody walks out. Sortie: every armed defender attacks the
// top-ranked besieger.
func siegeFormation(view CombatView, mode SiegeMode) []CombatRole {
	var top domain.PawnID
	besiegers := liveBesiegers(view)
	for _, h := range rankThreats(view) {
		if _, ok := besiegers[h.ID]; ok {
			top = h.ID
			break
		}
	}
	var firing []domain.Cell
	if layout, ok := view.Layout.Value(); ok {
		firing = layout.Firing
	}
	var roles []CombatRole
	i := 0
	for _, d := range view.Defenders {
		if !squadDefenderEligible(d) || !positive(d.Armed) {
			continue
		}
		ranged := positive(d.RangedEquipped)
		role := CombatRole{Pawn: d.ID, Ranged: ranged}
		switch {
		case mode == SiegeSortie:
			role.Target = top
		case !ranged:
			continue
		case i < len(firing):
			c := firing[i]
			role.Cell = &c
			i++
		}
		roles = append(roles, role)
	}
	if mode == SiegeHarass {
		roles = harassRoles(view, roles)
	}
	return sortRoles(roles)
}

// reformSiege is the siege row of the reaction table: a siege starting, a
// mode change, the lord assaulting (no besieger left), or a sortie target
// down re-forms.
func reformSiege(view CombatView, m CombatMemory) bool {
	mode := siegeMode(view, m)
	return m.Tactic != TacticSiege || mode != m.SiegeMode || squadTargetDown(view, m)
}

// siegeHold clears the targets focus fire gave a siege's home gunners
// (every role but a harasser, outside the sortie): an attack order on a
// hostile at the camp walks the gunner out to it.
func siegeHold(m *CombatMemory) {
	if m.Tactic != TacticSiege || m.SiegeMode == SiegeSortie {
		return
	}
	for i := range m.Roles {
		if m.Roles[i].Duty != DutyHarasser {
			m.Roles[i].Target = ""
		}
	}
}
