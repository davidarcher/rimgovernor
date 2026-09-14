package httpapi

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (p *playerFixture) SubmitResourcePolicy(ctx context.Context, q store.ResourcePolicySubmissionRequest) (store.ResourcePolicySubmission, bool, error) {
	p.calls++
	return p.journal.SubmitResourcePolicy(ctx, q)
}
func (p *playerFixture) ResourcePolicies(ctx context.Context, w store.World) ([]domain.ResourceDirective, error) {
	return p.journal.ResourcePolicies(ctx, w)
}
func (p *playerFixture) LookupResourcePolicySubmission(ctx context.Context, id string) (store.ResourcePolicySubmission, error) {
	return p.journal.LookupResourcePolicySubmission(ctx, id)
}

func TestPlayerResourcePolicyHTTPSubmitReplayReadAndConflict(t *testing.T) {
	s, p := playerAPI(t)
	const update = "/api/player/resource-policy/update"
	const expected = `"expected":{"colonyId":"colony","loadToken":"load","mapId":0}`
	request := `{"requestId":"reserve",` + expected + `,"policy":{"resource":"Steel","reserve":250}}`
	if out := playerCall(s, "POST", update, request, "bad"); out.Code != 403 || p.calls != 0 {
		t.Fatal(out.Code, p.calls)
	}
	out := playerCall(s, "POST", update, request, s.playerToken)
	var created resourcePolicySubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &created); err != nil || out.Code != 201 ||
		created.Applied != (ResourcePolicy{"Steel", 250, "normal"}) || created.Current != created.Applied ||
		created.PlanID == "" || created.ActionID == "" || created.Revision != 1 {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	replay := playerCall(s, "POST", update, request, s.playerToken)
	if replay.Code != 200 || replay.Body.String() != out.Body.String() {
		t.Fatal(replay.Code, replay.Body.String())
	}
	conflicting := `{"requestId":"reserve",` + expected + `,"policy":{"resource":"Steel","reserve":300}}`
	if out := playerCall(s, "POST", update, conflicting, s.playerToken); out.Code != 409 {
		t.Fatal(out.Code, out.Body.String())
	}
	// A spending change preserves the reserve the earlier request established.
	spending := `{"requestId":"spending",` + expected + `,"policy":{"resource":"Steel","spending":"defense_only"}}`
	out = playerCall(s, "POST", update, spending, s.playerToken)
	var restricted resourcePolicySubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &restricted); err != nil || out.Code != 201 ||
		restricted.Applied != (ResourcePolicy{"Steel", 250, "defense_only"}) || restricted.PlanID == created.PlanID {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	another := `{"requestId":"wood",` + expected + `,"policy":{"resource":"WoodLog","reserve":100}}`
	if out := playerCall(s, "POST", update, another, s.playerToken); out.Code != 201 {
		t.Fatal(out.Code, out.Body.String())
	}
	out = playerCall(s, "GET", "/api/player/resource-policy?colonyId=colony&loadToken=load&mapId=0", "", "")
	var current resourcePoliciesDTO
	if err := json.Unmarshal(out.Body.Bytes(), &current); err != nil || out.Code != 200 || len(current.Policies) != 2 ||
		current.Policies[0] != (ResourcePolicy{"Steel", 250, "defense_only"}) || current.Policies[1] != (ResourcePolicy{"WoodLog", 100, "normal"}) {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	// The superseded request stays replayable and reports the newer current.
	out = playerCall(s, "GET", "/api/player/resource-policy/submission?requestId=reserve", "", "")
	var old resourcePolicySubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &old); err != nil || out.Code != 200 ||
		old.Applied.Spending != "normal" || old.Current.Spending != "defense_only" {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	// A world with no declared policy reads as an empty list, not an error.
	out = playerCall(s, "GET", "/api/player/resource-policy?colonyId=other&loadToken=load&mapId=0", "", "")
	var empty resourcePoliciesDTO
	if err := json.Unmarshal(out.Body.Bytes(), &empty); err != nil || out.Code != 200 || len(empty.Policies) != 0 {
		t.Fatal(out.Code, out.Body.String(), err)
	}
}

func TestPlayerResourcePolicyHTTPRejectsBadRequests(t *testing.T) {
	s, _ := playerAPI(t)
	const update = "/api/player/resource-policy/update"
	const expected = `"expected":{"colonyId":"colony","loadToken":"load","mapId":0}`
	for _, body := range []string{
		`{"requestId":"r",` + expected + `,"policy":{"resource":"Steel","spending":"hoard"}}`,
		`{"requestId":"r",` + expected + `,"policy":{"resource":"Steel","spending":""}}`,
		`{"requestId":"r",` + expected + `,"policy":{"resource":"","reserve":5}}`,
		`{"requestId":"r",` + expected + `,"policy":{"resource":"Steel","reserve":-1}}`,
		`{"requestId":"r",` + expected + `,"policy":{"resource":"Steel","reserve":10001}}`,
		`{"requestId":"r",` + expected + `,"policy":{"resource":"Steel"}}`,
		// Each command sets exactly one half; naming both is a third command.
		`{"requestId":"r",` + expected + `,"policy":{"resource":"Steel","reserve":5,"spending":"stop"}}`,
		`{"requestId":"r",` + expected + `,"policy":{"resource":"Steel","reserve":5,"extra":1}}`,
		`{"requestId":"r","policy":{"resource":"Steel","reserve":5}}`,
		`{"requestId":"",` + expected + `,"policy":{"resource":"Steel","reserve":5}}`,
	} {
		if out := playerCall(s, "POST", update, body, s.playerToken); out.Code != 400 {
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
		if out := playerCall(s, "GET", "/api/player/resource-policy"+query, "", ""); out.Code != 400 {
			t.Fatal(out.Code, query, out.Body.String())
		}
	}
	if out := playerCall(s, "GET", "/api/player/resource-policy/submission?requestId=missing", "", ""); out.Code == 200 {
		t.Fatal(out.Code, out.Body.String())
	}
	if out := playerCall(s, "POST", "/api/player/resource-policy", "{}", s.playerToken); out.Code != 405 {
		t.Fatal(out.Code, out.Body.String())
	}
}
