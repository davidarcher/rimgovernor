package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"testing"
)

var recordedGroundSiteRoots = []string{
	"Gravcore_InsectLair", "Gravcore_AncientReactor", "Gravcore_AncientStockpile", "Gravcore_CrashedMechanoidPlatform", "Gravcore_FrozenTerraformer", "Gravcore_MechanoidRelay",
	"GravshipWreckage", "OpportunitySite_AncientMercenaries", "OpportunitySite_BanditCamp_Giver", "OpportunitySite_ItemStash_Giver", "OpportunitySite_AlphaThrumbo_Giver", "OpportunitySite_DistressCall",
	"Opportunity_AncientStructureLaunchSite", "Opportunity_AncientStructureGarrison", "Opportunity_AncientStructureChemfuelRefinery", "Opportunity_AncientStructureWarehouse", "Opportunity_AncientInfestedSettlement",
}

func TestOdysseyGroundSiteRecordedStrengthGoNoGo(t *testing.T) {
	for _, root := range recordedGroundSiteRoots {
		t.Run(root, func(t *testing.T) {
			offer, route, f, food := expeditionFixture()
			offer.ScriptDef = root
			offer.State = "Ongoing"
			offer.Profile = domain.Known(QuestFamilyForRoot(root))
			profile, _ := offer.Profile.Value()
			if profile.Family != QuestFamilyOdysseyGround || profile.Disposition != QuestFollow || profile.NeverAct || profile.Demands&QuestDemandSecurity == 0 {
				t.Fatalf("family not driven: %+v", profile)
			}
			f.QuestOffers = domain.Known([]JoinerOffer{offer})
			f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{})
			f.AnimalUpkeep.Food = domain.Known(food)
			security := SiteSecurity{Known: domain.Known(true), InitialPoints: domain.Known(20.0), PendingRaidPoints: domain.Known(0.0)}
			site := WorldSite{ID: route.ID, QuestIDs: []domain.QuestID{offer.Quest}, State: o.WorldSiteState_WORLD_SITE_STATE_SPAWNED, Tile: domain.Known(route.Tile), Layer: route.Layer, TravelTicks: route.TravelTicks, Reachable: route.Reachable, RoutePawnIDs: route.RoutePawnIDs, Security: domain.Known(security)}
			f.QuestSites = domain.Known([]WorldSite{site})
			if got := SelectExpedition(f, DefaultRoundsPolicy()); got.Departure == nil || got.Reason != "" {
				t.Fatalf("adequate observed strength: %+v", got)
			}
			security.InitialPoints = domain.Known(100.0)
			site.Security = domain.Known(security)
			f.QuestSites = domain.Known([]WorldSite{site})
			if got := SelectExpedition(f, DefaultRoundsPolicy()); got.Departure != nil || got.Reason != "home_defense" {
				t.Fatalf("outmatched departure: %+v", got)
			}
			security.InitialPoints = domain.Known(20.0)
			security.PendingRaidPoints = domain.Known(100.0)
			site.Security = domain.Known(security)
			f.QuestSites = domain.Known([]WorldSite{site})
			if got := SelectExpedition(f, DefaultRoundsPolicy()); got.Departure != nil {
				t.Fatalf("ambush ignored: %+v", got)
			}
			security.Known = domain.Known(false)
			site.Security = domain.Known(security)
			f.QuestSites = domain.Known([]WorldSite{site})
			if got := SelectExpedition(f, DefaultRoundsPolicy()); got.Departure != nil || got.Reason != "threat_unknown" {
				t.Fatalf("unresolved threat: %+v", got)
			}
			site.Map = domain.Known(domain.MapID(2))
			site.Threat = domain.Known(false)
			if got := PlanCoreSiteWork(offer, site, f, 2); got.Kind != "return" {
				t.Fatalf("cleared native site stranded: %+v", got)
			}
		})
	}
}

func TestOdysseyGroundSiteSpaceVariantsRemainShipOnly(t *testing.T) {
	for _, root := range []string{"Gravcore_OrbitalMechanoidPlatform", "Gravcore_OrbitalAncientPlatform", "Gravcore_Mechhive", "OpportunitySite_OrbitalItemStash"} {
		profile := QuestFamilyForRoot(root)
		if OdysseyGroundSiteRoot(root) || profile.Disposition != QuestRefuse || profile.SkipReason != "ship_only" {
			t.Fatalf("space variant %s: %+v", root, profile)
		}
	}
	offer, site, f, food := expeditionFixture()
	offer.ScriptDef = "Gravcore_InsectLair"
	offer.Profile = domain.Known(QuestFamilyForRoot(offer.ScriptDef))
	site.Layer = domain.Known(int32(1))
	if got := PlanExpedition(offer, site, f, domain.Known(food), DefaultRoundsPolicy()); got.Departure != nil || got.Reason != "ship_only" {
		t.Fatalf("ground root on space layer: %+v", got)
	}
}
