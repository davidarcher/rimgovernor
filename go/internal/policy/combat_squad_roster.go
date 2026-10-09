package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// squadHurtHealth is the native combat-health threshold: a defender at or
// below it is not selected for a squad and a role holder at it falls back.
const squadHurtHealth = float64(float32(0.5005))

// squadHurt are the squad's role holders that are alive, at or below
// squadHurtHealth and not yet pulled back.
func squadHurt(view CombatView, roles []CombatRole) []domain.PawnID {
	live := view.live()
	var out []domain.PawnID
	for _, r := range roles {
		if r.Retreat || !live[r.Pawn] {
			continue
		}
		for _, d := range view.Defenders {
			if health, ok := d.HealthFraction.Value(); d.ID == r.Pawn && ok && health <= squadHurtHealth {
				out = append(out, r.Pawn)
			}
		}
	}
	return out
}

// squadRosterChanged reports a squad fight that lost a fighter: a defender
// downed at this stop, or a role holder hurt past the selector's gate and
// not yet pulled back. A re-formation draws the replacements.
func squadRosterChanged(view CombatView, stop StopEvent, m CombatMemory) bool {
	if stop.Kind == StopDowned && isDefender(view, stop.Pawn) {
		return true
	}
	return len(squadHurt(view, m.Roles)) > 0
}

// squadFallBack keeps a squad formation's hurt former holders out of the
// fight: each moves away from the hostiles as the shelter tactic's
// colonists do, on a Retreat role the next re-formation carries over while
// the pawn stays out of the new formation. prev are the roles before the
// re-formation; formed the new ones.
func squadFallBack(view CombatView, geometry GeometryReply, unreachable []domain.Cell, prev, formed []CombatRole) []CombatRole {
	live := view.live()
	hurt := map[domain.PawnID]bool{}
	for _, id := range squadHurt(view, prev) {
		hurt[id] = true
	}
	for _, r := range prev {
		if r.Retreat && live[r.Pawn] {
			hurt[r.Pawn] = true
		}
	}
	for _, r := range formed {
		delete(hurt, r.Pawn)
	}
	if len(hurt) == 0 {
		return formed
	}
	formed = append(slices.Clone(formed), shelterRolesFor(view, geometry, unreachable, func(d SquadDefenderFacts) bool { return hurt[d.ID] })...)
	return sortRoles(formed)
}
