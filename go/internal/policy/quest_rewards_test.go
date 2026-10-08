package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func rewardOffer() JoinerOffer {
	return JoinerOffer{ChoiceCount: 2, RewardChoiceParts: domain.Known(int32(1)), Favor: []QuestFavor{{Choice: 0, Favor: 6}}, Rewards: []QuestReward{{Choice: 1, Items: []Amount{{Resource: "Steel", Count: 20}}}}}
}

func TestQuestRewardItemsMeetRecordedShortage(t *testing.T) {
	f := RoundsFacts{Resources: domain.Known([]Amount{{Resource: "Steel", Count: 5}}), ResourceNeeds: map[Resource]int64{"Steel": 25}, Items: ItemFacts{Market: map[Resource]float64{"Steel": 2}}}
	if got := SelectQuestReward(rewardOffer(), f); got.Choice != 1 || got.Value.Needed != 40 {
		t.Fatal(got)
	}
}

func TestQuestRewardPendingTitlePrefersFavor(t *testing.T) {
	f := RoundsFacts{Resources: domain.Known([]Amount{}), ResourceNeeds: map[Resource]int64{"Steel": 1000}, Royalty: domain.Known(RoyaltyFacts{Ladder: []RoyalRung{{Title: "Knight", FavorNeeded: domain.Known(8)}}, Holders: map[PawnID][]RoyalHolding{"pawn": {{Favor: domain.Known(2)}}}})}
	if got := SelectQuestReward(rewardOffer(), f); got.Choice != 0 || got.Value.TitleFavor != 6 {
		t.Fatal(got)
	}
}

func TestQuestRewardUniversalFavorPreservesChoice(t *testing.T) {
	offer := rewardOffer()
	offer.Favor = append(offer.Favor, QuestFavor{Choice: -1, Favor: 100})
	if got := SelectQuestReward(offer, RoundsFacts{ResourceNeeds: map[Resource]int64{"Steel": 20}, Resources: domain.Known([]Amount{})}); got.Choice != 1 {
		t.Fatal(got)
	}
	offer.ChoiceCount = 0
	offer.Rewards = nil
	offer.Favor = []QuestFavor{{Choice: -1, Favor: 4}}
	if got := SelectQuestReward(offer, RoundsFacts{}); got.Choice != -1 || got.Value.Benefits != 4 {
		t.Fatal(got)
	}
}

func TestQuestRewardBenefitsAndNativeChoiceSupport(t *testing.T) {
	for name, reward := range map[string]QuestReward{"items": {Items: []Amount{{Resource: "Medicine", Count: 5}}}, "goodwill": {Goodwill: 15}, "psylink": {Psylink: 1}, "permit": {PermitPoints: 1}} {
		t.Run(name, func(t *testing.T) {
			reward.Choice = 1
			offer := JoinerOffer{ChoiceCount: 2, Rewards: []QuestReward{reward}}
			if got := SelectQuestReward(offer, RoundsFacts{}); got.Choice != 1 || got.Value.Benefits <= 0 {
				t.Fatal(got)
			}
		})
	}
	offer := rewardOffer()
	offer.RewardChoiceParts = domain.Known(int32(2))
	if got := SelectQuestReward(offer, RoundsFacts{}); got.Reason != "reward_choices" {
		t.Fatal(got)
	}
}

func TestQuestRewardAggregatesNeedOnceAndBreaksTiesByIndex(t *testing.T) {
	offer := JoinerOffer{ChoiceCount: 2, Rewards: []QuestReward{{Choice: 0, Items: []Amount{{Resource: "Steel", Count: 10}}}, {Choice: 0, Items: []Amount{{Resource: "Steel", Count: 10}}}, {Choice: 1, Items: []Amount{{Resource: "Steel", Count: 20}}}}}
	f := RoundsFacts{Resources: domain.Known([]Amount{}), ResourceNeeds: map[Resource]int64{"Steel": 10}}
	if got := SelectQuestReward(offer, f); got.Choice != 0 || got.Value.Needed != 10 {
		t.Fatal(got)
	}
}

func TestQuestSelectorAnswersNonFavorRewards(t *testing.T) {
	offer := empireOffer("Quest_Items", func(offer *JoinerOffer) {
		offer.Favor = nil
		offer.Rewards = []QuestReward{{Choice: 1, Items: []Amount{{Resource: "Steel", Count: 20}}}}
		offer.RewardChoiceParts = domain.Known(int32(1))
	})
	f := questTestFacts(domain.Known([]JoinerOffer{offer}))
	f.Resources = domain.Known([]Amount{})
	f.ResourceNeeds = map[Resource]int64{"Steel": 20}
	if got := SelectQuestMethod(f); got.Quest != offer.Quest || got.RewardChoice != 1 || got.Reason != "" {
		t.Fatal(got)
	}
	offer.RewardChoiceParts = domain.Known(int32(2))
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	if skips := QuestSkips(f); len(skips) != 1 || skips[0].Reason != "reward_choices" {
		t.Fatal(skips)
	}
}
