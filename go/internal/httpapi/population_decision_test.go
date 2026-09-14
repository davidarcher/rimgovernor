package httpapi

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (p *playerFixture) SubmitPopulationDecision(ctx context.Context, q store.PopulationDecisionSubmissionRequest) (store.PopulationDecisionSubmission, bool, error) {
	p.calls++
	return p.journal.SubmitPopulationDecision(ctx, q)
}
func (p *playerFixture) PopulationDecisions(ctx context.Context, w store.World) ([]domain.PopulationDirective, error) {
	return p.journal.PopulationDecisions(ctx, w)
}
func (p *playerFixture) LookupPopulationDecisionSubmission(ctx context.Context, id string) (store.PopulationDecisionSubmission, error) {
	return p.journal.LookupPopulationDecisionSubmission(ctx, id)
}

func TestPlayerPopulationDecisionHTTPSubmitReplayReadAndConflict(t *testing.T) {
	s, p := playerAPI(t)
	const replace = "/api/player/population-decision/replace"
	const expected = `"expected":{"colonyId":"colony","loadToken":"load","mapId":0}`
	request := `{"requestId":"decision",` + expected + `,"decision":{"pawn":"Thing_Human1","decision":"rescue"}}`
	if out := playerCall(s, "POST", replace, request, "bad"); out.Code != 403 || p.calls != 0 {
		t.Fatal(out.Code, p.calls)
	}
	// Rescue, capture and recruit require an established population policy.
	if out := playerCall(s, "POST", replace, request, s.playerToken); out.Code != 404 {
		t.Fatal(out.Code, out.Body.String())
	}
	policy := `{"requestId":"policy",` + expected + `,"policy":{"maximum":12,"foodDays":30}}`
	if out := playerCall(s, "POST", "/api/player/population-policy/replace", policy, s.playerToken); out.Code != 201 {
		t.Fatal(out.Code, out.Body.String())
	}
	out := playerCall(s, "POST", replace, request, s.playerToken)
	var created populationDecisionSubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &created); err != nil || out.Code != 201 || created.Decision.Pawn != "Thing_Human1" || created.Decision.Decision != "rescue" || created.Current != created.Decision {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	replay := playerCall(s, "POST", replace, request, s.playerToken)
	if replay.Code != 200 || replay.Body.String() != out.Body.String() {
		t.Fatal(replay.Code, replay.Body.String())
	}
	conflicting := `{"requestId":"decision",` + expected + `,"decision":{"pawn":"Thing_Human1","decision":"capture"}}`
	if out := playerCall(s, "POST", replace, conflicting, s.playerToken); out.Code != 409 {
		t.Fatal(out.Code, out.Body.String())
	}
	// A new request ID replaces that pawn's direction; ignore needs no policy.
	withdraw := `{"requestId":"decision-2",` + expected + `,"decision":{"pawn":"Thing_Human1","decision":"ignore"}}`
	if out := playerCall(s, "POST", replace, withdraw, s.playerToken); out.Code != 201 {
		t.Fatal(out.Code, out.Body.String())
	}
	another := `{"requestId":"decision-3",` + expected + `,"decision":{"pawn":"Thing_Human2","decision":"recruit"}}`
	if out := playerCall(s, "POST", replace, another, s.playerToken); out.Code != 201 {
		t.Fatal(out.Code, out.Body.String())
	}
	out = playerCall(s, "GET", "/api/player/population-decision?colonyId=colony&loadToken=load&mapId=0", "", "")
	var current populationDecisionsDTO
	if err := json.Unmarshal(out.Body.Bytes(), &current); err != nil || out.Code != 200 || len(current.Decisions) != 2 ||
		current.Decisions[0] != (PopulationDecision{"Thing_Human1", "ignore"}) || current.Decisions[1] != (PopulationDecision{"Thing_Human2", "recruit"}) {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	// The superseded request stays replayable and reports the newer current.
	out = playerCall(s, "GET", "/api/player/population-decision/submission?requestId=decision", "", "")
	var old populationDecisionSubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &old); err != nil || out.Code != 200 || old.Decision.Decision != "rescue" || old.Current.Decision != "ignore" {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	// A world nobody has named anyone in reads as an empty list, not an error.
	out = playerCall(s, "GET", "/api/player/population-decision?colonyId=other&loadToken=load&mapId=0", "", "")
	var empty populationDecisionsDTO
	if err := json.Unmarshal(out.Body.Bytes(), &empty); err != nil || out.Code != 200 || len(empty.Decisions) != 0 {
		t.Fatal(out.Code, out.Body.String(), err)
	}
}

func TestPlayerPopulationDecisionHTTPRejectsBadRequests(t *testing.T) {
	s, _ := playerAPI(t)
	const replace = "/api/player/population-decision/replace"
	const expected = `"expected":{"colonyId":"colony","loadToken":"load","mapId":0}`
	for _, body := range []string{
		`{"requestId":"decision",` + expected + `,"decision":{"pawn":"Thing_Human1","decision":"release"}}`,
		`{"requestId":"decision",` + expected + `,"decision":{"pawn":"Thing_Human1","decision":""}}`,
		`{"requestId":"decision",` + expected + `,"decision":{"pawn":"","decision":"ignore"}}`,
		`{"requestId":"decision",` + expected + `,"decision":{"pawn":"Thing_Human1"}}`,
		`{"requestId":"decision",` + expected + `,"decision":{"pawn":"Thing_Human1","decision":"ignore","extra":1}}`,
		`{"requestId":"decision","decision":{"pawn":"Thing_Human1","decision":"ignore"}}`,
		`{"requestId":"",` + expected + `,"decision":{"pawn":"Thing_Human1","decision":"ignore"}}`,
	} {
		if out := playerCall(s, "POST", replace, body, s.playerToken); out.Code != 400 {
			t.Fatal(out.Code, body, out.Body.String())
		}
	}
	for _, query := range []string{
		"",
		"?colonyId=colony",
		"?colonyId=colony&loadToken=load",
		"?colonyId=colony&loadToken=load&mapId=x",
		"?colonyId=colony&loadToken=load&mapId=0&mapId=1",
	} {
		if out := playerCall(s, "GET", "/api/player/population-decision"+query, "", ""); out.Code != 400 {
			t.Fatal(out.Code, query, out.Body.String())
		}
	}
	if out := playerCall(s, "GET", "/api/player/population-decision/submission?requestId=missing", "", ""); out.Code == 200 {
		t.Fatal(out.Code, out.Body.String())
	}
	if out := playerCall(s, "POST", "/api/player/population-decision", "{}", s.playerToken); out.Code != 405 {
		t.Fatal(out.Code, out.Body.String())
	}
}
