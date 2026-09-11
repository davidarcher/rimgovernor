package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Compare independent same-tick censuses before deriving available armed capacity.
// Missing or contradictory pawn details must not recover a defense deficit.
func routineArmed(colony *o.ColonyFactsSnapshot, emergency policy.EmergencyFacts, pawns *o.PawnSnapshot) domain.Fact[int64] {
	unknown := domain.Unknown[int64]()
	if colony == nil || colony.ColonistCount == nil || int(colony.GetColonistCount()) != len(emergency.Colonists) || len(pawns.Pawns) != len(emergency.Colonists) {
		return unknown
	}
	byID := make(map[string]*o.PawnState, len(pawns.Pawns))
	for _, row := range pawns.Pawns {
		byID[row.Pawn.GetId()] = row
	}
	var armed int64
	for _, pawn := range emergency.Colonists {
		row := byID[string(pawn.ID)]
		dead, dk := pawn.Dead.Value()
		downed, nk := pawn.Downed.Value()
		if row == nil || !dk || !nk || row.Dead == nil || row.Downed == nil || row.GetDead() != dead || row.GetDowned() != downed || row.Colonist == nil || !row.GetColonist() {
			return unknown
		}
		if dead || downed {
			continue
		}
		if row.Equipment == nil || row.Equipment.Armed == nil {
			return unknown
		}
		if row.Equipment.GetArmed() {
			armed++
		}
	}
	return domain.Known(armed)
}
