package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func monumentOfferSnapshot() (JoinerOffer, RoundsFacts) {
	q, f := questForecastSnapshot()
	q.ScriptDef = "BuildMonument_Basic"
	q.Profile = domain.Known(QuestFamilyForRoot(q.ScriptDef))
	marker := QuestMonument{Map: 0, Offered: domain.Known(true), ClearSite: domain.Known(true), InstallCells: []domain.Cell{{X: 10, Z: 10}}, AvailableResources: []Amount{{Resource: "BlocksGranite", Count: 20}}, Pieces: []QuestMonumentPiece{{Def: "Wall", BuildOptions: []QuestMonumentBuildOption{{Stuff: "BlocksGranite", Work: 8000, Costs: []Amount{{Resource: "BlocksGranite", Count: 5}}}}}}}
	q.Objectives[0].Monument = domain.Known(marker)
	q.CanAccept = true
	q.OnMap = true
	q.FactionID = "faction"
	q.FactionHostile = domain.Known(false)
	f.ConstructionDeficit = domain.Known(map[Resource]int64{})
	f.QuestColonyCalm = domain.Known(true)
	f.QuestColonistsAtHome = domain.Known(5)
	return q, f
}

func TestMonumentAdmissionSnapshots(t *testing.T) {
	for name, test := range map[string]struct {
		edit func(*JoinerOffer, *RoundsFacts, *QuestMonument)
		want QuestSkipReason
	}{
		"affordable":    {},
		"no clear site": {edit: func(_ *JoinerOffer, _ *RoundsFacts, m *QuestMonument) { m.ClearSite = domain.Known(false) }, want: "monument_site"},
		"stock short":   {edit: func(_ *JoinerOffer, _ *RoundsFacts, m *QuestMonument) { m.AvailableResources = nil }, want: "monument_materials"},
		"construction stock reserved": {edit: func(_ *JoinerOffer, f *RoundsFacts, _ *QuestMonument) {
			f.ConstructionDeficit = domain.Known(map[Resource]int64{"BlocksGranite": 20})
		}, want: "monument_materials"},
		"pods fund stock": {edit: func(_ *JoinerOffer, _ *RoundsFacts, m *QuestMonument) {
			m.AvailableResources = nil
			m.SuppliedResources = []Amount{{Resource: "BlocksGranite", Count: 5}}
		}},
		"protect defense short": {edit: func(q *JoinerOffer, f *RoundsFacts, _ *QuestMonument) {
			q.Profile = domain.Known(QuestFamilyForRoot("BuildMonument_TimeProtect"))
			q.ThreatPoints = domain.Known(200.0)
			f.DefenseCapacity = domain.Known(100.0)
		}, want: "defense_capacity"},
		"protect defense sufficient": {edit: func(q *JoinerOffer, f *RoundsFacts, _ *QuestMonument) {
			q.Profile = domain.Known(QuestFamilyForRoot("BuildMonument_TimeProtect"))
			q.ThreatPoints = domain.Known(200.0)
			f.DefenseCapacity = domain.Known(250.0)
		}},
	} {
		t.Run(name, func(t *testing.T) {
			q, f := monumentOfferSnapshot()
			m, _ := q.Objectives[0].Monument.Value()
			if test.edit != nil {
				test.edit(&q, &f, &m)
			}
			q.Objectives[0].Monument = domain.Known(m)
			if got := MonumentAdmission(q, f); got != test.want {
				t.Fatalf("got %q want %q", got, test.want)
			}
		})
	}
}

func TestQuestSelectorAdmitsMonumentOnlyWithEnoughTime(t *testing.T) {
	q, f := monumentOfferSnapshot()
	f.QuestOffers = domain.Known([]JoinerOffer{q})
	if choice := SelectQuestMethod(f); choice.Quest != q.Quest {
		t.Fatal(choice)
	}
	q.Objectives[0].DurationTicks = domain.Known(int64(30000))
	f.QuestOffers = domain.Known([]JoinerOffer{q})
	if accept, reason := questDecision(q, f); accept || reason != "deadline_capacity" {
		t.Fatal(accept, reason)
	}
}
