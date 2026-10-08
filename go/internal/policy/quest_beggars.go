package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"slices"
	"sort"
)

type QuestGiftWork struct {
	Quest   domain.QuestID
	Gift    *domain.GiveItem
	Waiting bool
	Reason  QuestSkipReason
}

func SelectQuestGift(f RoundsFacts, currentMap domain.MapID) (QuestGiftWork, error) {
	offers, known := f.QuestOffers.Value()
	if !known {
		return QuestGiftWork{}, nil
	}
	offers = slices.Clone(offers)
	sort.Slice(offers, func(i, j int) bool { return offers[i].Quest < offers[j].Quest })
	for _, offer := range offers {
		if offer.State != "Ongoing" || offer.ScriptDef != "Beggars" {
			continue
		}
		result := QuestGiftWork{Quest: offer.Quest, Waiting: true}
		seen := false
		for _, objective := range offer.Objectives {
			request, known := objective.Gift.Value()
			if !known {
				continue
			}
			seen = true
			remaining, rk := request.Remaining.Value()
			if !rk {
				result.Reason = "gift_request_unknown"
				return result, nil
			}
			if remaining <= 0 {
				continue
			}
			if request.Map != currentMap {
				result.Reason = "off_map"
				return result, nil
			}
			stock, sk := f.Resources.Value()
			if !sk {
				result.Reason = "stock_unknown"
				return result, nil
			}
			def := Resource(request.Def)
			value, vk := f.Items.Market[def]
			if !vk || !finite(value) || value <= 0 {
				result.Reason = "gift_value_unknown"
				return result, nil
			}
			if float64(remaining)*value > 700 {
				result.Reason = "gift_value_limit"
				return result, nil
			}
			have := int64(0)
			for _, amount := range stock {
				if amount.Resource == def {
					have += amount.Count
				}
			}
			if have-max(int64(0), f.ResourceNeeds[def]) < remaining {
				result.Reason = "gift_unaffordable"
				return result, nil
			}
			if len(request.HaulingPawnIDs) > 0 {
				return result, nil
			}
			if len(request.EligiblePawnIDs) == 0 {
				result.Reason = "hauler_unavailable"
				return result, nil
			}
			actors := slices.Clone(request.EligiblePawnIDs)
			slices.Sort(actors)
			intent, err := domain.NewGiveItem(actors[0], request.Recipient, request.Def, remaining)
			if err != nil {
				return result, err
			}
			result.Gift = &intent
			result.Waiting = false
			return result, nil
		}
		if !seen {
			result.Reason = "gift_request_unknown"
		}
		return result, nil
	}
	return QuestGiftWork{}, nil
}
