package httpapi

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func (p *playerFixture) SubmitRoomAdoption(ctx context.Context, q store.RoomAdoptionSubmissionRequest) (store.RoomAdoptionSubmission, bool, error) {
	p.calls++
	return p.journal.SubmitRoomAdoption(ctx, q)
}
func (p *playerFixture) LookupRoomAdoptionSubmission(ctx context.Context, id string) (store.RoomAdoptionSubmission, error) {
	return p.journal.LookupRoomAdoptionSubmission(ctx, id)
}
func (p *playerFixture) AdoptedShelter(ctx context.Context, w store.World) (store.RoomAdoptionSubmission, error) {
	return p.journal.AdoptedShelter(ctx, w)
}

const adoptExpected = `"expected":{"colonyId":"colony","loadToken":"load","mapId":0},"expectedDirection":"1","planId":"plan"`
const adoptNative = `"native":{"roomId":"native-room-1","cells":9,"role":"Bedroom","roleLabel":"bedroom"}`

func TestAdoptRoomHTTPClaimReplayAndRead(t *testing.T) {
	s, p := playerAPI(t)
	const claim = "/api/player/adopt-room/claim"
	request := `{"requestId":"adopt-one",` + adoptExpected + `,"intentId":"shelter-a","room":{"x":10,"z":10,"width":5,"height":5,"entrance":"south"},` + adoptNative + `,"tick":10}`
	if out := playerCall(s, "POST", claim, request, "bad"); out.Code != 403 || p.calls != 0 {
		t.Fatal(out.Code, p.calls)
	}
	out := playerCall(s, "POST", claim, request, s.playerToken)
	var created roomAdoptionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &created); err != nil || out.Code != 201 {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	// The adopted goal is complete, not merely activated: this is the whole
	// point of the command.
	if created.State.Source != "player" || created.State.Status != "satisfied" || created.State.Need != "recovered" || created.State.GoalID == "" {
		t.Fatal("adoption did not report a completed shelter goal", out.Body.String())
	}
	if created.IntentID != "shelter-a" || created.Native.RoomID != "native-room-1" || created.Native.Cells != 9 || created.Orders != adoptionOrders {
		t.Fatal(out.Body.String())
	}
	// A rectangular adoption reports no exact interior or door.
	if created.Room.InteriorCell != nil || created.Room.EntranceCell != nil || created.Room.Entrance != "south" {
		t.Fatal("rectangular adoption reported an exact shape", out.Body.String())
	}
	replay := playerCall(s, "POST", claim, request, s.playerToken)
	if replay.Code != 200 || replay.Body.String() != out.Body.String() {
		t.Fatal(replay.Code, replay.Body.String())
	}
	conflicting := `{"requestId":"adopt-one",` + adoptExpected + `,"intentId":"shelter-b","room":{"x":10,"z":10,"width":5,"height":5,"entrance":"south"},` + adoptNative + `,"tick":10}`
	if out := playerCall(s, "POST", claim, conflicting, s.playerToken); out.Code != 409 {
		t.Fatal(out.Code, out.Body.String())
	}
	out = playerCall(s, "GET", "/api/player/adopt-room/submission?requestId=adopt-one", "", "")
	var found roomAdoptionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &found); err != nil || out.Code != 200 || found.State.GoalID != created.State.GoalID {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	out = playerCall(s, "GET", "/api/player/adopt-room?colonyId=colony&loadToken=load&mapId=0", "", "")
	var preferred roomAdoptionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &preferred); err != nil || out.Code != 200 || preferred.RequestID != "adopt-one" {
		t.Fatal(out.Code, out.Body.String(), err)
	}
}

func TestAdoptRoomHTTPNonrectangular(t *testing.T) {
	s, _ := playerAPI(t)
	const interior = `"interiorCells":[{"x":11,"z":11},{"x":12,"z":11},{"x":13,"z":11},{"x":11,"z":12},{"x":12,"z":12},{"x":13,"z":12},{"x":11,"z":13},{"x":12,"z":13},{"x":13,"z":13}],"entranceCell":{"x":12,"z":10}`
	request := `{"requestId":"adopt-shape",` + adoptExpected + `,"intentId":"shelter-a","room":{"x":10,"z":10,"width":5,"height":5,"entrance":"south",` + interior + `},` + adoptNative + `,"tick":10}`
	out := playerCall(s, "POST", "/api/player/adopt-room/claim", request, s.playerToken)
	var created roomAdoptionDTO
	if err := json.Unmarshal(out.Body.Bytes(), &created); err != nil || out.Code != 201 {
		t.Fatal(out.Code, out.Body.String(), err)
	}
	if len(created.Room.InteriorCell) != 9 || created.Room.EntranceCell == nil || created.Room.EntranceCell.X != 12 || created.Room.EntranceCell.Z != 10 {
		t.Fatal("nonrectangular adoption did not round trip", out.Body.String())
	}
}

func TestAdoptRoomHTTPRejections(t *testing.T) {
	s, _ := playerAPI(t)
	for _, body := range []string{
		// Half-supplied nonrectangular shape.
		`{"requestId":"a",` + adoptExpected + `,"intentId":"shelter-a","room":{"x":10,"z":10,"width":5,"height":5,"entrance":"south","interiorCells":[{"x":11,"z":11}]},` + adoptNative + `,"tick":10}`,
		// An explicit null for an optional field is not absence.
		`{"requestId":"a",` + adoptExpected + `,"intentId":"shelter-a","room":{"x":10,"z":10,"width":5,"height":5,"entrance":"south","entranceCell":null},` + adoptNative + `,"tick":10}`,
		// A field adoption has no business carrying.
		`{"requestId":"a",` + adoptExpected + `,"intentId":"shelter-a","room":{"x":10,"z":10,"width":5,"height":5,"entrance":"south","wallDef":"Wall"},` + adoptNative + `,"tick":10}`,
		// Evidence that does not describe the inspected interior.
		`{"requestId":"a",` + adoptExpected + `,"intentId":"shelter-a","room":{"x":10,"z":10,"width":5,"height":5,"entrance":"south"},"native":{"roomId":"native-room-1","cells":8,"role":"","roleLabel":""},"tick":10}`,
		// An intent identity outside Python's own pattern.
		`{"requestId":"a",` + adoptExpected + `,"intentId":"not valid!","room":{"x":10,"z":10,"width":5,"height":5,"entrance":"south"},` + adoptNative + `,"tick":10}`,
	} {
		if out := playerCall(s, "POST", "/api/player/adopt-room/claim", body, s.playerToken); out.Code == 200 || out.Code == 201 {
			t.Fatalf("accepted %s -> %d", body, out.Code)
		}
	}
}
