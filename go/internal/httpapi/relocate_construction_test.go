package httpapi

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const replacementJSON = `{"x":12,"z":13,"width":4,"height":5,"wallDef":"Wall","doorDef":"Door","material":"WoodLog","entrance":"north","purpose":"shelter"}`
const relocateConstructionJSON = `{"requestId":"relocate","expected":` + requestWorld + `,"intentId":"shelter-01","replacement":` + replacementJSON + `}`

func TestRelocateConstructionRequestTypedMapping(t *testing.T) {
	q, err := decodeRelocateConstructionSubmission(strings.NewReader(relocateConstructionJSON))
	if err != nil || q.RequestID != "relocate" || q.IntentID != "shelter-01" || q.World != (store.World{Colony: "colony", Load: "load", Map: 0}) {
		t.Fatal(q, err)
	}
	if q.Replacement.Bounds().X != 12 || q.Replacement.Material() != "WoodLog" {
		t.Fatal("replacement shell not decoded", q.Replacement)
	}
	for _, body := range []string{
		`{"requestId":"relocate","expected":` + requestWorld + `,"intentId":"shelter-01"}`,
		`{"requestId":"relocate","expected":` + requestWorld + `,"replacement":` + replacementJSON + `}`,
		`{"requestId":"relocate","expected":` + requestWorld + `,"intentId":"not valid","replacement":` + replacementJSON + `}`,
		// A partial replacement is not a relocation: the whole perimeter moves.
		`{"requestId":"relocate","expected":` + requestWorld + `,"intentId":"shelter-01","replacement":{"x":12,"z":13,"width":4,"height":5}}`,
		// There is no per-placement selector on the wire either.
		`{"requestId":"relocate","expected":` + requestWorld + `,"intentId":"shelter-01","replacement":` + replacementJSON + `,"actionId":"a"}`,
		`{"requestId":"   ","expected":` + requestWorld + `,"intentId":"shelter-01","replacement":` + replacementJSON + `}`,
	} {
		if _, err := decodeRelocateConstructionSubmission(strings.NewReader(body)); err == nil {
			t.Fatal("accepted", body)
		}
	}
}

// Nothing of the room was ever dispatched, so the relocation takes the fast
// path: the committed plan holds the replacement's placements and no
// withdrawals, which the endpoint reports as fastPath rather than as a failure.
func TestRelocateConstructionHTTPSubmissionAndLookup(t *testing.T) {
	s, _ := playerAPI(t)
	session := playerCall(s, "GET", "/api/player/session", "", "")
	var bootstrap struct{ Token string }
	if err := json.Unmarshal(session.Body.Bytes(), &bootstrap); err != nil {
		t.Fatal(err)
	}
	if room := playerCall(s, "POST", "/api/build-rooms/plans", buildRoomJSON, bootstrap.Token); room.Code != 201 {
		t.Fatal(room.Code, room.Body.String())
	}
	w := playerCall(s, "POST", "/api/relocate-constructions/plans", relocateConstructionJSON, bootstrap.Token)
	if w.Code != 201 {
		t.Fatal(w.Code, w.Body.String())
	}
	var created relocateConstructionSubmissionDTO
	if err := json.Unmarshal(w.Body.Bytes(), &created); err != nil {
		t.Fatal(err)
	}
	if created.IntentID != "shelter-01" || !created.FastPath || created.CancelActionID != "" || len(created.CancelActionIDs) != 0 ||
		len(created.Targets) != 0 || created.SourcePlanID == "" || created.PlanID == "" || created.BuildActionID == "" {
		t.Fatal("unexpected submission projection", created)
	}
	if len(created.BuildActionIDs) == 0 || created.BuildActionIDs[0] != created.BuildActionID || created.Replacement.X != 12 {
		t.Fatal("replacement placements not projected", created)
	}
	if replay := playerCall(s, "POST", "/api/relocate-constructions/plans", relocateConstructionJSON, bootstrap.Token); replay.Code != 200 {
		t.Fatal(replay.Code, replay.Body.String())
	}
	found := playerCall(s, "GET", "/api/relocate-constructions/submission?requestId=relocate", "", bootstrap.Token)
	if found.Code != 200 {
		t.Fatal(found.Code, found.Body.String())
	}
	var looked relocateConstructionSubmissionDTO
	if err := json.Unmarshal(found.Body.Bytes(), &looked); err != nil {
		t.Fatal(err)
	}
	if looked.PlanID != created.PlanID || looked.SourcePlanID != created.SourcePlanID || looked.FastPath != created.FastPath ||
		looked.BuildActionID != created.BuildActionID || looked.IntentID != created.IntentID {
		t.Fatal("lookup differs from submission", looked, created)
	}
	// Relocating to exactly the geometry the intent already names -- which after
	// the relocation above is the replacement, not the original room -- is
	// native churn for no change.
	unchanged := `{"requestId":"unchanged","expected":` + requestWorld + `,"intentId":"shelter-01","replacement":` + replacementJSON + `}`
	if got := playerCall(s, "POST", "/api/relocate-constructions/plans", unchanged, bootstrap.Token); got.Code == 201 {
		t.Fatal("unchanged geometry accepted", got.Body.String())
	}
	for _, tc := range []struct {
		method, path, body string
		code               int
	}{
		{"GET", "/api/relocate-constructions/plans", "", 405},
		{"POST", "/api/relocate-constructions/submission", "", 405},
		{"POST", "/api/relocate-constructions/plans", `{}`, 400},
		{"GET", "/api/relocate-constructions/submission", "", 400},
	} {
		if got := playerCall(s, tc.method, tc.path, tc.body, bootstrap.Token); got.Code != tc.code {
			t.Fatal(tc, got.Code, got.Body.String())
		}
	}
	if unauthenticated := playerCall(s, "POST", "/api/relocate-constructions/plans", relocateConstructionJSON, ""); unauthenticated.Code != 403 {
		t.Fatal(unauthenticated.Code)
	}
}
