package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"testing"
)

func TestPeaceTalksUsesBestSocialAndEligibleIdeologyLeader(t *testing.T) {
	offer, f := departureFixture(QuestFamilyPawnLend, 1)
	pawns := domain.Known([]PeaceTalksPawn{{ID: "a", Social: domain.Known(int32(5)), Leader: domain.Known(true)}, {ID: "b", Social: domain.Known(int32(18)), Leader: domain.Known(false)}, {ID: "c", Social: domain.Known(int32(12)), Leader: domain.Known(false)}})
	if id, reason := PeaceTalksNegotiator(offer, f, pawns, domain.Known(false)); id != "b" || reason != "" {
		t.Fatal(id, reason)
	}
	if id, reason := PeaceTalksNegotiator(offer, f, pawns, domain.Known(true)); id != "a" || reason != "" {
		t.Fatal(id, reason)
	}
	// Leadership never overrides a last essential work owner or reserved pawn.
	f.QuestSparePawns = domain.Known([]PawnID{"b", "c"})
	if id, reason := PeaceTalksNegotiator(offer, f, pawns, domain.Known(true)); id != "b" || reason != "" {
		t.Fatal(id, reason)
	}
	if id, reason := PeaceTalksNegotiator(offer, f, domain.Known([]PeaceTalksPawn{{ID: "a"}, {ID: "b", Social: domain.Known(int32(18))}}), domain.Known(false)); id != "b" || reason != "" {
		t.Fatal(id, reason)
	}
}

func TestPeaceTalksSharedSelectorUsesDiplomatAndDownside(t *testing.T) {
	offer, site, f, food := expeditionFixture()
	offer.ScriptDef = "OpportunitySite_PeaceTalks"
	offer.Profile = domain.Known(QuestFamilyForRoot(offer.ScriptDef))
	rows, _ := f.QuestDeparturePawns.Value()
	for i := range rows {
		rows[i].SocialLevel = domain.Known(int32(i))
		rows[i].FactionLeader = domain.Known(false)
		rows[i].CanFight = domain.Known(false)
	}
	f.QuestDeparturePawns = domain.Known(rows)
	world := WorldSite{ID: site.ID, State: o.WorldSiteState_WORLD_SITE_STATE_SPAWNED, Tile: domain.Known(site.Tile), Layer: site.Layer, QuestIDs: []domain.QuestID{offer.Quest}, ThreatPoints: domain.Known(0.0), Reachable: site.Reachable, TravelTicks: site.TravelTicks, RoutePawnIDs: site.RoutePawnIDs, PeaceTalks: domain.Known(PeaceTalksRisk{WorstGoodwillLoss: domain.Known(int32(76)), BestGoodwillGain: domain.Known(int32(100)), IdeologyActive: domain.Known(false)})}
	f.AnimalUpkeep.Food = domain.Known(food)
	f.QuestSites = domain.Known([]WorldSite{world})
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{})
	if plan := SelectExpedition(f, DefaultRoundsPolicy()); plan.Departure == nil || plan.Departure.Crew()[0] != "c" || plan.Reason != "" {
		t.Fatal(plan)
	}
	risk, _ := world.PeaceTalks.Value()
	risk.WorstGoodwillLoss = domain.Known(int32(126))
	risk.BestGoodwillGain = domain.Known(int32(50))
	world.PeaceTalks = domain.Known(risk)
	f.QuestSites = domain.Known([]WorldSite{world})
	if reason := ExpeditionAdmission(offer, f); reason != "goodwill_downside" {
		t.Fatal(reason)
	}
}

func TestPeaceTalksRefusesGoodwillDownsideBeyondReward(t *testing.T) {
	for _, tt := range []struct {
		loss, gain int32
		reason     QuestSkipReason
	}{{176, 0, "goodwill_downside"}, {126, 50, "goodwill_downside"}, {76, 100, ""}, {50, 110, ""}} {
		if reason := PeaceTalksDownside(PeaceTalksRisk{WorstGoodwillLoss: domain.Known(tt.loss), BestGoodwillGain: domain.Known(tt.gain)}); reason != tt.reason {
			t.Fatal(tt, reason)
		}
	}
	if reason := PeaceTalksDownside(PeaceTalksRisk{}); reason != "diplomacy_unknown" {
		t.Fatal(reason)
	}
}

func TestPeaceTalksKeepsHomeDefenseAndFoodStaffing(t *testing.T) {
	offer, f := departureFixture(QuestFamilyPawnLend, 1)
	f.DefenseCapacity = domain.Known(60.0)
	pawns := domain.Known([]PeaceTalksPawn{{ID: "a", Social: domain.Known(int32(20))}})
	if id, reason := PeaceTalksNegotiator(offer, f, pawns, domain.Known(false)); id != "" || reason != "negotiator_capacity" {
		t.Fatal(id, reason)
	}
	f.DefenseCapacity = domain.Known(100.0)
	f.QuestColonistsAtHome = domain.Known(3)
	f.QuestHomeFloor = domain.Known(3)
	if id, reason := PeaceTalksNegotiator(offer, f, pawns, domain.Known(false)); id != "" || reason != "negotiator_capacity" {
		t.Fatal(id, reason)
	}
}

func TestPeaceTalksExpeditionPacksSelectedDiplomatAndRefusesRisk(t *testing.T) {
	offer, site, f, food := expeditionFixture()
	offer.ScriptDef = "OpportunitySite_PeaceTalks"
	site.ThreatPoints = domain.Known(0.0)
	rows, _ := f.QuestDeparturePawns.Value()
	for i := range rows {
		rows[i].SocialLevel = domain.Known(int32(i + 1))
		rows[i].FactionLeader = domain.Known(false)
		rows[i].CanFight = domain.Known(false)
	}
	f.QuestDeparturePawns = domain.Known(rows)
	risk := PeaceTalksRisk{WorstGoodwillLoss: domain.Known(int32(76)), BestGoodwillGain: domain.Known(int32(100)), IdeologyActive: domain.Known(false)}
	f.QuestSites = domain.Known([]WorldSite{{ID: site.ID, PeaceTalks: domain.Known(risk)}})
	plan := PlanPeaceTalks(offer, site, f, domain.Known(food), DefaultRoundsPolicy())
	if plan.Reason != "" || plan.Departure == nil || len(plan.Departure.Crew()) != 1 || plan.Departure.Crew()[0] != "c" || len(plan.Departure.Cargo()) == 0 {
		t.Fatal(plan)
	}
	risk.WorstGoodwillLoss = domain.Known(int32(126))
	risk.BestGoodwillGain = domain.Known(int32(50))
	f.QuestSites = domain.Known([]WorldSite{{ID: site.ID, PeaceTalks: domain.Known(risk)}})
	if plan := PlanPeaceTalks(offer, site, f, domain.Known(food), DefaultRoundsPolicy()); plan.Reason != "goodwill_downside" || plan.Departure != nil {
		t.Fatal(plan)
	}
}
