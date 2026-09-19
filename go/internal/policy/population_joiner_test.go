package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func joinerPolicy(t *testing.T, maximum int32, foodDays float64) domain.Fact[domain.PopulationPolicy] {
	t.Helper()
	p, err := domain.NewPopulationPolicy(maximum, foodDays, 0)
	if err != nil {
		t.Fatal(err)
	}
	return domain.Known(p)
}

func joinerBeds(beds ...SleepingBed) domain.Fact[SleepingObservation] {
	return domain.Known(SleepingObservation{Beds: beds})
}

func spareSleepingBed(id string, owners ...PawnID) SleepingBed {
	return SleepingBed{ID: id, Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false), Owners: owners}
}

func hostedRow(pawn string, admitted, guest, dead bool) CustodyFacts {
	return CustodyFacts{Pawn: domain.PawnID(pawn), Dead: domain.Known(dead), Admitted: domain.Known(admitted), Guest: domain.Known(guest)}
}

func joinerFacts(t *testing.T, hosted int, maximum int32) JoinerCapacityFacts {
	t.Helper()
	rows := make([]CustodyFacts, 0, hosted)
	for i := 0; i < hosted; i++ {
		rows = append(rows, hostedRow(string(rune('a'+i)), true, false, false))
	}
	return JoinerCapacityFacts{Custody: domain.Known(rows), Sleeping: joinerBeds(spareSleepingBed("bed")), FoodDays: domain.Known(10.0), Policy: joinerPolicy(t, maximum, 3)}
}

func TestIsJoinerOfferNamesOnlyThreatRewardJoinerRoots(t *testing.T) {
	t.Parallel()
	for def, want := range map[string]bool{
		"ThreatReward_Raid_Joiner": true, "ThreatReward_MechPods_Joiner": true, "ThreatReward_Raid_Trade": false,
		"WandererJoins": false, "OpportunitySite_DownedRefugee": false, "TradeRequest": false, "": false,
	} {
		if got := IsJoinerOffer(def); got != want {
			t.Fatalf("IsJoinerOffer(%q) = %v", def, got)
		}
	}
}

func TestJoinerCapacityCountsHostedPeopleAgainstThePolicy(t *testing.T) {
	t.Parallel()
	facts := joinerFacts(t, 3, 4)
	if room, known := JoinerCapacity(facts).Value(); !known || !room {
		t.Fatal("three hosted under a maximum of four should have room")
	}
	// A guest and a prisoner count as hosted; the dead do not.
	facts.Custody = domain.Known([]CustodyFacts{hostedRow("a", true, false, false), hostedRow("b", true, false, false), hostedRow("g", false, true, false), hostedRow("p", false, true, false), hostedRow("d", true, false, true)})
	if room, known := JoinerCapacity(facts).Value(); !known || room {
		t.Fatal("four hosted under a maximum of four should have no room")
	}
	// An unknown row leaves the count, and so the capacity, unknown.
	facts.Custody = domain.Known([]CustodyFacts{hostedRow("a", true, false, false), {Pawn: "u", Dead: domain.Known(false)}})
	if _, known := JoinerCapacity(facts).Value(); known {
		t.Fatal("an unknown row must not be counted either way")
	}
}

