package httpapi

import (
	"context"
	"encoding/json"
	"strconv"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (p *playerFixture) SubmitGoalCreate(ctx context.Context, q store.GoalCreateSubmissionRequest) (store.GoalCreateSubmission, bool, error) {
	p.calls++
	return p.journal.SubmitGoalCreate(ctx, q)
}
func (p *playerFixture) CancelGoal(ctx context.Context, w store.World, id domain.GoalID, revision uint64) (store.GoalState, error) {
	p.calls++
	return p.journal.CancelPlayerGoal(ctx, w, id, revision)
}
func (p *playerFixture) PlayerGoals(ctx context.Context, w store.World) (map[domain.GoalKind]domain.GoalID, error) {
	return p.journal.PlayerGoals(ctx, w)
}
func (p *playerFixture) LookupGoalCreateSubmission(ctx context.Context, id string) (store.GoalCreateSubmission, error) {
	return p.journal.LookupGoalCreateSubmission(ctx, id)
}

const goalExpected = `"expected":{"colonyId":"colony","loadToken":"load","mapId":0},"planId":"plan"`

func TestPlayerGoalsHTTPActivateReplayReadAndCancel(t *testing.T) {
	s, p := playerAPI(t)
	const activate = "/api/player/goals/activate"
	request := `{"requestId":"food",` + goalExpected + `,"goal":"EnsureFoodSupply","tick":10}`
	if out := playerCall(s, "POST", activate, request, "bad"); out.Code != 403 || p.calls != 0 {
		t.Fatal(out.Code, p.calls)
	}
	out := playerCall(s, "POST", activate, request, s.playerToken)
	var created goalCreateSubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &created); err != nil || out.Code != 201 || created.Kind != "EnsureFoodSupply" ||
		created.State.Source != "player" || created.State.Status != "active" || created.State.Need != "deficit" ||
		created.State.Priority != 2 || created.State.GoalID == "" {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	replay := playerCall(s, "POST", activate, request, s.playerToken)
	if replay.Code != 200 || replay.Body.String() != out.Body.String() {
		t.Fatal(replay.Code, replay.Body.String())
	}
	conflicting := `{"requestId":"food",` + goalExpected + `,"goal":"MaintainWood","tick":10}`
	if out := playerCall(s, "POST", activate, conflicting, s.playerToken); out.Code != 409 {
		t.Fatal(out.Code, out.Body.String())
	}
	waste := `{"requestId":"waste",` + goalExpected + `,"goal":"MaintainWaste","tick":11}`
	if out = playerCall(s, "POST", activate, waste, s.playerToken); out.Code != 201 {
		t.Fatal(out.Code, out.Body.String())
	}
	out = playerCall(s, "GET", "/api/player/goals?colonyId=colony&loadToken=load&mapId=0", "", "")
	var listed playerGoalsDTO
	if err := json.Unmarshal(out.Body.Bytes(), &listed); err != nil || out.Code != 200 || len(listed.Goals) != 2 ||
		listed.Goals[0].Kind != "EnsureFoodSupply" || listed.Goals[1].Kind != "MaintainWaste" {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	out = playerCall(s, "GET", "/api/player/goals/submission?requestId=food", "", "")
	var found goalCreateSubmissionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &found); err != nil || out.Code != 200 || found.State.GoalID != created.State.GoalID {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	// Cancellation needs the goal's current CAS revision; a stale one conflicts.
	const cancel = "/api/player/goals/cancel"
	stale := `{"expected":{"colonyId":"colony","loadToken":"load","mapId":0},"goalId":"` + string(created.State.GoalID) + `","revision":"99"}`
	if out := playerCall(s, "POST", cancel, stale, s.playerToken); out.Code != 409 {
		t.Fatal(out.Code, out.Body.String())
	}
	good := `{"expected":{"colonyId":"colony","loadToken":"load","mapId":0},"goalId":"` + string(created.State.GoalID) + `","revision":"` + strconv.FormatUint(found.State.Revision, 10) + `"}`
	out = playerCall(s, "POST", cancel, good, s.playerToken)
	var cancelled goalStateDTO
	if err := json.Unmarshal(out.Body.Bytes(), &cancelled); err != nil || out.Code != 200 ||
		cancelled.GoalID != created.State.GoalID || cancelled.Status != "cancelled" {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	// A world with no activated goal reads as an empty list, not an error.
	out = playerCall(s, "GET", "/api/player/goals?colonyId=other&loadToken=load&mapId=0", "", "")
	var empty playerGoalsDTO
	if err := json.Unmarshal(out.Body.Bytes(), &empty); err != nil || out.Code != 200 || len(empty.Goals) != 0 {
		t.Fatal(out.Code, out.Body.String(), err)
	}
}

func TestPlayerGoalsHTTPRejectsBadRequests(t *testing.T) {
	s, _ := playerAPI(t)
	const activate = "/api/player/goals/activate"
	for _, body := range []string{
		// Unwhitelisted or absent kinds, and Python's per-goal target fields,
		// which have no Go counterpart and are not silently dropped.
		`{"requestId":"r",` + goalExpected + `,"goal":"EnsureComfort","tick":1}`,
		`{"requestId":"r",` + goalExpected + `,"goal":"","tick":1}`,
		`{"requestId":"r",` + goalExpected + `,"tick":1}`,
		`{"requestId":"r",` + goalExpected + `,"goal":"EnsureFoodSupply","tick":1,"foodDays":14}`,
		`{"requestId":"r",` + goalExpected + `,"goal":"EnsureFoodSupply","tick":-1}`,
		`{"requestId":"r",` + goalExpected + `,"goal":"EnsureFoodSupply","tick":1.5}`,
		`{"requestId":"r",` + goalExpected + `,"goal":"EnsureFoodSupply"}`,
		`{"requestId":"r","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"planId":"","goal":"EnsureFoodSupply","tick":1}`,
		`{"requestId":"",` + goalExpected + `,"goal":"EnsureFoodSupply","tick":1}`,
		`{"goal":"EnsureFoodSupply","tick":1}`,
	} {
		if out := playerCall(s, "POST", activate, body, s.playerToken); out.Code != 400 {
			t.Fatal(out.Code, body, out.Body.String())
		}
	}
	for _, body := range []string{
		`{"expected":{"colonyId":"colony","loadToken":"load","mapId":0},"goalId":"","revision":"1"}`,
		`{"expected":{"colonyId":"colony","loadToken":"load","mapId":0},"goalId":"g"}`,
		`{"expected":{"colonyId":"colony","loadToken":"load","mapId":0},"goalId":"g","revision":1}`,
		`{"goalId":"g","revision":"1"}`,
	} {
		if out := playerCall(s, "POST", "/api/player/goals/cancel", body, s.playerToken); out.Code != 400 {
			t.Fatal(out.Code, body, out.Body.String())
		}
	}
	// An unrecorded identity is not cancellable, and there is no fuzzy fallback.
	unknown := `{"expected":{"colonyId":"colony","loadToken":"load","mapId":0},"goalId":"EnsureFoodSupply","revision":"0"}`
	if out := playerCall(s, "POST", "/api/player/goals/cancel", unknown, s.playerToken); out.Code == 200 {
		t.Fatal(out.Code, out.Body.String())
	}
	for _, query := range []string{"", "?colonyId=colony", "?colonyId=colony&loadToken=load&mapId=x"} {
		if out := playerCall(s, "GET", "/api/player/goals"+query, "", ""); out.Code != 400 {
			t.Fatal(out.Code, query, out.Body.String())
		}
	}
	if out := playerCall(s, "GET", "/api/player/goals/submission?requestId=missing", "", ""); out.Code == 200 {
		t.Fatal(out.Code, out.Body.String())
	}
	if out := playerCall(s, "POST", "/api/player/goals", "{}", s.playerToken); out.Code != 405 {
		t.Fatal(out.Code, out.Body.String())
	}
}
