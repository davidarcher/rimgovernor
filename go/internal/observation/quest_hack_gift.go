package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func questHackGift(source bridge.QuestObjectiveFact, row *policy.QuestObjective) {
	for _, t := range source.HackTargets {
		target := policy.QuestHackTarget{ID: t.GetId(), Map: domain.MapID(t.GetMapId()), Spawned: optional(t.Spawned), Hacked: optional(t.Hackable.Hacked), LockedOut: optional(t.Hackable.LockedOut), Autohack: optional(t.Hackable.Autohack), ProgressPercent: optional(t.Hackable.ProgressPercent), Defence: optional(t.Hackable.Defence)}
		for _, id := range t.EligiblePawnIds {
			target.EligiblePawnIDs = append(target.EligiblePawnIDs, domain.PawnID(id))
		}
		target.Satisfied = optional(t.Satisfied)
		row.HackTargets = append(row.HackTargets, target)
	}
	if risk := source.HackRisk; risk != nil {
		row.HackRisk = domain.Known(policy.QuestHackRisk{FactionID: risk.GetFactionId(), Hostile: optional(risk.Hostile)})
	}
	if g := source.Gift; g != nil {
		gift := policy.QuestGiftRequest{Recipient: domain.PawnID(g.GetRecipientId()), Def: g.GetDef(), Remaining: optional(g.Remaining), Map: domain.MapID(g.GetMapId())}
		// A pending recipient has no dispatch map; retain an unknown request until arrival.
		if g.MapId == nil {
			return
		}
		for _, id := range g.PawnIds {
			gift.PawnIDs = append(gift.PawnIDs, domain.PawnID(id))
		}
		for _, id := range g.HaulingPawnIds {
			gift.HaulingPawnIDs = append(gift.HaulingPawnIDs, domain.PawnID(id))
		}
		for _, id := range g.EligiblePawnIds {
			gift.EligiblePawnIDs = append(gift.EligiblePawnIDs, domain.PawnID(id))
		}
		row.Gift = domain.Known(gift)
	}
}
