package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func hackSnapshot() RoundsFacts {
	target := QuestHackTarget{ID: "terminal", Map: 2, Spawned: domain.Known(true), Hacked: domain.Known(false), Satisfied: domain.Known(false), LockedOut: domain.Known(false), Autohack: domain.Known(false), EligiblePawnIDs: []domain.PawnID{"hacker"}}
	return RoundsFacts{QuestOffers: domain.Known([]JoinerOffer{{Quest: "hack", ScriptDef: "Hack_Spacedrone", State: "Ongoing", Profile: domain.Known(QuestProfile{Family: QuestFamilyHack}), Objectives: []QuestObjective{{HackTargets: []QuestHackTarget{target}}}}})}
}

func TestQuestHackDesignationWaitsForNativeCompletion(t *testing.T) {
	f := hackSnapshot()
	got, err := SelectQuestHack(f, 2)
	if err != nil || got.Hack == nil || got.Waiting {
		t.Fatalf("hack available: %+v %v", got, err)
	}
	offers, _ := f.QuestOffers.Value()
	offers[0].Objectives[0].HackTargets[0].Autohack = domain.Known(true)
	f.QuestOffers = domain.Known(offers)
	if got, err = SelectQuestHack(f, 2); err != nil || got.Hack != nil || !got.Waiting {
		t.Fatalf("designation repeated: %+v %v", got, err)
	}
	offers[0].Objectives[0].HackTargets[0].Hacked = domain.Known(true)
	offers[0].Objectives[0].HackTargets[0].Satisfied = domain.Known(true)
	f.QuestOffers = domain.Known(offers)
	if got, err = SelectQuestHack(f, 2); err != nil || got.Quest != "" {
		t.Fatalf("completed hack repeated: %+v %v", got, err)
	}
}

func TestQuestHackWorshippedTerminalRefusesExactNonhostileTribe(t *testing.T) {
	f := hackSnapshot()
	offers, _ := f.QuestOffers.Value()
	offer := offers[0]
	offer.ScriptDef = "Hack_WorshippedTerminal"
	offer.FactionHostile = domain.Known(true) // unrelated first faction cannot authorize the target tribe
	offer.Objectives[0].HackRisk = domain.Known(QuestHackRisk{FactionID: "tribe", Hostile: domain.Known(false)})
	if got := WorshippedTerminalAdmission(offer); got != "worshipped_terminal_trap" {
		t.Fatal(got)
	}
	offer.Objectives[0].HackRisk = domain.Unknown[QuestHackRisk]()
	if got := WorshippedTerminalAdmission(offer); got != "tribe_unknown" {
		t.Fatal(got)
	}
	offer.Objectives[0].HackRisk = domain.Known(QuestHackRisk{FactionID: "tribe", Hostile: domain.Known(true)})
	if got := WorshippedTerminalAdmission(offer); got != "" {
		t.Fatal(got)
	}
}

func TestRelicParentAdmissionRequiresAvailableHackingCrew(t *testing.T) {
	offer := JoinerOffer{ScriptDef: "RelicHunt"}
	f := RoundsFacts{QuestColonyCalm: domain.Known(true), QuestSparePawns: domain.Known([]PawnID{"hacker"}), QuestWorkers: domain.Known([]WorkPawn{{ID: "hacker", Available: domain.Known(true), Work: domain.Known([]WorkPriority{{Work: WorkResearch, Priority: 1}})}})}
	if got := RelicAdmission(offer, f); got != "" {
		t.Fatalf("baseparent incorrectly requires a spawnedsite: %s", got)
	}
	f.QuestColonyCalm = domain.Known(false)
	if got := RelicAdmission(offer, f); got != "colony_busy" {
		t.Fatal(got)
	}
	f.QuestColonyCalm = domain.Known(true)
	f.QuestWorkers = domain.Known([]WorkPawn{})
	if got := RelicAdmission(offer, f); got != "hacker_unavailable" {
		t.Fatal(got)
	}
}

