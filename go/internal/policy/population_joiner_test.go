package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func joinerBeds(beds ...SleepingBed) domain.Fact[SleepingObservation] {
	return domain.Known(SleepingObservation{Beds: beds})
}

func spareSleepingBed(id string, owners ...PawnID) SleepingBed {
	return SleepingBed{ID: id, Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: owners}
}

func hostedRow(pawn string, admitted, guest, dead bool) CustodyFacts {
	return CustodyFacts{Pawn: domain.PawnID(pawn), Dead: domain.Known(dead), Admitted: domain.Known(admitted), Guest: domain.Known(guest)}
}

func joinerFacts(t *testing.T, hosted int) JoinerCapacityFacts {
	t.Helper()
	rows := make([]CustodyFacts, 0, hosted)
	for i := 0; i < hosted; i++ {
		rows = append(rows, hostedRow(string(rune('a'+i)), true, false, false))
	}
	return JoinerCapacityFacts{Custody: domain.Known(rows), Sleeping: joinerBeds(spareSleepingBed("bed")), FoodDays: domain.Known(20.0)}
}

func TestIsJoinerOfferNamesOnlyThreatRewardJoinerRoots(t *testing.T) {
	t.Parallel()
	for def, want := range map[string]bool{
		"ThreatReward_Raid_Joiner": true, "ThreatReward_Infestation_Joiner": true, "ThreatReward_Raid_Trade": false,
		"ThreatReward_MechPods_Joiner": false, "ThreatReward_Unknown_Joiner": false,
		"WandererJoins": false, "OpportunitySite_DownedRefugee": false, "TradeRequest": false, "": false,
	} {
		if got := IsJoinerOffer(def); got != want {
			t.Fatalf("IsJoinerOffer(%q) = %v", def, got)
		}
	}
}

func TestJoinerCapacityAdmitsWithoutAPlayerPolicy(t *testing.T) {
	t.Parallel()
	if room, known := JoinerCapacity(joinerFacts(t, 3)).Value(); !known || !room {
		t.Fatal("beds and food alone admit a joiner")
	}
	// A guest and a prisoner count as hosted; the dead do not.
	facts := joinerFacts(t, 0)
	rows := []CustodyFacts{hostedRow("a", true, false, false), hostedRow("g", false, true, false), hostedRow("p", false, true, false), hostedRow("d", true, false, true)}
	facts.Custody = domain.Known(rows)
	facts.FoodDays = domain.Known(JoinerFoodFloorDays(3))
	if room, known := JoinerCapacity(facts).Value(); !known || !room {
		t.Fatal("three hosted at the three-person floor has room")
	}
	facts.Custody = domain.Known(append(rows, hostedRow("e", true, false, false)))
	facts.FoodDays = domain.Known(JoinerFoodFloorDays(4) - 0.01)
	if room, known := JoinerCapacity(facts).Value(); !known || room {
		t.Fatal("the floor rises with the hosted count")
	}
	// An unknown row leaves the count, and so the capacity, unknown.
	facts.Custody = domain.Known([]CustodyFacts{hostedRow("a", true, false, false), {Pawn: "u", Dead: domain.Known(false)}})
	if _, known := JoinerCapacity(facts).Value(); known {
		t.Fatal("an unknown row must not be counted either way")
	}
}

func TestJoinerCapacityStopsAtThePopulationTarget(t *testing.T) {
	t.Parallel()
	facts := joinerFacts(t, domain.PopulationTarget-1)
	if room, known := JoinerCapacity(facts).Value(); !known || !room {
		t.Fatal("one under the target has room")
	}
	facts = joinerFacts(t, domain.PopulationTarget)
	if room, known := JoinerCapacity(facts).Value(); !known || room {
		t.Fatal("the target is a ceiling")
	}
}

func TestJoinerFoodFloorScalesThreeToFifteenDays(t *testing.T) {
	t.Parallel()
	for hosted, want := range map[int64]float64{0: 3, 1: 3, 20: 15, 50: 15} {
		if got := JoinerFoodFloorDays(hosted); got != want {
			t.Fatalf("floor(%d) = %v, want %v", hosted, got, want)
		}
	}
	if a, b := JoinerFoodFloorDays(5), JoinerFoodFloorDays(6); !(3 < a && a < b && b < 15) {
		t.Fatal("floor must rise linearly between the ends", a, b)
	}
}

func TestJoinerCapacityNeedsFoodReserveAndASpareBed(t *testing.T) {
	t.Parallel()
	facts := joinerFacts(t, 1)
	facts.FoodDays = domain.Known(2.5)
	if room, known := JoinerCapacity(facts).Value(); !known || room {
		t.Fatal("a runway under the food floor has no room")
	}
	facts.FoodDays = domain.Known(3.0)
	if room, known := JoinerCapacity(facts).Value(); !known || !room {
		t.Fatal("a runway at the food floor has room")
	}
	facts.FoodDays = domain.Unknown[float64]()
	if _, known := JoinerCapacity(facts).Value(); known {
		t.Fatal("an unknown runway leaves capacity unknown")
	}
	facts.FoodDays = domain.Known(3.0)
	facts.Sleeping = joinerBeds(spareSleepingBed("owned", "a"), SleepingBed{ID: "medical", Humanlike: domain.Known(true), Medical: domain.Known(true), Prisoners: domain.Known(false)}, SleepingBed{ID: "prison", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(true)}, SleepingBed{ID: "animal", Humanlike: domain.Known(false), Medical: domain.Known(false), Prisoners: domain.Known(false)})
	if room, known := JoinerCapacity(facts).Value(); !known || room {
		t.Fatal("owned, medical, prisoner and animal beds are not spare")
	}
	facts.Sleeping = domain.Unknown[SleepingObservation]()
	if _, known := JoinerCapacity(facts).Value(); known {
		t.Fatal("an unknown sleeping census leaves capacity unknown")
	}
}

