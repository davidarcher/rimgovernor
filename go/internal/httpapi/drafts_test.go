package httpapi

import (
	"context"
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"strings"
	"testing"
)

const draftJSON = `{"requestId":"draft-request","expected":{"colonyId":"colony","loadToken":"load","mapId":0},"draft":{"pawnId":"pawn"}}`

func TestDraftHTTPSubmissionReplayAndProjection(t *testing.T) {
	s, f := playerAPI(t)
	first := playerCall(s, "POST", "/api/drafts/plans", draftJSON, s.playerToken)
	if first.Code != 201 {
		t.Fatal(first.Code, first.Body.String())
	}
	var dto draftSubmissionDTO
	if err := json.Unmarshal(first.Body.Bytes(), &dto); err != nil {
		t.Fatal(err)
	}
	for _, r := range []struct{ method, path, body string }{{"POST", "/api/drafts/plans", draftJSON}, {"GET", "/api/drafts/submission?requestId=draft-request", ""}} {
		w := playerCall(s, r.method, r.path, r.body, s.playerToken)
		if w.Code != 200 || w.Body.String() != first.Body.String() {
			t.Fatal(w.Code, w.Body.String())
		}
	}
	conflict := playerCall(s, "POST", "/api/drafts/plans", strings.Replace(draftJSON, `"pawn"`, `"other"`, 1), s.playerToken)
	if conflict.Code != 409 {
		t.Fatal(conflict.Code)
	}
	q, err := decodeBuildingSubmission(strings.NewReader(submissionJSON))
	if err != nil {
		t.Fatal(err)
	}
	cross := playerCall(s, "POST", "/api/buildings/plans", strings.Replace(submissionJSON, `"requestId":"`+q.RequestID+`"`, `"requestId":"draft-request"`, 1), s.playerToken)
	if cross.Code != 409 {
		t.Fatal(cross.Code, cross.Body.String())
	}
	q.RequestID = "draft-request"
	if _, _, err = f.journal.SubmitBuilding(context.Background(), q); err == nil {
		t.Fatal("cross-family request reused")
	}
	state, err := f.journal.LoadPlan(context.Background(), dto.PlanID)
	if err != nil {
		t.Fatal(err)
	}
	projected, err := plan(state)
	if err != nil {
		t.Fatal(err)
	}
	a := projected.Actions[0]
	if a.Building != nil || a.Draft == nil || a.Draft.PawnID != "pawn" || a.Progress.DraftCleanup != nil {
		t.Fatal(a)
	}
	snap := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: dto.PlanID, Revision: 1, Direction: 1, Native: 1}
	if _, err = f.journal.PrepareDraft(context.Background(), dto.PlanID, dto.ActionID, store.DraftAdmission{Snapshot: snap, Tick: 1, Pawn: "pawn", PawnSnapshotToken: "cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = f.journal.Dispatch(context.Background(), dto.PlanID, dto.ActionID, snap, 1); err != nil {
		t.Fatal(err)
	}
	state, err = f.journal.LoadPlan(context.Background(), dto.PlanID)
	if err != nil {
		t.Fatal(err)
	}
	projected, err = plan(state)
	if err != nil {
		t.Fatal(err)
	}
	if projected.Actions[0].Progress.DraftCleanup.Stage != domain.DraftAwaitingClaim {
		t.Fatal(projected)
	}
	raw, _ := json.Marshal(projected)
	for _, secret := range []string{"claimId", "controllerSession", "lease", "snapshotToken", "building"} {
		if strings.Contains(string(raw), secret) {
			t.Fatal(string(raw))
		}
	}
}
func TestDraftHTTPRejectsMalformedAndUnauthenticated(t *testing.T) {
	s, f := playerAPI(t)
	for _, body := range []string{`null`, draftJSON + `{}`, strings.Replace(draftJSON, `"pawnId":"pawn"`, `"pawnId":null`, 1), strings.Replace(draftJSON, `"pawnId":"pawn"`, `"pawnId":"a","pawnId":"b"`, 1), strings.Replace(draftJSON, `"pawnId":"pawn"`, `"pawnId":"pawn","drafted":false`, 1), strings.Replace(draftJSON, `"pawn"`, `"\ud800"`, 1), strings.Repeat(" ", 8193) + draftJSON} {
		w := playerCall(s, "POST", "/api/drafts/plans", body, s.playerToken)
		if w.Code != 400 {
			t.Fatal(w.Code, w.Body.String())
		}
	}
	for _, tc := range []struct {
		method, path, body, token string
		status                    int
	}{{"POST", "/api/drafts/plans", draftJSON, "", 403}, {"GET", "/api/drafts/plans", "", "", 405}, {"POST", "/api/drafts/plans?x=1", draftJSON, s.playerToken, 400}, {"GET", "/api/drafts/submission", "", "", 400}, {"GET", "/api/drafts/submission?requestId=x&requestId=y", "", "", 400}, {"GET", "/api/drafts/submission?requestId=missing", "", "", 404}} {
		w := playerCall(s, tc.method, tc.path, tc.body, tc.token)
		if w.Code != tc.status {
			t.Fatal(tc.path, w.Code, w.Body.String())
		}
	}
	if f.calls != 0 {
		t.Fatal("invalid request reached player", f.calls)
	}
	for _, path := range []string{"/api/buildings/session", "/api/buildings/control", "/api/buildings/control/acquire", "/api/buildings/control/manual"} {
		w := playerCall(s, "GET", path, "", "")
		if w.Code != 404 {
			t.Fatal("legacy route", path, w.Code)
		}
	}
}
