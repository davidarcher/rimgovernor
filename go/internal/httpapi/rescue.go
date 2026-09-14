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

type rescueSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Rescue    Rescue              `json:"rescue"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeRescueSubmission(reader io.Reader) (store.RescueSubmissionRequest, error) {
	var q store.RescueSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "rescue")
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
	rescue, err := buildingFields(fields["rescue"], "rescuer", "patient")
	if err != nil {
		return q, err
	}
	var rescuer, patient domain.PawnID
	for key, target := range map[string]any{"rescuer": &rescuer, "patient": &patient} {
		if err = json.Unmarshal(rescue[key], target); err != nil {
			return q, err
		}
	}
	q.Rescue, err = domain.NewRescue(rescuer, patient)
	return q, err
}
func projectRescueSubmission(v store.RescueSubmission) (rescueSubmissionDTO, error) {
	var zero rescueSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid rescue submission")
	}
	r := v.Request.Rescue
	canonical, err := domain.NewRescue(r.Rescuer(), r.Patient())
	if err != nil || canonical != r {
		return zero, errors.New("invalid rescue")
	}
	return rescueSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), Rescue{r.Rescuer(), r.Patient()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitRescue(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeRescueSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid rescue submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitRescue(ctx, q)
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
	dto, err := projectRescueSubmission(v)
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
func (s *Server) lookupRescue(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupRescueSubmission(ctx, id)
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
	dto, err := projectRescueSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
