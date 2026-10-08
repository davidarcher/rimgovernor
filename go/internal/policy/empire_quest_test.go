package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func empireOffer(id string, edit func(*JoinerOffer)) JoinerOffer {
	offer := JoinerOffer{Quest: domain.QuestID(id), ScriptDef: "ThreatReward_Raid_MiscReward", Profile: domain.Known(QuestFamilyForRoot("ThreatReward_Raid_MiscReward")), State: "NotYetAccepted", CanAccept: true, ChoiceCount: 2,
		FactionID: "Faction_9", FactionHostile: domain.Known(false), OnMap: true, Favor: []QuestFavor{{Choice: 0, Favor: 2}, {Choice: 1, Favor: 6}}}
	if edit != nil {
		edit(&offer)
	}
	return offer
}

func TestSelectEmpireQuestMethodChoosesTheHighestFavorChoice(t *testing.T) {
	t.Parallel()
	offers := domain.Known([]JoinerOffer{
		empireOffer("Quest_7", func(o *JoinerOffer) { o.Favor = []QuestFavor{{Choice: 0, Favor: 3}} }),
		empireOffer("Quest_3", nil),
		empireOffer("Quest_2", func(o *JoinerOffer) { o.Favor = []QuestFavor{{Choice: 0, Favor: 6}, {Choice: 1, Favor: 1}} }),
	})
	choice := SelectQuestMethod(questTestFacts(offers))
	if choice.Reason != "" || choice.Quest != "Quest_2" || choice.RewardChoice != 0 {
		t.Fatalf("tie on 6 favor goes to the lowest quest: %+v", choice)
	}
	offers = domain.Known([]JoinerOffer{empireOffer("Quest_3", nil), empireOffer("Quest_7", func(o *JoinerOffer) { o.Favor = []QuestFavor{{Choice: 0, Favor: 3}} })})
	if choice = SelectQuestMethod(questTestFacts(offers)); choice.Quest != "Quest_3" || choice.RewardChoice != 1 {
		t.Fatalf("%+v", choice)
	}
	if deficit, known := QuestDeficit(questTestFacts(offers)).Value(); !known || !deficit {
		t.Fatal("an answerable Empire quest is a population deficit")
	}
}

func TestSelectEmpireQuestMethodRefuses(t *testing.T) {
	t.Parallel()
	for name, edit := range map[string]func(*JoinerOffer){
		"hostile":          func(o *JoinerOffer) { o.FactionHostile = domain.Known(true) },
		"faction unread":   func(o *JoinerOffer) { o.FactionHostile = domain.Unknown[bool]() },
		"no faction":       func(o *JoinerOffer) { o.FactionID = "" },
		"off map":          func(o *JoinerOffer) { o.OnMap = false },
		"cannot accept":    func(o *JoinerOffer) { o.CanAccept = false },
		"needs accepter":   func(o *JoinerOffer) { o.RequiresAccepter = true },
		"already accepted": func(o *JoinerOffer) { o.State = "Ongoing" },
		"joiner": func(o *JoinerOffer) {
			o.ScriptDef = "ThreatReward_Raid_Joiner"
			o.Profile = domain.Known(QuestFamilyForRoot(o.ScriptDef))
		},
	} {
		offers := domain.Known([]JoinerOffer{empireOffer("Quest_1", edit)})
		if choice := SelectQuestMethod(questTestFacts(offers)); choice.Reason != QuestNoOffer {
			t.Fatalf("%s: %+v", name, choice)
		}
		if deficit, known := QuestDeficit(questTestFacts(offers)).Value(); !known || deficit {
			t.Fatalf("%s: refused offer is no deficit", name)
		}
	}
	if choice := SelectQuestMethod(questTestFacts(domain.Unknown[[]JoinerOffer]())); choice.Reason != JoinerCensusUnknown {
		t.Fatalf("%+v", choice)
	}
}

func questTestFacts(offers domain.Fact[[]JoinerOffer], claims ...domain.QuestID) RoundsFacts {
	return RoundsFacts{DefenseCapacity: domain.Known(1000.0), QuestOffers: offers, TitleClaimQuests: claims, QuestColonyCalm: domain.Known(true), QuestSparePawns: domain.Known([]PawnID{"spare"}), QuestColonistsAtHome: domain.Known(4), FoodDays: domain.Known(20.0)}
}

func TestFamilyQuestDoesNotRequireFavor(t *testing.T) {
	offer := empireOffer("Quest_1", func(o *JoinerOffer) { o.Favor = nil })
	if choice := SelectQuestMethod(questTestFacts(domain.Known([]JoinerOffer{offer}))); choice.Quest != offer.Quest {
		t.Fatal(choice)
	}
}
