package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// These snapshots use the native quest state and linked site security contract:
// initial and future raid points are a closed bound, not a label-based estimate.
func TestComplexExpeditionNativeSecuritySnapshots(t *testing.T) {
	for _, root := range []string{"AncientComplex_Standard", "AncientComplex_Mission", "OpportunitySite_AncientComplex", "OpportunitySite_AncientComplex_Mechanitor", "OpportunitySite_WorkSite", "OpportunitySite_AncientComplex_Giver"} {
		t.Run(root, func(t *testing.T) {
			profile := QuestFamilyForRoot(root)
			follow := root != "AncientComplex_Standard" && root != "AncientComplex_Mission"
			if profile.NeverAct || (follow && profile.Disposition != QuestFollow) || (!follow && profile.Disposition != QuestDecide) {
				t.Fatalf("complex classification: %+v", profile)
			}
			for _, scenario := range []struct {
				name     string
				security SiteSecurity
				reason   QuestSkipReason
			}{
				{"bounded", SiteSecurity{Known: domain.Known(true), InitialPoints: domain.Known(20.0), PendingRaidPoints: domain.Known(0.0)}, ""},
				{"over_strength", SiteSecurity{Known: domain.Known(true), InitialPoints: domain.Known(100.0), PendingRaidPoints: domain.Known(0.0)}, "home_defense"},
				{"pending_raids", SiteSecurity{Known: domain.Known(true), InitialPoints: domain.Known(20.0), PendingRaidPoints: domain.Known(100.0)}, "home_defense"},
				{"unknown_trap_bound", SiteSecurity{Known: domain.Known(false), InitialPoints: domain.Known(20.0), PendingRaidPoints: domain.Known(0.0), TrapCount: domain.Known(int32(3))}, "threat_unknown"},
				{"unknown_bound", SiteSecurity{Known: domain.Unknown[bool](), InitialPoints: domain.Known(0.0), PendingRaidPoints: domain.Known(0.0)}, "threat_unknown"},
				{"unknown_future_raids", SiteSecurity{Known: domain.Known(true), InitialPoints: domain.Known(20.0)}, "threat_unknown"},
			} {
				t.Run(scenario.name, func(t *testing.T) {
					offer, route, f, food := expeditionFixture()
					offer.ScriptDef, offer.State = root, "Ongoing"
					offer.Profile = domain.Known(profile)
					f.QuestOffers = domain.Known([]JoinerOffer{offer})
					f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{})
					f.AnimalUpkeep.Food = domain.Known(food)
					f.QuestSites = domain.Known([]WorldSite{{ID: route.ID, QuestIDs: []domain.QuestID{offer.Quest}, State: o.WorldSiteState_WORLD_SITE_STATE_SPAWNED, Tile: domain.Known(route.Tile), Layer: route.Layer, TravelTicks: route.TravelTicks, Reachable: route.Reachable, RoutePawnIDs: route.RoutePawnIDs, Security: domain.Known(scenario.security)}})
					got := SelectExpedition(f, DefaultRoundsPolicy())
					if got.Reason != scenario.reason || (got.Departure != nil) != (scenario.reason == "") || got.Quest != offer.Quest {
						t.Fatalf("departure decision: %+v", got)
					}
				})
			}
		})
	}
}

func TestWorshippedTerminalExpeditionRefusesFriendlyTribeBeforeDeparture(t *testing.T) {
	for _, hostile := range []bool{false, true} {
		t.Run(map[bool]string{false: "friendly", true: "hostile"}[hostile], func(t *testing.T) {
			offer, route, f, food := expeditionFixture()
			offer.ScriptDef, offer.State = "Hack_WorshippedTerminal", "Ongoing"
			offer.Profile = domain.Known(QuestFamilyForRoot(offer.ScriptDef))
			offer.FactionHostile = domain.Known(true) // unrelated quest faction is not the tribe
			offer.Objectives[0].HackRisk = domain.Known(QuestHackRisk{FactionID: "exact-tribe", Hostile: domain.Known(hostile)})
			f.QuestOffers = domain.Known([]JoinerOffer{offer})
			f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{})
			f.AnimalUpkeep.Food = domain.Known(food)
			f.QuestSites = domain.Known([]WorldSite{{ID: route.ID, QuestIDs: []domain.QuestID{offer.Quest}, State: o.WorldSiteState_WORLD_SITE_STATE_SPAWNED, Tile: domain.Known(route.Tile), Layer: route.Layer, TravelTicks: route.TravelTicks, Reachable: route.Reachable, RoutePawnIDs: route.RoutePawnIDs, Security: domain.Known(SiteSecurity{Known: domain.Known(true), InitialPoints: domain.Known(20.0), PendingRaidPoints: domain.Known(0.0)})}})
			got := SelectExpedition(f, DefaultRoundsPolicy())
			if hostile {
				if got.Departure == nil || got.Reason != "" {
					t.Fatalf("hostile tribe affordable route refused: %+v", got)
				}
			} else if got.Departure != nil || got.Reason != "worshipped_terminal_trap" {
				t.Fatalf("friendly tribe expedition dispatched: %+v", got)
			}
		})
	}
}
