package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestExpeditionDeficitRetainsTerminalSiteCrewThenClears(t *testing.T) {
	for _, state := range []string{"EndedSuccess", "EndedFailed"} {
		t.Run(state, func(t *testing.T) {
			offer, _, f, _ := expeditionFixture()
			offer.State = state
			f.QuestOffers = domain.Known([]JoinerOffer{offer})
			site := WorldSite{ID: "site", Map: domain.Known(domain.MapID(77)), QuestIDs: []domain.QuestID{offer.Quest}, Extraction: domain.Known(SiteExtraction{Crew: []domain.PawnID{"a"}})}
			f.QuestSites = domain.Known([]WorldSite{site})
			if !ExpeditionDeficit(f) {
				t.Fatal("terminal quest stranded its site crew")
			}
			site.Extraction = domain.Known(SiteExtraction{})
			f.QuestSites = domain.Known([]WorldSite{site})
			if ExpeditionDeficit(f) {
				t.Fatal("returned crew left an endless deficit")
			}
		})
	}
}

func TestSurveyFailedQuestReturnsLivingScannerCrew(t *testing.T) {
	offer, _, f, food, site := surveyFixture()
	offer.State = "EndedFailed"
	plan := SurveyWork(offer, site, f, domain.Known(food), DefaultRoundsPolicy())
	if !plan.Return || !plan.Failed || plan.Waiting || plan.Departure != nil || plan.Reason != "quest_failed" {
		t.Fatal(plan)
	}
}
