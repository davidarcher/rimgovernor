package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// mechKiteLead is kiteLead for a mech raid (#923): unsupported slow mechs
// (centipedes, breachers) are kited like a slow pack. Every live mech must
// be slower than a colonist, so a raid with fast support (scythers) is
// never kited. Under the hold the lead cell is the inner-line cell
// farthest behind the line; under the sapper tactic it is the predicted
// breach's first gunner post, inside the room.
func mechKiteLead(view CombatView, m CombatMemory) (domain.Cell, float64, bool) {
	if m.Tactic != TacticHold && m.Tactic != TacticSapper || !mechRaid(view) {
		return domain.Cell{}, 0, false
	}
	fastest := 0.0
	for _, h := range rankThreats(view) {
		if h.MoveSpeed <= 0 || h.MoveSpeed >= colonistMoveSpeed {
			return domain.Cell{}, 0, false
		}
		fastest = max(fastest, h.MoveSpeed)
	}
	if m.Tactic == TacticHold {
		lure, ok := rearmostRetreat(view)
		return lure, fastest, ok
	}
	b, ok := predictBreach(view)
	if !ok {
		return domain.Cell{}, 0, false
	}
	posts := breachPosts(b, sapperInset, sapperSpread, 1)
	if len(posts) == 0 {
		return domain.Cell{}, 0, false
	}
	return posts[0], fastest, true
}
