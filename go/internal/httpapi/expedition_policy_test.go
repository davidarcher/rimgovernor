package httpapi

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (p *playerFixture) SubmitExpeditionPolicy(ctx context.Context, q store.ExpeditionPolicySubmissionRequest) (store.ExpeditionPolicySubmission, bool, error) {
	p.calls++
	return p.journal.SubmitExpeditionPolicy(ctx, q)
}
func (p *playerFixture) ExpeditionPolicy(ctx context.Context, w store.World) (domain.ExpeditionPolicy, error) {
	return p.journal.CurrentExpeditionPolicy(ctx, w)
}
func (p *playerFixture) LookupExpeditionPolicySubmission(ctx context.Context, id string) (store.ExpeditionPolicySubmission, error) {
	return p.journal.LookupExpeditionPolicySubmission(ctx, id)
}

const expeditionWorld = `"expected":{"colonyId":"colony","loadToken":"load","mapId":0}`

func TestPlayerExpeditionPolicyHTTPPatchMergeReplayAndRead(t *testing.T) {
	s, p := playerAPI(t)
	const update = "/api/player/expedition-policy/update"
	request := `{"requestId":"policy",` + expeditionWorld + `,"policy":{"maximumTravelDays":9,"keepHomeDoctor":false}}`
	if out := playerCall(s, "POST", update, request, "bad"); out.Code != 403 || p.calls != 0 {
		t.Fatal(out.Code, p.calls)
	}
	out := playerCall(s, "POST", update, request, s.playerToken)
	var created expeditionPolicySubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &created); err != nil || out.Code != 201 {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	// Unnamed limits keep the contract defaults, and the request echoes back
	// only what was actually asked for.
	if created.Applied.MaximumTravelDays != 9 || created.Applied.KeepHomeDoctor ||
		created.Applied.MinimumHomeColonists != 1 || !created.Applied.RequireReturnStorage ||
		created.Applied != created.Current {
		t.Fatal(out.Body.String())
	}
	if created.Policy.MinimumHomeColonists != nil || created.Policy.MaximumTravelDays == nil || *created.Policy.MaximumTravelDays != 9 {
		t.Fatal("the echoed request must carry only the supplied limits", out.Body.String())
	}
	replay := playerCall(s, "POST", update, request, s.playerToken)
	if replay.Code != 200 || replay.Body.String() != out.Body.String() {
		t.Fatal(replay.Code, replay.Body.String())
	}
	conflicting := `{"requestId":"policy",` + expeditionWorld + `,"policy":{"maximumTravelDays":8,"keepHomeDoctor":false}}`
	if out := playerCall(s, "POST", update, conflicting, s.playerToken); out.Code != 409 {
		t.Fatal(out.Code, out.Body.String())
	}
	// A second request merges over the first rather than replacing it.
	next := `{"requestId":"policy-2",` + expeditionWorld + `,"policy":{"minimumGoodwill":10}}`
	out = playerCall(s, "POST", update, next, s.playerToken)
	var merged expeditionPolicySubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &merged); err != nil || out.Code != 201 ||
		merged.Applied.MinimumGoodwill != 10 || merged.Applied.MaximumTravelDays != 9 || merged.Applied.KeepHomeDoctor {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	out = playerCall(s, "GET", "/api/player/expedition-policy?colonyId=colony&loadToken=load&mapId=0", "", "")
	var current expeditionPolicyDTO
	if err := json.Unmarshal(out.Body.Bytes(), &current); err != nil || out.Code != 200 || current.Policy != merged.Applied {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	// The superseded request stays replayable, reports what it applied and
	// reports the newer current alongside it.
	out = playerCall(s, "GET", "/api/player/expedition-policy/submission?requestId=policy", "", "")
	var old expeditionPolicySubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &old); err != nil || out.Code != 200 ||
		old.Applied != created.Applied || old.Current != merged.Applied {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	// A world nobody has set limits for still reports the contract defaults.
	out = playerCall(s, "GET", "/api/player/expedition-policy?colonyId=other&loadToken=load&mapId=0", "", "")
	var fresh expeditionPolicyDTO
	if err := json.Unmarshal(out.Body.Bytes(), &fresh); err != nil || out.Code != 200 ||
		fresh.Policy != expeditionPolicyWire(domain.DefaultExpeditionPolicy()) {
		t.Fatal(out.Code, out.Body.String(), err)
	}
}

func TestPlayerExpeditionPolicyHTTPRejectsBadRequests(t *testing.T) {
	s, _ := playerAPI(t)
	const update = "/api/player/expedition-policy/update"
	for _, body := range []string{
		// An empty patch changes nothing and is not a request.
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"maximumTravelDays":null}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"maximumTravelDays":9,"extra":1}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"maximumTravelDays":0}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"maximumTravelDays":61}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"minimumHomeColonists":0}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"minimumHomeColonists":101}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"minimumHomeColonists":1.5}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"minimumHomeFoodDays":61}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"travelFoodMarginDays":-1}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"maximumCaravans":21}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"minimumGoodwill":101}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"minimumDestinationTemperature":51}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"maximumDestinationTemperature":-51}}`,
		// Reversed window supplied in one request.
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"minimumDestinationTemperature":40,"maximumDestinationTemperature":10}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":{"keepHomeDoctor":"no"}}`,
		`{"requestId":"policy",` + expeditionWorld + `,"policy":9}`,
		`{"requestId":"policy","policy":{"maximumTravelDays":9}}`,
		`{"requestId":"",` + expeditionWorld + `,"policy":{"maximumTravelDays":9}}`,
	} {
		if out := playerCall(s, "POST", update, body, s.playerToken); out.Code != 400 {
			t.Fatal(out.Code, body, out.Body.String())
		}
	}
	// A lone temperature end that would reverse the established window is
	// refused at merge time, not at field-range time.
	accepted := `{"requestId":"narrow",` + expeditionWorld + `,"policy":{"minimumDestinationTemperature":45}}`
	if out := playerCall(s, "POST", update, accepted, s.playerToken); out.Code == 201 {
		t.Fatal("merging past the established maximum must be refused", out.Code, out.Body.String())
	}
	for _, query := range []string{
		"",
		"?colonyId=colony",
		"?colonyId=colony&loadToken=load",
		"?colonyId=colony&loadToken=load&mapId=x",
		"?colonyId=colony&loadToken=load&mapId=00",
		"?colonyId=colony&loadToken=load&mapId=0&mapId=1",
	} {
		if out := playerCall(s, "GET", "/api/player/expedition-policy"+query, "", ""); out.Code != 400 {
			t.Fatal(out.Code, query, out.Body.String())
		}
	}
	if out := playerCall(s, "GET", "/api/player/expedition-policy/submission?requestId=missing", "", ""); out.Code == 200 {
		t.Fatal(out.Code, out.Body.String())
	}
	if out := playerCall(s, "POST", "/api/player/expedition-policy", "{}", s.playerToken); out.Code != 405 {
		t.Fatal(out.Code, out.Body.String())
	}
}
