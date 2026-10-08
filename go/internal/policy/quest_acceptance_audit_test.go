package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func TestSanguophageMeetingAdmissionAndProtectedGuestPlannerVetoes(t *testing.T) {
	offer := empireOffer("meeting", func(q *JoinerOffer) {
		q.ScriptDef = "SanguophageMeetingHost"
		q.Profile = domain.Known(QuestFamilyForRoot(q.ScriptDef))
	})
	f := questTestFacts(domain.Known([]JoinerOffer{offer}))
	if accepted, reason := questDecision(offer, f); !accepted || reason != "" {
		t.Fatalf("calm meeting refused: %v %s", accepted, reason)
	}
	f.QuestColonyCalm = domain.Known(false)
	if accepted, reason := questDecision(offer, f); accepted || reason != "colony_busy" {
		t.Fatalf("busy colony admitted meeting: %v %s", accepted, reason)
	}
	offer.State = "Ongoing"
	offer.Objectives = hospitalityOffer("meeting", QuestFamilyHospitalityJoiners).Objectives
	offer.Objectives[0].PawnIDs = []domain.PawnID{"guest"}
	protected := ProtectedQuestGuestIDs(domain.Known([]JoinerOffer{offer}))
	row := CustodyFacts{Pawn: "guest", Dead: domain.Known(false), Downed: domain.Known(true), Guest: domain.Known(false), Admitted: domain.Known(false), Prisoner: domain.Known(false), Hostile: domain.Known(true), Recruitable: domain.Known(true), WearingApparel: domain.Known(false)}
	if got := SelectCustodyMethod(domain.Known([]CustodyFacts{row})); got.Decision != CustodyCapture {
		t.Fatalf("capture fixture ineligible: %+v", got)
	}
	row.QuestProtected = protected[row.Pawn]
	if got := SelectCustodyMethod(domain.Known([]CustodyFacts{row})); got.Pawn != "" {
		t.Fatalf("meeting visitor captured: %+v", got)
	}
	patient := surgeryPawn("guest", 0, restoreOp("InstallPegLeg", "Leg", 30, 0.95, 1, true))
	if got := SelectSurgery(domain.Known([]CarePawn{patient}), nil, SurgeryContext{}); len(got.Queue) != 1 {
		t.Fatalf("surgery fixture ineligible: %+v", got)
	}
	patient.QuestProtected = protected[domain.PawnID(patient.ID)]
	if got := SelectSurgery(domain.Known([]CarePawn{patient}), nil, SurgeryContext{}); len(got.Queue) != 0 || len(got.Wants) != 0 {
		t.Fatalf("meeting visitor surgery planned: %+v", got)
	}
}

func TestCoreSiteOutboundStrengthAndUnknownBoundRefusals(t *testing.T) {
	for _, root := range []string{"OpportunitySite_DownedRefugee", "OpportunitySite_ItemStash", "LongRangeMineralScannerLump", "OpportunitySite_PrisonerWillingToJoin"} {
		for _, unknown := range []bool{false, true} {
			t.Run(root+map[bool]string{false: "/weak", true: "/unknown"}[unknown], func(t *testing.T) {
				offer, route, f, food := expeditionFixture()
				offer.ScriptDef, offer.State = root, "Ongoing"
				offer.Profile = domain.Known(QuestFamilyForRoot(root))
				security := SiteSecurity{Known: domain.Known(true), InitialPoints: domain.Known(100.0), PendingRaidPoints: domain.Known(0.0)}
				want := QuestSkipReason("home_defense")
				if unknown {
					security.Known = domain.Unknown[bool]()
					want = "threat_unknown"
				}
				f.QuestOffers = domain.Known([]JoinerOffer{offer})
				f.QuestExpeditionTrips = domain.Known([]ExpeditionTrip{})
				f.AnimalUpkeep.Food = domain.Known(food)
				f.QuestSites = domain.Known([]WorldSite{{ID: route.ID, QuestIDs: []domain.QuestID{offer.Quest}, State: o.WorldSiteState_WORLD_SITE_STATE_SPAWNED, Tile: domain.Known(route.Tile), Layer: route.Layer, TravelTicks: route.TravelTicks, Reachable: route.Reachable, RoutePawnIDs: route.RoutePawnIDs, Security: domain.Known(security)}})
				if got := SelectExpedition(f, DefaultRoundsPolicy()); got.Departure != nil || got.Reason != want || got.Quest != offer.Quest {
					t.Fatalf("unsafe core departure: %+v", got)
				}
			})
		}
	}
}

func TestRefugeeBetrayalNeverAcceptsOrDrivesHospitality(t *testing.T) {
	offer := empireOffer("betrayal", func(q *JoinerOffer) {
		q.ScriptDef = "RefugeeBetrayal"
		q.Profile = domain.Known(QuestFamilyForRoot(q.ScriptDef))
	})
	f := questTestFacts(domain.Known([]JoinerOffer{offer}))
	if got := SelectQuestMethod(f); got.Quest != "" {
		t.Fatalf("betrayal accepted: %+v", got)
	}
	offer.State = "Ongoing"
	offer.Objectives = hospitalityOffer("betrayal", QuestFamilyHospitalityRefugee).Objectives
	f.QuestOffers = domain.Known([]JoinerOffer{offer})
	f.Sleeping = domain.Known(guestCensus(nil))
	if got, err := SelectHospitalityWork(f, 1); err != nil || got.Assign != nil || got.Shuttle != nil || got.Quest != "" {
		t.Fatalf("betrayal drove guest work: %+v %v", got, err)
	}
	if HospitalityDeficit(f) {
		t.Fatal("betrayal opened hospitality work")
	}
}
