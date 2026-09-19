package httpapi

import (
	"context"
	"errors"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

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
		Threat: bridge.ColonyThreat{RaidPoints: domain.Known(120.5), WealthTotal: domain.Known(7400.0), WealthItems: domain.Known(1200.0)},
		Pawns: []buildingruntime.ColonyStatusPawn{
			{ID: "p1", Label: "Ann", Downed: domain.Known(true), Mood: domain.Known(0.2), Food: domain.Known(0.1)},
			{ID: "p2", Label: "Bob", Downed: domain.Known(false), Mood: domain.Known(0.6)},
			{ID: "p3", Label: "Cy"},
		},
	}}
	r := httptest.NewRequest("GET", "http://127.0.0.1/api/player/colony", nil)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	body := w.Body.String()
	for _, want := range []string{`"tick":1200`, `"colonists":3`, `"foodRunwayDays":2.25`, `"pendingFoodNutrition":null`, `"foodCorpses":1`, `"raidPoints":120.5`, `"wealthTotal":7400`, `"wealthItems":1200`, `"wealthBuildings":null`, `"wealthPawns":null`, `"downed":1`, `"moodMean":0.4`, `"id":"p3","label":"Cy","downed":null,"mood":null,"food":null`} {
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
