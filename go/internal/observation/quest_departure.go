package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func projectDepartureStrength(candidates domain.Fact[[]policy.QuestDeparturePawn], defenders domain.Fact[[]policy.SquadDefenderFacts]) domain.Fact[[]policy.QuestDeparturePawn] {
	rows, known := candidates.Value()
	if !known {
		return domain.Unknown[[]policy.QuestDeparturePawn]()
	}
	rows = append([]policy.QuestDeparturePawn(nil), rows...)
	fighters, _ := defenders.Value()
	byID := map[domain.PawnID]policy.SquadDefenderFacts{}
	for _, pawn := range fighters {
		byID[pawn.ID] = pawn
	}
	for i := range rows {
		if fighting, known := rows[i].CanFight.Value(); known && !fighting {
			rows[i].DefensePoints = domain.Known(0.0)
			continue
		}
		pawn, found := byID[domain.PawnID(rows[i].ID)]
		if !found {
			continue
		}
		melee, mk := pawn.MeleePower.Value()
		ranged, rk := pawn.RangedDPS.Value()
		if mk && rk {
			rows[i].DefensePoints = domain.Known((melee + ranged) * policy.DefensePointsPerDPS)
		}
	}
	return domain.Known(rows)
}
