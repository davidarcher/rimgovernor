package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"testing"
)

func coreSiteSnapshot(root string) (JoinerOffer, WorldSite, RoundsFacts) {
	offer := JoinerOffer{Quest: "quest", ScriptDef: root, State: "Ongoing", Profile: domain.Known(QuestProfile{Family: QuestFamilySite})}
	site := WorldSite{ID: "site", QuestIDs: []domain.QuestID{offer.Quest}, Map: domain.Known(domain.MapID(2)), Threat: domain.Known(false), Extraction: domain.Known(SiteExtraction{Crew: []domain.PawnID{"worker"}})}
	facts := RoundsFacts{Custody: domain.Known([]CustodyFacts{}), QuestDeparturePawns: domain.Known([]QuestDeparturePawn{{ID: "worker", HealthyAdult: domain.Known(true)}})}
	return offer, site, facts
}

func TestFailedHackSiteReturnsCrewWithoutCompletingTargets(t *testing.T) {
	offer, site, facts := coreSiteSnapshot("AncientComplex_Mission")
	offer.Profile = domain.Known(QuestFamilyForRoot(offer.ScriptDef))
	offer.Objectives = []QuestObjective{{HackTargets: []QuestHackTarget{{ID: "terminal", Satisfied: domain.Known(false)}}}}
	if got := PlanCoreSiteWork(offer, site, facts, 2); got.Kind == "return" {
		t.Fatalf("unfinished ongoing complex abandoned: %+v", got)
	}
	offer.State = "EndedFailed"
	if QuestHackComplete(offer) {
		t.Fatal("failed objective incorrectly complete")
	}
	if got := PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "return" || got.Reason != "quest_failed" {
		t.Fatalf("failed complex stranded crew: %+v", got)
	}
	returnSite := returnFixture()
	returnSite.ID, returnSite.Map, returnSite.QuestIDs, returnSite.Threat = site.ID, site.Map, site.QuestIDs, site.Threat
	if got := PlanSiteReturn(returnSite, false); got.Departure == nil || got.Departure.DestinationTile() != 12 {
		t.Fatalf("legal failed complex return missing: %+v", got)
	}
	site.Threat = domain.Known(true)
	if got := PlanCoreSiteWork(offer, site, facts, 2); got.Kind == "return" {
		t.Fatalf("failed complex bypassed active threat: %+v", got)
	}
	site.Threat = domain.Known(false)
	site.QuestIDs = nil
	if got := PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "" {
		t.Fatalf("failed complex claimed unrelated map: %+v", got)
	}
}

func TestCoreSiteWorkRecordedFamiliesSecurityAndReturn(t *testing.T) {
	for _, root := range []string{"OpportunitySite_BanditCamp", "OpportunitySite_DownedRefugee", "OpportunitySite_PrisonerWillingToJoin", "OpportunitySite_ItemStash", "LongRangeMineralScannerLump"} {
		t.Run(root, func(t *testing.T) {
			offer, site, facts := coreSiteSnapshot(root)
			if root == "OpportunitySite_DownedRefugee" || root == "OpportunitySite_PrisonerWillingToJoin" {
				offer.Objectives = []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_RESCUE_PAWNS, PawnIDs: []domain.PawnID{"joiner"}}}
				facts.Custody = domain.Known([]CustodyFacts{{Pawn: "joiner", Dead: domain.Known(false), Admitted: domain.Known(true)}})
			}
			if got := PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "return" {
				t.Fatalf("completed site: %+v", got)
			}
			site.Threat = domain.Known(true)
			if got := PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "wait" || got.Reason != "site_security" {
				t.Fatalf("unsafe site: %+v", got)
			}
			site.Threat = domain.Unknown[bool]()
			if got := PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "wait" {
				t.Fatalf("unknown security: %+v", got)
			}
			site.Threat = domain.Known(false)
			if got := PlanCoreSiteWork(offer, site, facts, 1); got.Reason != "site_map_unavailable" {
				t.Fatalf("wrong map: %+v", got)
			}
			site.QuestIDs = nil
			if got := PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "" {
				t.Fatalf("unlinked site: %+v", got)
			}
		})
	}
}

func TestCoreSiteScannerDesignatesExactLumpAndWaitsForNativeCompletion(t *testing.T) {
	offer, site, facts := coreSiteSnapshot("LongRangeMineralScannerLump")
	site.MiningTargets = []SiteMiningTarget{{ID: "lump-b", Def: "Gold", Map: 2, Cell: domain.Cell{X: 11, Z: 12}, Designated: domain.Known(false)}, {ID: "lump-a", Def: "Gold", Map: 2, Cell: domain.Cell{X: 10, Z: 12}, Designated: domain.Known(false)}}
	got := PlanCoreSiteWork(offer, site, facts, 2)
	if got.Kind != "mine" || got.Mine.Thing() != "lump-a" || got.Mine.Definition() != "Gold" || got.Mine.Cell() != (domain.Cell{X: 10, Z: 12}) {
		t.Fatalf("exact mine: %+v", got)
	}
	for i := range site.MiningTargets {
		site.MiningTargets[i].Designated = domain.Known(true)
	}
	if got = PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "wait" || got.Reason != "mining_work" {
		t.Fatalf("designation treated as completion: %+v", got)
	}
	site.MiningTargets = nil
	if got = PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "return" {
		t.Fatalf("depleted lump: %+v", got)
	}
}

