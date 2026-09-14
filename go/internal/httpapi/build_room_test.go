package httpapi

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const roomJSON = `{"x":2,"z":3,"width":4,"height":5,"wallDef":"Wall","doorDef":"Door","material":"WoodLog","entrance":"south","purpose":"shelter"}`
const buildRoomJSON = `{"requestId":"request","expected":` + requestWorld + `,"intentId":"shelter-01","room":` + roomJSON + `}`

func TestBuildRoomRequestTypedMapping(t *testing.T) {
	q, err := decodeBuildRoomSubmission(strings.NewReader(buildRoomJSON))
	if err != nil || q.RequestID != "request" || q.IntentID != "shelter-01" || q.World != (store.World{Colony: "colony", Load: "load", Map: 0}) {
		t.Fatal(q, err)
	}
	if q.Room.Bounds() != (domain.RoomBounds{X: 2, Z: 3, Width: 4, Height: 5}) || q.Room.WallDefinition() != "Wall" ||
		q.Room.DoorDefinition() != "Door" || q.Room.Material() != "WoodLog" || q.Room.Entrance() != domain.South || q.Room.Purpose() != domain.ShelterRoom {
		t.Fatal("room shell mapping", q.Room)
	}
	for _, body := range []string{
		`{"requestId":"request","expected":` + requestWorld + `,"room":` + roomJSON + `}`,
		`{"requestId":"request","expected":` + requestWorld + `,"intentId":"not valid","room":` + roomJSON + `}`,
		`{"requestId":"request","expected":` + requestWorld + `,"intentId":"i","room":{"x":2,"z":3,"width":3,"height":5,"wallDef":"Wall","doorDef":"Door","material":"WoodLog","entrance":"south","purpose":"shelter"}}`,
		`{"requestId":"request","expected":` + requestWorld + `,"intentId":"i","room":{"x":2,"z":3,"width":4,"height":5,"wallDef":"Wall","doorDef":"Wall","material":"WoodLog","entrance":"south","purpose":"shelter"}}`,
		`{"requestId":"request","expected":` + requestWorld + `,"intentId":"i","room":{"x":2,"z":3,"width":4,"height":5,"wallDef":"Wall","doorDef":"Door","material":"WoodLog","entrance":"south"}}`,
		`{"requestId":"   ","expected":` + requestWorld + `,"intentId":"i","room":` + roomJSON + `}`,
	} {
		if _, err := decodeBuildRoomSubmission(strings.NewReader(body)); err == nil {
			t.Fatal("accepted", body)
		}
	}
}

// The endpoint reports the committed plan and every placement identity it
// expanded to, so the caller can follow each one through the ordinary reads.
func TestBuildRoomHTTPSubmissionAndLookup(t *testing.T) {
	s, _ := playerAPI(t)
	session := playerCall(s, "GET", "/api/player/session", "", "")
	var bootstrap struct{ Token string }
	if err := json.Unmarshal(session.Body.Bytes(), &bootstrap); err != nil {
		t.Fatal(err)
	}
	w := playerCall(s, "POST", "/api/build-rooms/plans", buildRoomJSON, bootstrap.Token)
	if w.Code != 201 {
		t.Fatal(w.Code, w.Body.String())
	}
	var created buildRoomSubmissionDTO
	if err := json.Unmarshal(w.Body.Bytes(), &created); err != nil {
		t.Fatal(err)
	}
	if created.IntentID != "shelter-01" || created.Room.Width != 4 || created.Room.Entrance != domain.South ||
		len(created.ActionIDs) != 2*4+2*(5-2) || created.ActionIDs[0] != created.ActionID {
		t.Fatal("unexpected submission projection", created)
	}
	if replay := playerCall(s, "POST", "/api/build-rooms/plans", buildRoomJSON, bootstrap.Token); replay.Code != 200 {
		t.Fatal(replay.Code, replay.Body.String())
	}
	found := playerCall(s, "GET", "/api/build-rooms/submission?requestId=request", "", bootstrap.Token)
	if found.Code != 200 {
		t.Fatal(found.Code, found.Body.String())
	}
	var looked buildRoomSubmissionDTO
	if err := json.Unmarshal(found.Body.Bytes(), &looked); err != nil {
		t.Fatal(err)
	}
	if looked.PlanID != created.PlanID || looked.ActionID != created.ActionID || len(looked.ActionIDs) != len(created.ActionIDs) {
		t.Fatal("lookup differs from submission", looked, created)
	}
	for _, tc := range []struct {
		method, path, body string
		code               int
	}{
		{"GET", "/api/build-rooms/plans", "", 405},
		{"POST", "/api/build-rooms/submission", "", 405},
		{"POST", "/api/build-rooms/plans", `{}`, 400},
		{"GET", "/api/build-rooms/submission", "", 400},
	} {
		if got := playerCall(s, tc.method, tc.path, tc.body, bootstrap.Token); got.Code != tc.code {
			t.Fatal(tc, got.Code, got.Body.String())
		}
	}
	if unauthenticated := playerCall(s, "POST", "/api/build-rooms/plans", buildRoomJSON, ""); unauthenticated.Code != 403 {
		t.Fatal(unauthenticated.Code)
	}
}
