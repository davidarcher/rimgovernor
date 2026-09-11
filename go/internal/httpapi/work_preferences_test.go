package httpapi

import (
	"context"
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (p *playerFixture) WorkPreferences(ctx context.Context, plan domain.PlanID) (store.WorkPreferences, error) {
	return p.journal.LoadWorkPreferences(ctx, plan)
}
func (p *playerFixture) SetWorkPreferences(ctx context.Context, q store.WorkPreferenceRequest) (store.WorkPreferenceRecord, error) {
	p.calls++
	return p.journal.SetWorkPreferences(ctx, q)
}

func TestPlayerWorkPreferencesHTTPAuthReadReplayAndConflict(t *testing.T) {
	s, p := playerAPI(t)
	b, _ := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 1}, domain.North, "WoodLog")
	sub, _, err := p.journal.SubmitBuilding(context.Background(), store.SubmissionRequest{RequestID: "building", World: store.World{Colony: "colony", Load: "load"}, Building: b})
	if err != nil {
		t.Fatal(err)
	}
	request := `{"requestId":"work","planId":"` + string(sub.Plan) + `","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"expectedRevision":"0","overrides":[{"pawn":"pawn","work":"Construction","priority":0}]}`
	path := "/api/player/work-preferences/replace"
	if out := playerCall(s, "POST", path, request, "bad"); out.Code != 403 || p.calls != 0 {
		t.Fatal(out.Code, p.calls)
	}
	for i := 0; i < 2; i++ {
		out := playerCall(s, "POST", path, request, s.playerToken)
		var value workPreferencesDTO
		if err := json.Unmarshal(out.Body.Bytes(), &value); err != nil || out.Code != 200 || value.Revision != 1 || len(value.Overrides) != 1 || value.Overrides[0].Priority != 0 {
			t.Fatal(out.Code, out.Body.String(), err)
		}
	}
	out := playerCall(s, "GET", "/api/player/work-preferences?planId="+string(sub.Plan), "", "")
	if out.Code != 200 || !strings.Contains(out.Body.String(), `"revision":"1"`) {
		t.Fatal(out.Code, out.Body.String())
	}
	stale := strings.Replace(request, `"requestId":"work"`, `"requestId":"stale"`, 1)
	if out := playerCall(s, "POST", path, stale, s.playerToken); out.Code != 409 {
		t.Fatal(out.Code, out.Body.String())
	}
	if out := playerCall(s, "GET", "/api/player/work-preferences?planId=x&planId=y", "", ""); out.Code != 400 {
		t.Fatal(out.Code)
	}
}

func TestDecodeWorkPreferencesRejectsAmbiguousOrInvalidIntent(t *testing.T) {
	good := `{"requestId":"work","planId":"plan","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"expectedRevision":"0","overrides":[{"pawn":"pawn","work":"Cooking","priority":0}]}`
	for _, bad := range []string{
		strings.Replace(good, `"priority":0`, `"priority":null`, 1),
		strings.Replace(good, `"priority":0`, `"priority":5`, 1),
		strings.Replace(good, `"priority":0`, `"priority":0,"priority":1`, 1),
		strings.Replace(good, `"expectedRevision":"0"`, `"expectedRevision":"00"`, 1),
		strings.Replace(good, `"expectedRevision":"0"`, `"expectedRevision":0`, 1),
		strings.Replace(good, `"overrides":[{"pawn":"pawn","work":"Cooking","priority":0}]`, `"overrides":null`, 1),
		strings.Replace(good, `"overrides":[`, `"overrides":[{"pawn":"pawn","work":"Cooking","priority":1},`, 1),
	} {
		if _, err := decodeWorkPreferences(strings.NewReader(bad)); err == nil {
			t.Fatal(bad)
		}
	}
}
