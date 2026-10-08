package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func QuestRefugeeIDs(offers domain.Fact[[]JoinerOffer]) map[domain.PawnID]domain.QuestID {
	rows, _ := offers.Value()
	out := map[domain.PawnID]domain.QuestID{}
	for _, offer := range rows {
		profile, known := offer.Profile.Value()
		if offer.State != "Ongoing" || !known || profile.Family != QuestFamilyRefugeePod && profile.Family != QuestFamilySite {
			continue
		}
		for _, objective := range offer.Objectives {
			if objective.Kind == o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_RESCUE_PAWNS {
				for _, id := range objective.PawnIDs {
					out[id] = offer.Quest
				}
			}
		}
	}
	return out
}

func RefugeeDeficit(f RoundsFacts) bool { return len(QuestRefugeeIDs(f.QuestOffers)) > 0 }
