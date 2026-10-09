package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"net/http/httptest"
	"os"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// TestColonyStatusReportsMoodLedger ranks the recorded pressured pawn's
// thoughts (policy/testdata/mood-provision-pressured.json) plus one unowned
// thought; an unread expectation level is an empty list, not a guess.
func TestColonyStatusReportsMoodLedger(t *testing.T) {
	data, err := os.ReadFile("../policy/testdata/mood-provision-pressured.json")
	if err != nil {
		t.Fatal(err)
	}
	var pawn policy.MoodPawn
	if err = snapshot.Decode(data, &pawn); err != nil {
		t.Fatal(err)
	}
	rows, known := pawn.Thoughts.Value()
	if !known {
		t.Fatal("recorded thoughts unknown")
	}
	rows = append(rows, policy.MoodThought{Def: "SleptInBarracks", Offset: -4})
	ledger := policy.BuildMoodLedger([]policy.MoodLedgerPawn{
		{ID: pawn.ID, Thoughts: domain.Known(rows), Traits: domain.Known([]string{}), Precepts: domain.Known([]string{}), Expectation: domain.Unknown[string]()},
		{ID: "unread", Thoughts: domain.Unknown[[]policy.MoodThought]()},
	}, nil)
	dto := projectColonyStatus(buildingruntime.ColonyStatusReport{MoodLedger: domain.Known(ledger)})
	b, err := json.Marshal(dto.MoodLedger)
	if err != nil {
		t.Fatal(err)
	}
	want := `{"sources":[{"def":"NeedJoy","pawns":1,"lost":-10,"owners":["EnsureComfort"],"unverified":1},{"def":"EnvironmentCold","pawns":1,"lost":-4,"owners":["EnsureTemperatureSafety"],"unverified":1},{"def":"SleptInBarracks","pawns":1,"lost":-4,"owners":[],"unverified":1},{"def":"SleptOutside","pawns":1,"lost":-4,"owners":["MaintainHousing"],"unverified":1}],` +
		`"unowned":[{"def":"SleptInBarracks","pawns":1,"lost":-4,"owners":[],"unverified":1}],"unknownPawns":1,"expectation":[]}`
	if string(b) != want {
		t.Fatalf("ledger = %s\nwant     %s", b, want)
	}
	if projectColonyStatus(buildingruntime.ColonyStatusReport{}).MoodLedger != nil {
		t.Fatal("an unfiled ledger must be null")
	}
}

type colonyStatusFixture struct {
	report buildingruntime.ColonyStatusReport
	err    error
}

func (f *colonyStatusFixture) Read(context.Context) (buildingruntime.ColonyStatusReport, error) {
	return f.report, f.err
}

func TestColonyStatusNotEnabled(t *testing.T) {
	s, _ := playerAPI(t)
	r := httptest.NewRequest("GET", "http://127.0.0.1/api/player/colony", nil)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	if w.Code != 404 {
		t.Fatal(w.Code, w.Body.String())
	}
}

