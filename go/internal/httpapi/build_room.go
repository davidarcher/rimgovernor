package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// RoomShell is the wire form of one requested room shell. It carries the
// request, never an expansion: the committed placements are reported back as
// actionIds so the caller can follow each one through the ordinary plan and
// progress reads.
type RoomShell struct {
	X        int32              `json:"x"`
	Z        int32              `json:"z"`
	Width    int32              `json:"width"`
	Height   int32              `json:"height"`
	WallDef  string             `json:"wallDef"`
	DoorDef  string             `json:"doorDef"`
	Material string             `json:"material"`
	Entrance domain.Rotation    `json:"entrance"`
	Purpose  domain.RoomPurpose `json:"purpose"`
}
type buildRoomSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	IntentID  string              `json:"intentId"`
	Room      RoomShell           `json:"room"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	ActionIDs []domain.ActionID   `json:"actionIds"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeRoomShellFields(raw json.RawMessage) (domain.RoomShell, error) {
	var zero domain.RoomShell
	var shell RoomShell
	fields, err := buildingFields(raw, "x", "z", "width", "height", "wallDef", "doorDef", "material", "entrance", "purpose")
	if err != nil {
		return zero, err
	}
	for key, target := range map[string]any{"x": &shell.X, "z": &shell.Z, "width": &shell.Width, "height": &shell.Height,
		"wallDef": &shell.WallDef, "doorDef": &shell.DoorDef, "material": &shell.Material, "entrance": &shell.Entrance, "purpose": &shell.Purpose} {
		if err = json.Unmarshal(fields[key], target); err != nil {
			return zero, err
		}
	}
	return domain.NewRoomShell(domain.RoomBounds{X: shell.X, Z: shell.Z, Width: shell.Width, Height: shell.Height},
		shell.WallDef, shell.DoorDef, shell.Material, shell.Entrance, shell.Purpose)
}
func roomShellDTO(r domain.RoomShell) RoomShell {
	b := r.Bounds()
	return RoomShell{b.X, b.Z, b.Width, b.Height, r.WallDefinition(), r.DoorDefinition(), r.Material(), r.Entrance(), r.Purpose()}
}
func decodeBuildRoomSubmission(reader io.Reader) (store.BuildRoomSubmissionRequest, error) {
	var q store.BuildRoomSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "intentId", "room")
	if err != nil {
		return q, err
	}
	if err = json.Unmarshal(fields["requestId"], &q.RequestID); err != nil {
		return q, err
	}
	if err = buildingRequestID(q.RequestID); err != nil {
		return q, err
	}
	if q.World, err = buildingWorld(fields["expected"]); err != nil {
		return q, err
	}
	if err = json.Unmarshal(fields["intentId"], &q.IntentID); err != nil {
		return q, err
	}
	if err = domain.ValidateRoomIntent(q.IntentID); err != nil {
		return q, err
	}
	q.Room, err = decodeRoomShellFields(fields["room"])
	return q, err
}
func projectBuildRoomSubmission(v store.BuildRoomSubmission) (buildRoomSubmissionDTO, error) {
	var zero buildRoomSubmissionDTO
	ids := v.ActionIDs()
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || domain.ValidateRoomIntent(v.Request.IntentID) != nil ||
		buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 ||
		len(ids) == 0 || ids[0] != v.Action {
		return zero, errors.New("invalid build room submission")
	}
	for _, id := range ids {
		if buildingRequestID(string(id)) != nil {
			return zero, errors.New("invalid build room submission")
		}
	}
	return buildRoomSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), v.Request.IntentID,
		roomShellDTO(v.Request.Room), v.Plan, v.Action, ids, v.Revision}, nil
}
func (s *Server) submitBuildRoom(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeBuildRoomSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid build room submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitBuildRoom(ctx, q)
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		status, failure := playerFailure(err)
		s.write(w, r, status, failure)
		return
	}
	if v.Request != q {
		s.readFailure(w, r, errors.New("mismatched submission"))
		return
	}
	dto, err := projectBuildRoomSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	status := 200
	if created {
		status = 201
	}
	s.write(w, r, status, dto)
}
func (s *Server) lookupBuildRoom(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupBuildRoomSubmission(ctx, id)
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	if v.Request.RequestID != id {
		s.readFailure(w, r, errors.New("mismatched submission"))
		return
	}
	dto, err := projectBuildRoomSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
