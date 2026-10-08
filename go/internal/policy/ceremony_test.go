package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func pendingCeremony(edit func(*BestowingCeremony)) RoyaltyFacts {
	c := BestowingCeremony{Quest: "Quest_4", Pawn: "Alice", Bestower: "Envoy", Title: "Knight", Accepted: domain.Known(true),
		BestowerWaiting: domain.Known(true), Started: domain.Known(false), Attendees: []PawnID{"Bob"}}
	if edit != nil {
		edit(&c)
	}
	return RoyaltyFacts{Ladder: throneLadder(), Holders: map[PawnID][]RoyalHolding{"Alice": royalHolder("Yeoman", 0)}, Ceremonies: []BestowingCeremony{c}}
}

// The room the ceremony needs is the bestowed title's, not the favor-driven
// next rung of whoever holds favor.
func TestCeremonyThroneNeedIsTheBestowedTitle(t *testing.T) {
	f := pendingCeremony(func(c *BestowingCeremony) { c.Title = "Baron" })
	f.Holders["Alice"] = royalHolder("Yeoman", 0)
	need, ok := CeremonyThroneNeed(f)
	if !ok || need.Title != "Baron" || need.Holder != "Alice" || need.MinArea != 48 {
		t.Fatalf("%+v %v", need, ok)
	}
	for name, edit := range map[string]func(*BestowingCeremony){
		"offered":       func(c *BestowingCeremony) { c.Accepted = domain.Known(false) },
		"unread":        func(c *BestowingCeremony) { c.Accepted = domain.Unknown[bool]() },
		"no throne":     func(c *BestowingCeremony) { c.Title = "Yeoman" },
		"unknown title": func(c *BestowingCeremony) { c.Title = "Duke" },
	} {
		if need, ok := CeremonyThroneNeed(pendingCeremony(edit)); ok {
			t.Errorf("%s: %+v", name, need)
		}
	}
}

func TestCeremonyHoldsTheColonistAndAttendeesOffSleep(t *testing.T) {
	hold := CeremonyHold(pendingCeremony(nil))
	if !hold["Alice"] || !hold["Bob"] || hold["Envoy"] || len(hold) != 2 {
		t.Fatalf("%v", hold)
	}
	if CeremonyHold(pendingCeremony(func(c *BestowingCeremony) { c.Accepted = domain.Known(false) })) != nil {
		t.Fatal("an unaccepted offer holds nobody")
	}
	if CeremonyHoldOf(domain.Unknown[RoyaltyFacts]()) != nil {
		t.Fatal("unknown royalty holds nobody")
	}
}

func TestPlanSchedulesHeldKeepsTheCeremonyColonistAwake(t *testing.T) {
	schedulePawn := func(id PawnID) WorkPawn {
		p := testWorkPawn(id, true, false, nil)
		p.Schedule = domain.Known(nativeDefaultSchedule())
		return p
	}
	alice, bob := schedulePawn("Alice"), schedulePawn("Bob")
	plain := PlanSchedules([]WorkPawn{alice, bob}, domain.Unknown[ComfortObservation](), false)
	held := PlanSchedulesHeld([]WorkPawn{alice, bob}, domain.Unknown[ComfortObservation](), false, map[PawnID]bool{"Alice": true})
	count := func(slots []string) int {
		n := 0
		for _, s := range slots {
			if s == ScheduleSleep {
				n++
			}
		}
		return n
	}
	if count(plain.Schedules[0].Slots) == 0 || count(held.Schedules[0].Slots) != 0 {
		t.Fatalf("held Alice: plain %v held %v", plain.Schedules[0].Slots, held.Schedules[0].Slots)
	}
	if count(held.Schedules[1].Slots) != count(plain.Schedules[1].Slots) {
		t.Fatal("Bob is not held")
	}
}

func TestClaimQuestsFollowTheClaimGate(t *testing.T) {
	f := pendingCeremony(func(c *BestowingCeremony) { c.Accepted = domain.Known(false) })
	claim := TitleClaim{Holder: "Alice", Title: "Knight", Claim: true}
	if got := ClaimQuests(f, claim); len(got) != 1 || got[0] != "Quest_4" {
		t.Fatalf("%v", got)
	}
	for name, c := range map[string]TitleClaim{
		"held":        {Holder: "Alice", Title: "Knight", Reason: ClaimThroneUnmet},
		"other pawn":  {Holder: "Bob", Title: "Knight", Claim: true},
		"other title": {Holder: "Alice", Title: "Baron", Claim: true},
	} {
		if got := ClaimQuests(f, c); got != nil {
			t.Errorf("%s: %v", name, got)
		}
	}
	if got := ClaimQuests(pendingCeremony(nil), claim); got != nil {
		t.Fatalf("an accepted quest is not offered: %v", got)
	}
}

func bestowingOffer(edit func(*JoinerOffer)) JoinerOffer {
	o := JoinerOffer{Quest: "Quest_4", ScriptDef: "BestowingCeremony", State: "NotYetAccepted", CanAccept: true, OnMap: true}
	if edit != nil {
		edit(&o)
	}
	return o
}

func TestSelectEmpireQuestMethodAcceptsTheClaimedCeremony(t *testing.T) {
	claims := []domain.QuestID{"Quest_4"}
	offers := domain.Known([]JoinerOffer{empireOffer("Quest_2", nil), bestowingOffer(nil)})
	if got := SelectQuestMethod(questTestFacts(offers, claims...)); got.Reason != "" || got.Quest != "Quest_4" || got.RewardChoice != -1 {
		t.Fatalf("the allowed claim outranks a favor quest: %+v", got)
	}
	if got := SelectQuestMethod(questTestFacts(offers)); got.Quest != "Quest_2" {
		t.Fatalf("without a claim the favor quest stands: %+v", got)
	}
	if deficit, _ := QuestDeficit(questTestFacts(domain.Known([]JoinerOffer{bestowingOffer(nil)}), claims...)).Value(); !deficit {
		t.Fatal("an allowed claim is a population deficit")
	}
	for name, edit := range map[string]func(*JoinerOffer){
		"accepted":     func(o *JoinerOffer) { o.State = "Ongoing" },
		"cannot":       func(o *JoinerOffer) { o.CanAccept = false },
		"needs accept": func(o *JoinerOffer) { o.RequiresAccepter = true },
		"other quest":  func(o *JoinerOffer) { o.Quest = "Quest_9" },
	} {
		offers := domain.Known([]JoinerOffer{bestowingOffer(edit)})
		if got := SelectQuestMethod(questTestFacts(offers, claims...)); got.Reason != QuestNoOffer {
			t.Errorf("%s: %+v", name, got)
		}
		if deficit, _ := QuestDeficit(questTestFacts(offers, claims...)).Value(); deficit {
			t.Errorf("%s: deficit", name)
		}
	}
}