func TestCoreSiteJoinerHelpAndNativeMembershipLifecycle(t *testing.T) {
	for _, root := range []string{"OpportunitySite_DownedRefugee", "OpportunitySite_PrisonerWillingToJoin"} {
		t.Run(root, func(t *testing.T) {
			offer, site, facts := coreSiteSnapshot(root)
			offer.Objectives = []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_RESCUE_PAWNS, PawnIDs: []domain.PawnID{"joiner"}}}
			row := CustodyFacts{Pawn: "joiner", Dead: domain.Known(false), Admitted: domain.Known(false), WillJoinIfRescued: domain.Known(true)}
			facts.Custody = domain.Known([]CustodyFacts{row, {Pawn: "worker", Dead: domain.Known(false), Downed: domain.Known(false), Admitted: domain.Known(true)}})
			got := PlanCoreSiteWork(offer, site, facts, 2)
			if got.Kind != "help" || got.Help.Pawn() != "worker" || got.Help.Thing() != "joiner" || got.Help.Method() != domain.RecoveryServiceOfferHelp {
				t.Fatalf("help: %+v", got)
			}
			row.WillJoinIfRescued = domain.Known(false)
			facts.Custody = domain.Known([]CustodyFacts{row, {Pawn: "worker", Dead: domain.Known(false), Downed: domain.Known(false), Admitted: domain.Known(true)}})
			if got = PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "waitrescue" {
				t.Fatalf("receipt replaced membership: %+v", got)
			}
			facts.Prisoners = domain.Known([]PrisonerFacts{{Pawn: "joiner", Prisoner: domain.Known(true), Recruitable: domain.Known(true), CurrentInteraction: domain.Known(domain.PrisonerInteractionMaintain)}})
			if got = PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "recruit" || got.Recruit.Pawn() != "joiner" {
				t.Fatalf("recruit: %+v", got)
			}
			row.Admitted = domain.Known(true)
			facts.Custody = domain.Known([]CustodyFacts{row, {Pawn: "worker", Dead: domain.Known(false), Downed: domain.Known(false), Admitted: domain.Known(true)}})
			if got = PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "return" {
				t.Fatalf("membership: %+v", got)
			}
		})
	}
}

func TestCoreSiteTerminalQuestRetainsWorkAndReturnsFailedCrew(t *testing.T) {
	offer, site, facts := coreSiteSnapshot("LongRangeMineralScannerLump")
	site.MiningTargets = []SiteMiningTarget{{ID: "lump", Def: "Gold", Map: 2, Cell: domain.Cell{X: 10, Z: 12}, Designated: domain.Known(false)}}
	offer.State = "EndedSuccess"
	if got := PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "mine" {
		t.Fatalf("terminal success dropped remaining native work: %+v", got)
	}
	offer.State = "EndedFailed"
	if got := PlanCoreSiteWork(offer, site, facts, 2); got.Kind != "return" {
		t.Fatalf("failed crew stranded: %+v", got)
	}
}

func TestSelectExpeditionCoreFollowRowsReachSiteDriver(t *testing.T) {
	for _, root := range []string{"OpportunitySite_DownedRefugee", "OpportunitySite_ItemStash", "LongRangeMineralScannerLump", "OpportunitySite_PrisonerWillingToJoin"} {
		t.Run(root, func(t *testing.T) {
			offer, route, f, food := expeditionFixture()
			offer.ScriptDef = root
			offer.State = "Ongoing"
			offer.Profile = domain.Known(QuestFamilyForRoot(root))
			profile, _ := offer.Profile.Value()
			if profile.NeverAct || profile.Disposition != QuestFollow || profile.Cost != QuestCostPawns {
				t.Fatalf("core follow disabled: %+v", profile)
			}
			f.QuestOffers = domain.Known([]JoinerOffer{offer})
			f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{})
			f.AnimalUpkeep.Food = domain.Known(food)
			site := WorldSite{ID: route.ID, QuestIDs: []domain.QuestID{offer.Quest}, State: o.WorldSiteState_WORLD_SITE_STATE_SPAWNED, Tile: domain.Known(route.Tile), Layer: route.Layer, TravelTicks: route.TravelTicks, Reachable: route.Reachable, RoutePawnIDs: route.RoutePawnIDs, Security: domain.Known(SiteSecurity{Known: domain.Known(true), InitialPoints: domain.Known(20.0), PendingRaidPoints: domain.Known(0.0)})}
			f.QuestSites = domain.Known([]WorldSite{site})
			if !ExpeditionDeficit(f) {
				t.Fatal("follow row did not open expedition concern")
			}
			if got := SelectExpedition(f, DefaultRoundsPolicy()); got.Departure == nil || got.Reason != "" {
				t.Fatalf("linked core departure: %+v", got)
			}
			site.State = o.WorldSiteState_WORLD_SITE_STATE_MAP_LOADED
			f.QuestSites = domain.Known([]WorldSite{site})
			if got := SelectExpedition(f, DefaultRoundsPolicy()); !got.Waiting || got.Departure != nil || got.Quest != offer.Quest {
				t.Fatalf("arrival lost current site driver: %+v", got)
			}
		})
	}
}
