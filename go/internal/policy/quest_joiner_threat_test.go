package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func joinerThreatTestFacts() RoundsFacts {
	return RoundsFacts{QuestColonyCalm: domain.Known(true), DefenseCapacity: domain.Known(100.0)}
}

func TestJoinerRaidAdmissionAndRefusal(t *testing.T) {
	offer := JoinerOffer{Quest: "joiner", ScriptDef: "ThreatReward_Raid_Joiner", Profile: domain.Known(QuestFamilyForRoot("ThreatReward_Raid_Joiner")), State: "NotYetAccepted", CanAccept: true, ThreatPoints: domain.Known(100.0)}
	facts := joinerThreatTestFacts()
	facts.QuestOffers = domain.Known([]JoinerOffer{offer})
	if got := SelectJoinerMethod(facts.QuestOffers, domain.Known(true), facts); got.Quest != offer.Quest {
		t.Fatal(got)
	}
	facts.DefenseCapacity = domain.Known(99.0)
	if got := SelectJoinerMethod(facts.QuestOffers, domain.Known(true), facts); got.Reason != JoinerNoCapacity {
		t.Fatal(got)
	}
	if got := QuestSkips(facts); len(got) != 1 || got[0].Reason != "defense_capacity" {
		t.Fatal(got)
	}
	facts.DefenseCapacity = domain.Known(100.0)
	facts.QuestColonyCalm = domain.Known(false)
	if got := JoinerThreatReason(offer, facts); got != "colony_busy" {
		t.Fatal(got)
	}
	facts.QuestColonyCalm = domain.Known(true)
	offer.ThreatPoints = domain.Unknown[float64]()
	if got := JoinerThreatReason(offer, facts); got != "threat_unknown" {
		t.Fatal(got)
	}
}
