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

type tendSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Tend      Tend                `json:"tend"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeTendSubmission(reader io.Reader) (store.TendSubmissionRequest, error) {
	var q store.TendSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "tend")
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
	tend, err := buildingFields(fields["tend"], "doctor", "patient")
	if err != nil {
		return q, err
	}
	var doctor, patient domain.PawnID
	for key, target := range map[string]any{"doctor": &doctor, "patient": &patient} {
		if err = json.Unmarshal(tend[key], target); err != nil {
			return q, err
		}
	}
	q.Tend, err = domain.NewTend(doctor, patient)
	return q, err
}
func projectTendSubmission(v store.TendSubmission) (tendSubmissionDTO, error) {
	var zero tendSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid tend submission")
	}
	t := v.Request.Tend
	canonical, err := domain.NewTend(t.Doctor(), t.Patient())
	if err != nil || canonical != t {
		return zero, errors.New("invalid tend")
	}
	return tendSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), Tend{t.Doctor(), t.Patient()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitTend(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeTendSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid tend submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitTend(ctx, q)
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
	dto, err := projectTendSubmission(v)
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
func (s *Server) lookupTend(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupTendSubmission(ctx, id)
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
	dto, err := projectTendSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
