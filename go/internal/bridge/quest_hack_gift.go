package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
)

func decodeQuestHackGift(row *o.QuestObjective, fact *QuestObjectiveFact) error {
	ids := func(values []string) bool {
		seen := map[string]bool{}
		for _, id := range values {
			if validID(id) != nil || seen[id] {
				return false
			}
			seen[id] = true
		}
		return true
	}
	seen := map[string]bool{}
	for _, target := range row.HackTargets {
		if target == nil || validID(target.GetId()) != nil || seen[target.GetId()] || target.GetMapId() < 0 || (target.GetSpawned() && target.MapId == nil) || !ids(target.EligiblePawnIds) || target.Hackable == nil {
			return contract("invalid quest hack target")
		}
		seen[target.GetId()] = true
		for _, v := range []*float64{target.Hackable.ProgressPercent, target.Hackable.Defence} {
			if v != nil && (math.IsNaN(*v) || math.IsInf(*v, 0) || *v < 0) {
				return contract("invalid quest hack state")
			}
		}
		fact.HackTargets = append(fact.HackTargets, proto.Clone(target).(*o.QuestHackTarget))
	}
	if gift := row.GiftRequest; gift != nil {
		if validID(gift.GetRecipientId()) != nil || gift.GetDef() == "" || gift.GetRemaining() < 0 || gift.GetMapId() < 0 || !ids(gift.PawnIds) || !ids(gift.HaulingPawnIds) || !ids(gift.EligiblePawnIds) || (len(gift.EligiblePawnIds) > 0 && gift.MapId == nil) {
			return contract("invalid quest gift request")
		}
		fact.Gift = proto.Clone(gift).(*o.QuestGiftRequest)
	}
	if risk := row.HackRisk; risk != nil {
		if validID(risk.GetFactionId()) != nil {
			return contract("invalid quest hack faction")
		}
		fact.HackRisk = proto.Clone(risk).(*o.QuestHackRisk)
	}
	return nil
}