func TestJoinerCapacityNeedsFoodReserveAndASpareBed(t *testing.T) {
	t.Parallel()
	facts := joinerFacts(t, 2, 10)
	facts.FoodDays = domain.Known(2.5)
	if room, known := JoinerCapacity(facts).Value(); !known || room {
		t.Fatal("a runway under the policy reserve has no room")
	}
	facts.FoodDays = domain.Known(3.0)
	if room, known := JoinerCapacity(facts).Value(); !known || !room {
		t.Fatal("a runway at the policy reserve has room")
	}
	facts.Sleeping = joinerBeds(spareSleepingBed("owned", "a"), SleepingBed{ID: "medical", Humanlike: domain.Known(true), Medical: domain.Known(true), Prisoners: domain.Known(false)}, SleepingBed{ID: "prison", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(true)}, SleepingBed{ID: "animal", Humanlike: domain.Known(false), Medical: domain.Known(false), Prisoners: domain.Known(false)})
	if room, known := JoinerCapacity(facts).Value(); !known || room {
		t.Fatal("owned, medical, prisoner and animal beds are not spare")
	}
	facts.Sleeping = domain.Unknown[SleepingObservation]()
	if _, known := JoinerCapacity(facts).Value(); known {
		t.Fatal("an unknown sleeping census leaves capacity unknown")
	}
	facts = joinerFacts(t, 2, 10)
	facts.Policy = domain.Known(domain.PopulationPolicy{})
	if room, known := JoinerCapacity(facts).Value(); !known || room {
		t.Fatal("no declared policy is a known no")
	}
	facts.Policy = domain.Unknown[domain.PopulationPolicy]()
	if _, known := JoinerCapacity(facts).Value(); known {
		t.Fatal("an unread policy leaves capacity unknown")
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

func TestRoutineFactsJoinerCapacityFallsBackToStockRunway(t *testing.T) {
	t.Parallel()
	f := RoutineFacts{FoodDays: domain.Known(4.0), PopulationCapacity: joinerPolicy(t, 5, 1)}
	f.RaidPoints, f.DefenseTiers = domain.Known(120.0), domain.Known(1)
	if f.JoinerCapacity().RaidPoints != f.RaidPoints || f.JoinerCapacity().DefenseTiers != f.DefenseTiers {
		t.Fatal("joiner capacity lost raid/defense facts")
	}
	if days, known := f.JoinerCapacity().FoodDays.Value(); !known || days != 4 {
		t.Fatal(f.JoinerCapacity().FoodDays)
	}
	f.PopulationFoodDays = domain.Known(2.0)
	if days, known := f.JoinerCapacity().FoodDays.Value(); !known || days != 2 {
		t.Fatal(f.JoinerCapacity().FoodDays)
	}
}

func TestJoinerCapacityRaidThreshold(t *testing.T) {
	for _, tt := range []struct {
		name      string
		points    domain.Fact[float64]
		tiers     domain.Fact[int]
		threshold float64
		want      bool
	}{
		{"disabled", domain.Known(1000.0), domain.Known(0), 0, true},
		{"below", domain.Known(99.0), domain.Known(0), 300, true},
		{"at", domain.Known(100.0), domain.Known(0), 300, true},
		{"crosses", domain.Known(101.0), domain.Known(0), 300, false},
		{"already above", domain.Known(400.0), domain.Known(0), 300, false},
		{"firing line", domain.Known(101.0), domain.Known(1), 300, true},
		{"two tiers", domain.Known(101.0), domain.Known(2), 300, true},
		{"unknown points", domain.Unknown[float64](), domain.Known(0), 300, true},
		{"unknown tiers", domain.Known(101.0), domain.Unknown[int](), 300, true},
		{"both unknown", domain.Unknown[float64](), domain.Unknown[int](), 300, true},
	} {
		t.Run(tt.name, func(t *testing.T) {
			f := joinerFacts(t, 2, 10)
			p, err := domain.NewPopulationPolicy(10, 3, tt.threshold)
			if err != nil {
				t.Fatal(err)
			}
			f.Policy, f.RaidPoints, f.DefenseTiers = domain.Known(p), tt.points, tt.tiers
			if got, known := JoinerCapacity(f).Value(); !known || got != tt.want {
				t.Fatalf("capacity = %v, known = %v; want %v", got, known, tt.want)
			}
			f.FoodDays = domain.Known(0.0)
			if got, known := JoinerCapacity(f).Value(); !known || got {
				t.Fatal("raid facts bypassed food capacity")
			}
			f.FoodDays = domain.Unknown[float64]()
			if _, known := JoinerCapacity(f).Value(); known {
				t.Fatal("raid facts changed unknown food capacity")
			}
		})
	}
}
