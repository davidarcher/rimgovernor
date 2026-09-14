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

type movementSubmissionDTO struct {
	RequestID     string              `json:"requestId"`
	Expected      Identity            `json:"expected"`
	Movement      Movement            `json:"movement"`
	PlanID        domain.PlanID       `json:"planId"`
	DraftActionID domain.ActionID     `json:"draftActionId"`
	ActionID      domain.ActionID     `json:"actionId"`
	Revision      domain.PlanRevision `json:"revision,string"`
}

func decodeMovementSubmission(reader io.Reader) (store.MovementSubmissionRequest, error) {
	var q store.MovementSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "movement")
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
	movement, err := buildingFields(fields["movement"], "pawn", "x", "z")
	if err != nil {
		return q, err
	}
	if err = json.Unmarshal(movement["pawn"], &q.Pawn); err != nil {
		return q, err
	}
	if err = json.Unmarshal(movement["x"], &q.Destination.X); err != nil {
		return q, err
	}
	if err = json.Unmarshal(movement["z"], &q.Destination.Z); err != nil {
		return q, err
	}
	return q, nil
}
func projectMovementSubmission(v store.MovementSubmission) (movementSubmissionDTO, error) {
	var zero movementSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.DraftAction)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid movement submission")
	}
	if v.Request.Destination.X < 0 || v.Request.Destination.Z < 0 {
		return zero, errors.New("invalid movement")
	}
	return movementSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), Movement{v.Request.Pawn, v.Request.Destination.X, v.Request.Destination.Z}, v.Plan, v.DraftAction, v.Action, v.Revision}, nil
}
func (s *Server) submitMovement(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeMovementSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid movement submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitMovement(ctx, q)
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
	dto, err := projectMovementSubmission(v)
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
func (s *Server) lookupMovement(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupMovementSubmission(ctx, id)
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
	dto, err := projectMovementSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
