package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func odysseyOffer(id, script string, scope QuestScope, edit func(*JoinerOffer)) JoinerOffer {
	offer := JoinerOffer{Quest: domain.QuestID(id), ScriptDef: script, State: "NotYetAccepted", CanAccept: true,
		Profile:      domain.Known(QuestFamilyForRoot(script)),
		ThreatPoints: domain.Known(100.0), Class: domain.Known(QuestClass{Scope: scope})}
	if scope == QuestScopeShipOnly {
		offer.Class = domain.Known(QuestClass{Scope: scope, SpaceLayer: "Orbit"})
	}
	if edit != nil {
		edit(&offer)
	}
	return offer
}

// TestSelectOdysseyQuestMethodAcceptsGroundSkipsShipOnly (#1717): a ground
// Odyssey offer is accepted (the lowest quest ID first, reward index -1
// without a choice part), a ship-only one is skipped with its reason and
// layer, and a ship-only quest is no population deficit.
func TestSelectOdysseyQuestMethodAcceptsGroundSkipsShipOnly(t *testing.T) {
	t.Parallel()
	offers := domain.Known([]JoinerOffer{
		odysseyOffer("Quest_9", "OrbitalFugitive", QuestScopeShipOnly, nil),
		odysseyOffer("Quest_7", "MechanoidSignal", QuestScopeGround, nil),
		odysseyOffer("Quest_5", "SurveySite", QuestScopeGround, func(o *JoinerOffer) { o.ChoiceCount = 1 }),
		odysseyOffer("Quest_2", "ThreatReward_Raid_Joiner", QuestScopeOther, nil),
	})
	if choice := SelectQuestMethod(questTestFacts(offers)); choice.Reason != "" || choice.Quest != "Quest_7" || choice.RewardChoice != -1 {
		t.Fatalf("choice %+v", choice)
	}
	if deficit, known := QuestDeficit(questTestFacts(offers)).Value(); !known || !deficit {
		t.Fatal("an answerable ground quest is a population deficit")
	}
	want := []QuestSkip{{Quest: "Quest_5", ScriptDef: "SurveySite", Reason: QuestSkipNeedsRemoteSiteHold}, {Quest: "Quest_9", ScriptDef: "OrbitalFugitive", Reason: QuestSkipShipOnly, Detail: "Orbit"}}
	if got := QuestSkips(questTestFacts(offers)); !reflect.DeepEqual(got, want) {
		t.Fatalf("skips %+v", got)
	}
	shipOnly := domain.Known([]JoinerOffer{odysseyOffer("Quest_9", "OrbitalFugitive", QuestScopeShipOnly, nil)})
	if choice := SelectQuestMethod(questTestFacts(shipOnly)); choice.Reason != QuestNoOffer || choice.Quest != "" {
		t.Fatalf("a ship-only quest was chosen: %+v", choice)
	}
	if deficit, known := QuestDeficit(questTestFacts(shipOnly)).Value(); !known || deficit {
		t.Fatal("a skipped quest is no deficit")
	}
	if got := QuestSkips(questTestFacts(shipOnly)); len(got) != 1 || got[0].Reason != QuestSkipShipOnly {
		t.Fatalf("skips %+v", got)
	}
	// Without a choice part the write names no reward.
	plain := domain.Known([]JoinerOffer{odysseyOffer("Quest_1", "MechanoidSignal", QuestScopeGround, nil)})
	if choice := SelectQuestMethod(questTestFacts(plain)); choice.Quest != "Quest_1" || choice.RewardChoice != -1 {
		t.Fatalf("choice %+v", choice)
	}
}

// TestSelectOdysseyQuestMethodRefusesWithAReason (#1717): every offer the
// colony does not accept is a named skip or waits on the game, never a
// silent accept.
func TestSelectOdysseyQuestMethodRefusesWithAReason(t *testing.T) {
	t.Parallel()
	for name, c := range map[string]struct {
		edit   func(*JoinerOffer)
		reason QuestSkipReason
	}{
		"unclassified":     {func(o *JoinerOffer) { o.Profile = domain.Unknown[QuestProfile](); o.ClassError = "no row" }, QuestSkipClassUnknown},
		"unknown scope":    {func(o *JoinerOffer) { o.Profile = domain.Known(QuestProfile{}) }, QuestSkipClassUnknown},
		"several rewards":  {func(o *JoinerOffer) { o.ChoiceCount = 2 }, QuestSkipRewardChoices},
		"needs accepter":   {func(o *JoinerOffer) { o.RequiresAccepter = true }, QuestSkipNeedsAccepter},
		"cannot accept":    {func(o *JoinerOffer) { o.CanAccept = false }, "cannot_accept"},
		"already accepted": {func(o *JoinerOffer) { o.State = "Ongoing" }, ""},
	} {
		offers := domain.Known([]JoinerOffer{odysseyOffer("Quest_3", "MechanoidSignal", QuestScopeGround, c.edit)})
		if choice := SelectQuestMethod(questTestFacts(offers)); choice.Reason != QuestNoOffer {
			t.Errorf("%s: chose %+v", name, choice)
		}
		skips := QuestSkips(questTestFacts(offers))
		switch {
		case c.reason == "" && len(skips) != 0:
			t.Errorf("%s: skipped %+v", name, skips)
		case c.reason != "" && (len(skips) != 1 || skips[0].Reason != c.reason):
			t.Errorf("%s: skips %+v, want %s", name, skips, c.reason)
		}
	}
	if skips := QuestSkips(questTestFacts(domain.Known([]JoinerOffer{odysseyOffer("Quest_3", "ThreatReward_Raid_Joiner", QuestScopeOther, nil)}))); len(skips) != 0 {
		t.Errorf("a non-Odyssey quest was skipped: %+v", skips)
	}
	unknown := domain.Unknown[[]JoinerOffer]()
	if choice := SelectQuestMethod(questTestFacts(unknown)); choice.Reason != JoinerCensusUnknown {
		t.Errorf("unknown census: %+v", choice)
	}
	if _, known := QuestDeficit(questTestFacts(unknown)).Value(); known {
		t.Error("unknown census reads as a known deficit")
	}
	if QuestSkips(questTestFacts(unknown)) != nil {
		t.Error("unknown census lists skips")
	}
}

// A ship-only quest is never accepted for Empire favor either (#1717).
func TestEmpireSelectorSkipsShipOnlyQuests(t *testing.T) {
	t.Parallel()
	offers := domain.Known([]JoinerOffer{empireOffer("Quest_4", func(o *JoinerOffer) {
		o.Class = domain.Known(QuestClass{Scope: QuestScopeShipOnly, SpaceLayer: "Orbit"})
		o.Profile = domain.Known(QuestFamilyForRoot("OrbitalFugitive"))
	})})
	if choice := SelectQuestMethod(questTestFacts(offers)); choice.Reason != QuestNoOffer {
		t.Fatalf("chose %+v", choice)
	}
}
