package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func frameQuestColonistsAtHome(read *bridge.WorldProgressionRead, home domain.MapID, emergency policy.EmergencyFacts) domain.Fact[int] {
	complete, known := emergency.ColonistsComplete.Value()
	if read == nil || !known || !complete {
		return domain.Unknown[int]()
	}
	away := map[string]bool{}
	for _, caravan := range read.Caravans {
		for _, id := range caravan.PawnIDs {
			away[id] = true
		}
	}
	for _, row := range read.Maps {
		if domain.MapID(row.ID) != home || !row.Home {
			continue
		}
		present := map[string]bool{}
		for _, id := range row.PawnIDs {
			present[id] = true
		}
		n := 0
		for _, pawn := range emergency.Colonists {
			if !present[string(pawn.ID)] || away[string(pawn.ID)] {
				continue
			}
			dead, known := pawn.Dead.Value()
			if !known {
				return domain.Unknown[int]()
			}
			if !dead {
				n++
			}
		}
		return domain.Known(n)
	}
	return domain.Unknown[int]()
}
