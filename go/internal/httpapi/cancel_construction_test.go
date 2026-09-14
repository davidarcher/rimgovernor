package httpapi

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const cancelConstructionJSON = `{"requestId":"cancel","expected":` + requestWorld + `,"intentId":"shelter-01"}`

func TestCancelConstructionRequestTypedMapping(t *testing.T) {
	q, err := decodeCancelConstructionSubmission(strings.NewReader(cancelConstructionJSON))
	if err != nil || q.RequestID != "cancel" || q.IntentID != "shelter-01" || q.World != (store.World{Colony: "colony", Load: "load", Map: 0}) {
		t.Fatal(q, err)
	}
	for _, body := range []string{
		`{"requestId":"cancel","expected":` + requestWorld + `}`,
		`{"requestId":"cancel","expected":` + requestWorld + `,"intentId":""}`,
		`{"requestId":"cancel","expected":` + requestWorld + `,"intentId":"not valid"}`,
		// There is no per-placement selector on the wire either: a whole named
		// construction is the only unit this command withdraws.
		`{"requestId":"cancel","expected":` + requestWorld + `,"intentId":"shelter-01","actionId":"a"}`,
		`{"requestId":"   ","expected":` + requestWorld + `,"intentId":"shelter-01"}`,
	} {
		if _, err := decodeCancelConstructionSubmission(strings.NewReader(body)); err == nil {
			t.Fatal("accepted", body)
		}
	}
}

// Nothing dispatched means nothing to withdraw, which the endpoint reports as a
// committed observed-absent submission with no plan rather than as a failure.
func TestCancelConstructionHTTPSubmissionAndLookup(t *testing.T) {
	s, _ := playerAPI(t)
	session := playerCall(s, "GET", "/api/player/session", "", "")
	var bootstrap struct{ Token string }
	if err := json.Unmarshal(session.Body.Bytes(), &bootstrap); err != nil {
		t.Fatal(err)
	}
	if room := playerCall(s, "POST", "/api/build-rooms/plans", buildRoomJSON, bootstrap.Token); room.Code != 201 {
		t.Fatal(room.Code, room.Body.String())
	}
	w := playerCall(s, "POST", "/api/cancel-constructions/plans", cancelConstructionJSON, bootstrap.Token)
	if w.Code != 201 {
		t.Fatal(w.Code, w.Body.String())
	}
	var created cancelConstructionSubmissionDTO
	if err := json.Unmarshal(w.Body.Bytes(), &created); err != nil {
		t.Fatal(err)
	}
	if created.IntentID != "shelter-01" || !created.ObservedAbsent || created.PlanID != "" || created.ActionID != "" ||
		len(created.ActionIDs) != 0 || len(created.Targets) != 0 || created.SourcePlanID == "" {
		t.Fatal("unexpected submission projection", created)
	}
	if replay := playerCall(s, "POST", "/api/cancel-constructions/plans", cancelConstructionJSON, bootstrap.Token); replay.Code != 200 {
		t.Fatal(replay.Code, replay.Body.String())
	}
	found := playerCall(s, "GET", "/api/cancel-constructions/submission?requestId=cancel", "", bootstrap.Token)
	if found.Code != 200 {
		t.Fatal(found.Code, found.Body.String())
	}
	var looked cancelConstructionSubmissionDTO
	if err := json.Unmarshal(found.Body.Bytes(), &looked); err != nil {
		t.Fatal(err)
	}
	if looked.SourcePlanID != created.SourcePlanID || looked.ObservedAbsent != created.ObservedAbsent || looked.IntentID != created.IntentID {
		t.Fatal("lookup differs from submission", looked, created)
	}
	for _, tc := range []struct {
		method, path, body string
		code               int
	}{
		{"GET", "/api/cancel-constructions/plans", "", 405},
		{"POST", "/api/cancel-constructions/submission", "", 405},
		{"POST", "/api/cancel-constructions/plans", `{}`, 400},
		{"GET", "/api/cancel-constructions/submission", "", 400},
	} {
		if got := playerCall(s, tc.method, tc.path, tc.body, bootstrap.Token); got.Code != tc.code {
			t.Fatal(tc, got.Code, got.Body.String())
		}
	}
	if unauthenticated := playerCall(s, "POST", "/api/cancel-constructions/plans", cancelConstructionJSON, ""); unauthenticated.Code != 403 {
		t.Fatal(unauthenticated.Code)
	}
}
