package policy

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// mechRaid reports a raid of mechanoids: at least one live hostile, and
// every live hostile a Mech_ kind.
func mechRaid(view CombatView) bool {
	ranked := rankThreats(view)
	for _, h := range ranked {
		if !strings.HasPrefix(h.Kind, "Mech_") {
			return false
		}
	}
	return len(ranked) > 0
}

// markMechs is the view's positional facts with each Mech_ kind marked,
// so a mech raid's assault lord gets the hold (#922).
func markMechs(view CombatView) []DefensiveThreatFacts {
	mech := mechKinds(view)
	out := slices.Clone(view.Positional)
	for i := range out {
		out[i].Mech = mech[domain.PawnID(out[i].ID)]
	}
	return out
}

// markSquadMechs is the view's squad threats with each Mech_ kind marked,
// so a mech raid without a hold still forms a squad (#970).
func markSquadMechs(view CombatView) []SquadThreatFacts {
	mech := mechKinds(view)
	out := slices.Clone(view.Threats)
	for i := range out {
		out[i].Mech = mech[domain.PawnID(out[i].ID)]
	}
	return out
}

func mechKinds(view CombatView) map[domain.PawnID]bool {
	mech := map[domain.PawnID]bool{}
	for _, p := range view.Pawns {
		mech[p.ID] = strings.HasPrefix(p.Kind, "Mech_")
	}
	return mech
}