func TestJoinerDeficitOnlyForAnswerableOffersWithRoom(t *testing.T) {
	t.Parallel()
	offer := JoinerOffer{Quest: "Quest_7", ScriptDef: "ThreatReward_Raid_Joiner", State: "NotYetAccepted", CanAccept: true}
	if _, known := JoinerDeficit(domain.Unknown[[]JoinerOffer](), domain.Known(true)).Value(); known {
		t.Fatal("unknown census must not settle the need")
	}
	if deficit, known := JoinerDeficit(domain.Known([]JoinerOffer{}), domain.Unknown[bool]()).Value(); !known || deficit {
		t.Fatal("no offer is no deficit whatever the capacity")
	}
	for _, other := range []JoinerOffer{
		{Quest: "Quest_1", ScriptDef: "TradeRequest", State: "NotYetAccepted", CanAccept: true},
		{Quest: "Quest_2", ScriptDef: "ThreatReward_Raid_Joiner", State: "Ongoing", CanAccept: false},
		{Quest: "Quest_3", ScriptDef: "ThreatReward_Raid_Joiner", State: "NotYetAccepted", CanAccept: false},
		{Quest: "Quest_4", ScriptDef: "ThreatReward_Raid_Joiner", State: "NotYetAccepted", CanAccept: true, RequiresAccepter: true},
	} {
		if deficit, known := JoinerDeficit(domain.Known([]JoinerOffer{other}), domain.Known(true)).Value(); !known || deficit {
			t.Fatalf("%+v is not answerable", other)
		}
	}
	if deficit, known := JoinerDeficit(domain.Known([]JoinerOffer{offer}), domain.Known(true)).Value(); !known || !deficit {
		t.Fatal("an answerable offer with room is a deficit")
	}
	if deficit, known := JoinerDeficit(domain.Known([]JoinerOffer{offer}), domain.Known(false)).Value(); !known || deficit {
		t.Fatal("an answerable offer without room is left to expire, not a deficit")
	}
	if _, known := JoinerDeficit(domain.Known([]JoinerOffer{offer}), domain.Unknown[bool]()).Value(); known {
		t.Fatal("an answerable offer with unknown room is unknown")
	}
}

func TestSelectJoinerMethodPicksTheLowestAnswerableOffer(t *testing.T) {
	t.Parallel()
	offers := domain.Known([]JoinerOffer{
		{Quest: "Quest_9", ScriptDef: "ThreatReward_Raid_Joiner", State: "NotYetAccepted", CanAccept: true, ChoiceCount: 2},
		{Quest: "Quest_3", ScriptDef: "TradeRequest", State: "NotYetAccepted", CanAccept: true},
		{Quest: "Quest_5", ScriptDef: "ThreatReward_Raid_Joiner", State: "NotYetAccepted", CanAccept: true},
	})
	if got := SelectJoinerMethod(offers, domain.Known(true)); got != (JoinerChoice{Quest: "Quest_5", RewardChoice: -1}) {
		t.Fatalf("%+v", got)
	}
	single := domain.Known([]JoinerOffer{{Quest: "Quest_9", ScriptDef: "ThreatReward_Raid_Joiner", State: "NotYetAccepted", CanAccept: true, ChoiceCount: 2}})
	if got := SelectJoinerMethod(single, domain.Known(true)); got != (JoinerChoice{Quest: "Quest_9", RewardChoice: 0}) {
		t.Fatalf("a reward-choice offer takes the first option: %+v", got)
	}
	if got := SelectJoinerMethod(offers, domain.Known(false)); got.Reason != JoinerNoCapacity {
		t.Fatalf("%+v", got)
	}
	if got := SelectJoinerMethod(offers, domain.Unknown[bool]()); got.Reason != JoinerCensusUnknown {
		t.Fatalf("%+v", got)
	}
	if got := SelectJoinerMethod(domain.Unknown[[]JoinerOffer](), domain.Known(true)); got.Reason != JoinerCensusUnknown {
		t.Fatalf("%+v", got)
	}
	if got := SelectJoinerMethod(domain.Known([]JoinerOffer{{Quest: "Quest_3", ScriptDef: "TradeRequest", State: "NotYetAccepted", CanAccept: true}}), domain.Known(false)); got.Reason != JoinerNoOffer {
		t.Fatalf("no answerable offer is no offer, whatever the capacity: %+v", got)
	}
}

func TestRoundsFactsJoinerCapacityFallsBackToStockRunway(t *testing.T) {
	t.Parallel()
	f := RoundsFacts{FoodDays: domain.Known(4.0)}
	if days, known := f.JoinerCapacity().FoodDays.Value(); !known || days != 4 {
		t.Fatal(f.JoinerCapacity().FoodDays)
	}
}
