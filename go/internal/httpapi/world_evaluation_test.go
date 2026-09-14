package httpapi

import (
	"context"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type worldEvaluationFixture struct {
	report policy.WorldEvaluationReport
	err    error
}

func (f *worldEvaluationFixture) Read(context.Context) (policy.WorldEvaluationReport, error) {
	return f.report, f.err
}

func TestWorldEvaluationNotEnabled(t *testing.T) {
	s, _ := playerAPI(t)
	r := httptest.NewRequest("GET", "http://127.0.0.1/api/player/world-evaluation", nil)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	if w.Code != 404 {
		t.Fatal(w.Code, w.Body.String())
	}
}

func TestWorldEvaluationReadSuccess(t *testing.T) {
	s, _ := playerAPI(t)
	route := policy.WorldEvaluationRouteFact{DestinationMapID: 1, Reachable: true, EstimatedTicks: 6000, EstimatedTicksKnown: true}
	f := &worldEvaluationFixture{report: policy.WorldEvaluationReport{
		Readable: true,
		Caravans: []policy.WorldEvaluationCaravanReport{{
			ID: "caravan-1", RecoveryRequired: false, Healthy: true, FoodDays: 3, FoodDaysKnown: true,
			ReachableHome: &route, Recommendation: "Observed return route available",
		}},
		Quests: []policy.WorldEvaluationQuestReport{{
			ID: "quest-1", State: "Ongoing", NativeEligible: true,
			ResourceDeficits: map[string]int64{"Steel": 35}, ResourceScope: "Current home stock; carried cargo is listed separately",
			CarriedCandidates: []string{"caravan-1"},
			Objectives:        []policy.WorldEvaluationTradeRequestFact{{Resource: "Steel", Count: 40, DestinationTile: 7}},
			Recommendation:    "Cargo observed in listed parties; validate native quality, freshness and settlement eligibility",
		}},
		Policy: policy.WorldEvaluationPolicy{TravelFoodMarginDays: 0.5},
		Scope:  "Read-only evaluation; no automatic quest acceptance, diplomatic escalation or expedition orders",
	}}
	s.config.WorldEvaluation = f
	r := httptest.NewRequest("GET", "http://127.0.0.1/api/player/world-evaluation", nil)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	body := w.Body.String()
	if w.Code != 200 || !strings.Contains(body, `"id":"caravan-1"`) || !strings.Contains(body, `"destinationMapId":1`) ||
		!strings.Contains(body, `"resourceDeficits":{"Steel":35}`) || !strings.Contains(body, `"carriedCandidates":["caravan-1"]`) ||
		!strings.Contains(body, `"travelFoodMarginDays":0.5`) {
		t.Fatal(w.Code, body)
	}
}

func TestWorldEvaluationReadUnreadable(t *testing.T) {
	s, _ := playerAPI(t)
	f := &worldEvaluationFixture{report: policy.WorldEvaluationReport{Readable: false, Reason: "Complete native world evidence is required"}}
	s.config.WorldEvaluation = f
	r := httptest.NewRequest("GET", "http://127.0.0.1/api/player/world-evaluation", nil)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	body := w.Body.String()
	if w.Code != 200 || !strings.Contains(body, `"readable":false`) || !strings.Contains(body, `"caravans":[]`) || !strings.Contains(body, `"quests":[]`) {
		t.Fatal(w.Code, body)
	}
}

func TestWorldEvaluationReadPropagatesFailure(t *testing.T) {
	s, _ := playerAPI(t)
	f := &worldEvaluationFixture{err: store.ErrNotFound}
	s.config.WorldEvaluation = f
	r := httptest.NewRequest("GET", "http://127.0.0.1/api/player/world-evaluation", nil)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	if w.Code != 404 {
		t.Fatal(w.Code, w.Body.String())
	}
}

func TestWorldEvaluationRejectsQueryAndBody(t *testing.T) {
	s, _ := playerAPI(t)
	s.config.WorldEvaluation = &worldEvaluationFixture{}
	r := httptest.NewRequest("GET", "http://127.0.0.1/api/player/world-evaluation?x=1", nil)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	if w.Code != 400 {
		t.Fatal(w.Code, w.Body.String())
	}
	r2 := httptest.NewRequest("GET", "http://127.0.0.1/api/player/world-evaluation", strings.NewReader("{}"))
	r2.ContentLength = 2
	w2 := httptest.NewRecorder()
	s.Handler().ServeHTTP(w2, r2)
	if w2.Code != 400 {
		t.Fatal(w2.Code, w2.Body.String())
	}
}

func TestWorldEvaluationRequiresGet(t *testing.T) {
	s, _ := playerAPI(t)
	s.config.WorldEvaluation = &worldEvaluationFixture{}
	r := httptest.NewRequest("POST", "http://127.0.0.1/api/player/world-evaluation", nil)
	w := httptest.NewRecorder()
	s.Handler().ServeHTTP(w, r)
	if w.Code != 405 {
		t.Fatal(w.Code, w.Body.String())
	}
}