func TestRelicSpacedroneSubquestRiskUsesObservedRaidBudget(t *testing.T) {
	offer := JoinerOffer{ScriptDef: "Hack_Spacedrone", ThreatPoints: domain.Known(200.0)}
	f := RoundsFacts{QuestColonyCalm: domain.Known(true), DefenseCapacity: domain.Known(300.0)}
	if got := HackAdmission(offer, f); got != "" {
		t.Fatal(got)
	}
	f.DefenseCapacity = domain.Known(199.0)
	if got := HackAdmission(offer, f); got != "defense_capacity" {
		t.Fatal(got)
	}
	offer.ThreatPoints = domain.Unknown[float64]()
	if got := HackAdmission(offer, f); got != "threat_unknown" {
		t.Fatal(got)
	}
}

func giftSnapshot() RoundsFacts {
	return RoundsFacts{QuestOffers: domain.Known([]JoinerOffer{{Quest: "charity", ScriptDef: "Beggars", State: "Ongoing", Objectives: []QuestObjective{{Gift: domain.Known(QuestGiftRequest{Recipient: "recipient", Map: 2, Def: "Silver", Remaining: domain.Known(int64(100)), EligiblePawnIDs: []domain.PawnID{"hauler"}, PawnIDs: []domain.PawnID{"recipient", "visitor"}})}}}}), Resources: domain.Known([]Amount{{Resource: "Silver", Count: 400}}), ResourceNeeds: map[Resource]int64{"Silver": 300}, Items: ItemFacts{Market: map[Resource]float64{"Silver": 1}}}
}

func TestBeggarsGiftAffordabilityAndNativeRemaining(t *testing.T) {
	f := giftSnapshot()
	got, err := SelectQuestGift(f, 2)
	if err != nil || got.Gift == nil {
		t.Fatalf("exact affordable gift: %+v %v", got, err)
	}
	f.Resources = domain.Known([]Amount{{Resource: "Silver", Count: 399}})
	if got, err = SelectQuestGift(f, 2); err != nil || got.Gift != nil || got.Reason != "gift_unaffordable" {
		t.Fatalf("reserve spent: %+v %v", got, err)
	}
	f = giftSnapshot()
	offers, _ := f.QuestOffers.Value()
	request, _ := offers[0].Objectives[0].Gift.Value()
	request.Remaining = domain.Known(int64(701))
	offers[0].Objectives[0].Gift = domain.Known(request)
	f.QuestOffers = domain.Known(offers)
	if got, err = SelectQuestGift(f, 2); err != nil || got.Gift != nil || got.Reason != "gift_value_limit" {
		t.Fatalf("gift wealth cap ignored: %+v %v", got, err)
	}
	request.Remaining = domain.Known(int64(0))
	offers[0].Objectives[0].Gift = domain.Known(request)
	f.QuestOffers = domain.Known(offers)
	if got, err = SelectQuestGift(f, 2); err != nil || got.Gift != nil || got.Reason != "" {
		t.Fatalf("fulfilled request repeated: %+v %v", got, err)
	}
}

func TestBeggarsProtectEveryVisitorUntilNativeQuestEnds(t *testing.T) {
	f := giftSnapshot()
	offers, _ := f.QuestOffers.Value()
	offers[0].Profile = domain.Known(QuestFamilyForRoot("Beggars"))
	profile, _ := offers[0].Profile.Value()
	if profile.NeverAct || profile.Disposition != QuestFollow || profile.Cost == QuestCostFree || !profile.ProtectGuests {
		t.Fatalf("unfunded or unprotected beggars: %+v", profile)
	}
	ids := ProtectedQuestGuestIDs(domain.Known(offers))
	if !ids["recipient"] || !ids["visitor"] || ids["hauler"] {
		t.Fatal(ids)
	}
	row := CustodyFacts{Pawn: "visitor", QuestProtected: ids["visitor"], Dead: domain.Known(false), Downed: domain.Known(true), Guest: domain.Known(false), Admitted: domain.Known(false), Prisoner: domain.Known(false), Hostile: domain.Known(true), Recruitable: domain.Known(true), WearingApparel: domain.Known(true)}
	if choice := SelectCustodyMethod(domain.Known([]CustodyFacts{row})); choice.Pawn != "" {
		t.Fatalf("protected visitor captured: %+v", choice)
	}
	offers[0].State = "EndedSuccess"
	if ids = ProtectedQuestGuestIDs(domain.Known(offers)); len(ids) != 0 {
		t.Fatal(ids)
	}
}

