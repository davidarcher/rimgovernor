package httpapi

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (p *playerFixture) SubmitPopulationPolicy(ctx context.Context, q store.PopulationPolicySubmissionRequest) (store.PopulationPolicySubmission, bool, error) {
	p.calls++
	return p.journal.SubmitPopulationPolicy(ctx, q)
}
func (p *playerFixture) PopulationPolicy(ctx context.Context, w store.World) (domain.PopulationPolicy, error) {
	return p.journal.CurrentPopulationPolicy(ctx, w)
}
func (p *playerFixture) LookupPopulationPolicySubmission(ctx context.Context, id string) (store.PopulationPolicySubmission, error) {
	return p.journal.LookupPopulationPolicySubmission(ctx, id)
}

func TestPlayerPopulationPolicyHTTPSubmitReplayReadAndConflict(t *testing.T) {
	s, p := playerAPI(t)
	const replace = "/api/player/population-policy/replace"
	request := `{"requestId":"policy","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"policy":{"maximum":12,"foodDays":30}}`
	if out := playerCall(s, "POST", replace, request, "bad"); out.Code != 403 || p.calls != 0 {
		t.Fatal(out.Code, p.calls)
	}
	out := playerCall(s, "POST", replace, request, s.playerToken)
	var created populationPolicySubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &created); err != nil || out.Code != 201 || created.Policy.Maximum != 12 || created.Policy.FoodDays != 30 || created.Current != created.Policy {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	replay := playerCall(s, "POST", replace, request, s.playerToken)
	if replay.Code != 200 || replay.Body.String() != out.Body.String() {
		t.Fatal(replay.Code, replay.Body.String())
	}
	conflicting := `{"requestId":"policy","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"policy":{"maximum":20,"foodDays":30}}`
	if out := playerCall(s, "POST", replace, conflicting, s.playerToken); out.Code != 409 {
		t.Fatal(out.Code, out.Body.String())
	}
	// A new request ID with new values becomes the current policy.
	next := `{"requestId":"policy-2","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"policy":{"maximum":20,"foodDays":45}}`
	if out := playerCall(s, "POST", replace, next, s.playerToken); out.Code != 201 {
		t.Fatal(out.Code, out.Body.String())
	}
	out = playerCall(s, "GET", "/api/player/population-policy?colonyId=colony&loadToken=load&mapId=0", "", "")
	var current populationPolicyDTO
	if err := json.Unmarshal(out.Body.Bytes(), &current); err != nil || out.Code != 200 || current.Policy.Maximum != 20 || current.Policy.FoodDays != 45 {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	// The superseded request stays replayable and reports the newer current.
	out = playerCall(s, "GET", "/api/player/population-policy/submission?requestId=policy", "", "")
	var old populationPolicySubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &old); err != nil || out.Code != 200 || old.Policy.Maximum != 12 || old.Current.Maximum != 20 {
		t.Fatal(out.Code, out.Body.String(), err)
	}
}

func TestPlayerPopulationPolicyHTTPRejectsBadRequests(t *testing.T) {
	s, _ := playerAPI(t)
	const replace = "/api/player/population-policy/replace"
	for _, body := range []string{
		`{"requestId":"policy","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"policy":{"maximum":0,"foodDays":30}}`,
		`{"requestId":"policy","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"policy":{"maximum":101,"foodDays":30}}`,
		`{"requestId":"policy","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"policy":{"maximum":12,"foodDays":0}}`,
		`{"requestId":"policy","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"policy":{"maximum":12,"foodDays":121}}`,
		`{"requestId":"policy","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"policy":{"maximum":12}}`,
		`{"requestId":"policy","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"policy":{"maximum":12,"foodDays":30,"extra":1}}`,
		`{"requestId":"policy","policy":{"maximum":12,"foodDays":30}}`,
		`{"requestId":"","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"policy":{"maximum":12,"foodDays":30}}`,
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
		"?colonyId=colony&loadToken=load&mapId=00",
		"?colonyId=colony&loadToken=load&mapId=0&mapId=1",
	} {
		if out := playerCall(s, "GET", "/api/player/population-policy"+query, "", ""); out.Code != 400 {
			t.Fatal(out.Code, query, out.Body.String())
		}
	}
	// An unset world has no policy to read.
	if out := playerCall(s, "GET", "/api/player/population-policy?colonyId=other&loadToken=load&mapId=0", "", ""); out.Code == 200 {
		t.Fatal(out.Code, out.Body.String())
	}
	if out := playerCall(s, "GET", "/api/player/population-policy/submission?requestId=missing", "", ""); out.Code == 200 {
		t.Fatal(out.Code, out.Body.String())
	}
	if out := playerCall(s, "POST", "/api/player/population-policy", "{}", s.playerToken); out.Code != 405 {
		t.Fatal(out.Code, out.Body.String())
	}
}

func TestPlayerPopulationPolicyRaidThreshold(t *testing.T) {
	s, _ := playerAPI(t)
	const body = `{"requestId":"raid-policy","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"policy":{"maximum":12,"foodDays":30,"raidThreshold":300.5}}`
	out := playerCall(s, "POST", "/api/player/population-policy/replace", body, s.playerToken)
	var got populationPolicySubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &got); err != nil || out.Code != 201 || got.Current.RaidThreshold != 300.5 {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	out = playerCall(s, "GET", "/api/player/population-policy?colonyId=colony&loadToken=load&mapId=0", "", "")
	var current populationPolicyDTO
	if err := json.Unmarshal(out.Body.Bytes(), &current); err != nil || out.Code != 200 || current.Policy.RaidThreshold != 300.5 {
		t.Fatal(out.Code, out.Body.String(), err)
	}
}
