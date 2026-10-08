package policy

import (
	"cmp"
	"slices"
)

// QuestReward is typed native benefit evidence. Choice -1 applies to every option.
type QuestReward struct {
	Choice                          int32
	Items                           []Amount
	Goodwill, Psylink, PermitPoints int32
	Permits                         []string
	TitleDef, FactionID             string
}

// QuestRewardValue gives pending titles priority, then observed shortages, then
// useful benefits. Scores compare only quest rewards, never colony purchases.
type QuestRewardValue struct {
	TitleFavor       int64
	Needed, Benefits float64
}

func (v QuestRewardValue) BetterThan(other QuestRewardValue) bool {
	if v.TitleFavor != other.TitleFavor {
		return v.TitleFavor > other.TitleFavor
	}
	if v.Needed != other.Needed {
		return v.Needed > other.Needed
	}
	return v.Benefits > other.Benefits
}

type QuestRewardChoice struct {
	Choice int32
	Value  QuestRewardValue
	Reason QuestSkipReason
}

func questWantsTitles(f RoundsFacts) bool {
	royalty, known := f.Royalty.Value()
	if !known {
		return false
	}
	for _, holdings := range royalty.Holders {
		for _, holding := range holdings {
			rung, more := nextClaim(royalty.Ladder, holding.Title)
			favor, fk := holding.Favor.Value()
			needed, nk := rung.FavorNeeded.Value()
			if more && fk && nk && favor < needed {
				return true
			}
		}
	}
	return false
}

// SelectQuestReward keeps native option indices and aggregates all rows for a
// choice. A multiple-part quest is refused because native cannot choose it.
func SelectQuestReward(offer JoinerOffer, f RoundsFacts) QuestRewardChoice {
	if parts, known := offer.RewardChoiceParts.Value(); known && parts > 1 {
		return QuestRewardChoice{Choice: -1, Reason: "reward_choices"}
	}
	indices := []int32{}
	if offer.ChoiceCount == 0 {
		indices = append(indices, -1)
	} else {
		for i := int32(0); i < offer.ChoiceCount; i++ {
			indices = append(indices, i)
		}
	}
	stock, stockKnown := f.Resources.Value()
	need := map[Resource]int64{}
	if stockKnown {
		for def, floor := range f.ResourceNeeds {
			have := int64(0)
			for _, amount := range stock {
				if amount.Resource == def {
					have += amount.Count
				}
			}
			need[def] = max(0, floor-have)
		}
	}
	if deficits, known := f.ConstructionDeficit.Value(); known {
		for def, count := range deficits {
			need[def] = max(need[def], count)
		}
	}
	titles := questWantsTitles(f)
	hosted := int64(1)
	if colonists, known := f.Colonists.Value(); known {
		hosted = max(1, colonists)
	}
	var best QuestRewardChoice
	for ordinal, index := range indices {
		value := QuestRewardValue{}
		favor := int64(0)
		for _, row := range offer.Favor {
			if row.Choice == index || row.Choice == -1 {
				favor += int64(row.Favor)
			}
		}
		if titles {
			value.TitleFavor = favor
		}
		value.Benefits += float64(favor)
		quantities := map[Resource]int64{}
		for _, row := range offer.Rewards {
			if row.Choice != index && row.Choice != -1 {
				continue
			}
			for _, item := range row.Items {
				quantities[item.Resource] += item.Count
			}
			value.Benefits += float64(max(0, row.Goodwill))
			value.Benefits += float64(row.Psylink) * float64(permitValue(PermitPsycast, true))
			value.Benefits += float64(row.PermitPoints) * float64(permitValue(PermitAid, false))
			if royalty, known := f.Royalty.Value(); known {
				for _, name := range row.Permits {
					if permit, found := royalty.Permits[name]; found {
						value.Benefits += float64(permitValue(permitCategory(permit), len(royalty.Casters) > 0))
					}
				}
			}
		}
		defs := make([]Resource, 0, len(quantities))
		for def := range quantities {
			defs = append(defs, def)
		}
		slices.SortFunc(defs, func(a, b Resource) int { return cmp.Compare(a, b) })
		for _, def := range defs {
			count := quantities[def]
			price := f.Items.Market[def]
			if price <= 0 {
				price = 1
			}
			value.Benefits += float64(count) * price
			value.Needed += float64(min(count, need[def])) * price
			if days, known := f.FoodDays.Value(); known && days < JoinerFoodFloorDays(hosted) {
				value.Needed += float64(count) * f.Items.Nutrition[def]
			}
		}
		if ordinal == 0 || value.BetterThan(best.Value) {
			best = QuestRewardChoice{Choice: index, Value: value}
		}
	}
	return best
}