func TestQuestHackWaitsWhenNativeWorkersOrCurrentMapUnavailable(t *testing.T) {
	f := hackSnapshot()
	if got, err := SelectQuestHack(f, 1); err != nil || got.Hack != nil || got.Reason != "off_map" {
		t.Fatalf("wrong map: %+v %v", got, err)
	}
	offers, _ := f.QuestOffers.Value()
	offers[0].Objectives[0].HackTargets[0].EligiblePawnIDs = nil
	f.QuestOffers = domain.Known(offers)
	if got, err := SelectQuestHack(f, 2); err != nil || got.Hack != nil || got.Reason != "hacker_unavailable" {
		t.Fatalf("normal hacker absent: %+v %v", got, err)
	}
	offers[0].Objectives[0].HackTargets[0].LockedOut = domain.Known(true)
	f.QuestOffers = domain.Known(offers)
	if got, err := SelectQuestHack(f, 2); err != nil || got.Hack != nil || !got.Waiting || got.Reason != "" {
		t.Fatalf("lockout wait: %+v %v", got, err)
	}
}

func TestQuestHackCompletionUsesExactNativeDestroyedPredicate(t *testing.T) {
	f := hackSnapshot()
	offers, _ := f.QuestOffers.Value()
	offer := offers[0]
	if QuestHackComplete(offer) || !IdeologyWorkDeficit(f) {
		t.Fatal("unfinished hack cleared")
	}
	if QuestHackComplete(JoinerOffer{}) {
		t.Fatal("missing targets completed")
	}
	// Native AllThingsHackedOrDestroyed can satisfy a destroyed, unhacked target.
	offer.Objectives[0].HackTargets[0].Spawned = domain.Known(false)
	offer.Objectives[0].HackTargets[0].Satisfied = domain.Known(true)
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	if !QuestHackComplete(offer) || IdeologyWorkDeficit(f) {
		t.Fatal("native destroyed completion ignored")
	}
	if got, err := SelectQuestHack(f, 2); err != nil || got.Hack != nil || got.Quest != "" {
		t.Fatalf("destroyed completed target driven: %+v %v", got, err)
	}
	offer.Objectives[0].HackTargets[0].Satisfied = domain.Known(false)
	if QuestHackComplete(offer) {
		t.Fatal("destruction bypassed native filter")
	}
}

func TestBeggarsWorkDeficitWaitsForObservedRemainingRequest(t *testing.T) {
	f := giftSnapshot()
	offers, _ := f.QuestOffers.Value()
	offers[0].Profile = domain.Known(QuestFamilyForRoot("Beggars"))
	f.QuestOffers = domain.Known(offers)
	if !IdeologyWorkDeficit(f) {
		t.Fatal("request did not open concern")
	}
	request, _ := offers[0].Objectives[0].Gift.Value()
	request.Remaining = domain.Known(int64(0))
	offers[0].Objectives[0].Gift = domain.Known(request)
	f.QuestOffers = domain.Known(offers)
	if IdeologyWorkDeficit(f) {
		t.Fatal("fulfilled request held concern")
	}
	request.Remaining = domain.Unknown[int64]()
	offers[0].Objectives[0].Gift = domain.Known(request)
	f.QuestOffers = domain.Known(offers)
	if !IdeologyWorkDeficit(f) {
		t.Fatal("unknown request silently cleared")
	}
}