func TestColonyStatusReadSuccess(t *testing.T) {
	s, _ := playerAPI(t)
	s.config.ColonyStatus = &colonyStatusFixture{report: buildingruntime.ColonyStatusReport{
		Tick: 1200, Colonists: domain.Known[int64](3), Workers: domain.Known[int64](2),
		FoodNutrition: domain.Known(12.5), FoodRunwayDays: domain.Known(2.25), FoodCorpses: 1,
		Threat:          bridge.ColonyThreat{RaidPoints: domain.Known(120.5), WealthTotal: domain.Known(7400.0), WealthItems: domain.Known(1200.0)},
		Shrines:         domain.Known([]policy.AncientShrine{{ID: "AncientShrineGroup_1", Sealed: true, Caskets: []policy.ShrineCasket{{EntityID: "c1", HasContents: true}, {EntityID: "c2"}}, BreachWalls: []policy.ShrineBreachWall{{EntityID: "w1"}}}}),
		PlayerTechLevel: domain.Known("Neolithic"), TechTier: domain.Known(policy.TechTierMasonry),
		Stockpiles: domain.Known([]policy.StockpileRoleCount{{Role: "general", Zones: 1, Cells: 40, Used: 12}}), ForbiddenSupplies: domain.Known(true),
		ShrineReadiness: []buildingruntime.ShrineReadinessReport{{Shrine: "AncientShrineGroup_1", Readiness: policy.ShrineReadiness{Reason: policy.ShrineHoldNoTraps, Wall: policy.ShrineBreachWall{EntityID: "w1"}, Squad: []domain.PawnID{"p1", "p2"}, Traps: 1}}},
		Pawns: []buildingruntime.ColonyStatusPawn{
			{ID: "p1", Label: "Ann", Downed: domain.Known(true), Mood: domain.Known(0.2), Food: domain.Known(0.1), Share: domain.Known(400.0), Spent: domain.Known(150.0), Remaining: domain.Known(250.0)},
			{ID: "p2", Label: "Bob", Downed: domain.Known(false), Mood: domain.Known(0.6), Share: domain.Known(400.0)},
			{ID: "p3", Label: "Cy"},
		},
	}}
	r := httptest.NewRequest("GET", "http://127.0.0.1/api/player/colony", nil)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	body := w.Body.String()
	for _, want := range []string{`"tick":1200`, `"colonists":3`, `"foodRunwayDays":2.25`, `"pendingFoodNutrition":null`, `"foodCorpses":1`, `"raidPoints":120.5`, `"wealthTotal":7400`, `"wealthItems":1200`, `"wealthBuildings":null`, `"wealthPawns":null`, `"playerTechLevel":"Neolithic"`, `"techTier":"Masonry"`, `"stockpiles":[{"role":"general","zones":1,"cells":40,"used":12}]`, `"forbiddenSupplies":true`, `"shrines":[{"id":"AncientShrineGroup_1","sealed":true,"inHome":false,"caskets":2,"filledCaskets":1,"guardsKnown":false,"guardsAlive":true,"breachWalls":1,"ready":false,"reason":"no_traps","wall":"w1","squad":2,"traps":1}]`, `"downed":1`, `"moodMean":0.4`, `"food":0.1,"share":400,"spent":150,"remaining":250`, `"food":null,"share":400,"spent":null,"remaining":null`, `"id":"p3","label":"Cy","downed":null,"mood":null,"food":null,"share":null,"spent":null,"remaining":null`} {
		if w.Code != 200 || !strings.Contains(body, want) {
			t.Fatal(w.Code, want, body)
		}
	}
}

func TestColonyStatusReadPropagatesFailure(t *testing.T) {
	s, _ := playerAPI(t)
	s.config.ColonyStatus = &colonyStatusFixture{err: errors.New("native observation context changed")}
	r := httptest.NewRequest("GET", "http://127.0.0.1/api/player/colony", nil)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	if w.Code != 503 || !strings.Contains(w.Body.String(), "native observation context changed") {
		t.Fatal(w.Code, w.Body.String())
	}
}

func TestColonyStatusRejectsQueryAndBody(t *testing.T) {
	s, _ := playerAPI(t)
	s.config.ColonyStatus = &colonyStatusFixture{}
	r := httptest.NewRequest("GET", "http://127.0.0.1/api/player/colony?x=1", nil)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	if w.Code != 400 {
		t.Fatal(w.Code, w.Body.String())
	}
	r2 := httptest.NewRequest("GET", "http://127.0.0.1/api/player/colony", strings.NewReader("{}"))
	r2.ContentLength = 2
	w2 := httptest.NewRecorder()
	s.Handler().ServeHTTP(w2, r2)
	if w2.Code != 400 {
		t.Fatal(w2.Code, w2.Body.String())
	}
}

// #708: the food plan names pets the colony runway no longer carries.
func TestColonyStatusReportsPetShortfalls(t *testing.T) {
	for _, test := range []struct {
		pets []policy.ConsumerFoodForecast
		want string
	}{
		{nil, `"petShortfalls":[]`},
		{[]policy.ConsumerFoodForecast{{ID: "Thing_Cat5169", NutritionPerDay: 0.3}}, `"petShortfalls":[{"id":"Thing_Cat5169","label":"Whiskers","runwayDays":0,"nutritionPerDay":0.3}]`},
	} {
		s, _ := playerAPI(t)
		plan := policy.FoodPlan{Forecast: policy.FoodForecast{PetShortfalls: test.pets}}
		s.config.ColonyStatus = &colonyStatusFixture{report: buildingruntime.ColonyStatusReport{Tick: 1, FoodPlan: domain.Known(plan), PetLabels: map[policy.PawnID]string{"Thing_Cat5169": "Whiskers"}}}
		w := httptest.NewRecorder()
		s.Handler().ServeHTTP(w, httptest.NewRequest("GET", "http://127.0.0.1/api/player/colony", nil))
		if w.Code != 200 || !strings.Contains(w.Body.String(), test.want) {
			t.Fatal(w.Code, test.want, w.Body.String())
		}
	}
}
