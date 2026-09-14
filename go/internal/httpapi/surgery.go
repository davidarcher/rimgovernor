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

type surgerySubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Surgery   Surgery             `json:"surgery"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeSurgerySubmission(reader io.Reader) (store.SurgerySubmissionRequest, error) {
	var q store.SurgerySubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "surgery")
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
	surgery, err := buildingFields(fields["surgery"], "patient", "recipe", "part")
	if err != nil {
		return q, err
	}
	var patient domain.PawnID
	var recipe string
	var part int32
	if err = json.Unmarshal(surgery["patient"], &patient); err != nil {
		return q, err
	}
	if err = json.Unmarshal(surgery["recipe"], &recipe); err != nil {
		return q, err
	}
	if err = json.Unmarshal(surgery["part"], &part); err != nil {
		return q, err
	}
	q.Surgery, err = domain.NewSurgery(patient, recipe, part)
	return q, err
}
func projectSurgerySubmission(v store.SurgerySubmission) (surgerySubmissionDTO, error) {
	var zero surgerySubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid surgery submission")
	}
	s := v.Request.Surgery
	canonical, err := domain.NewSurgery(s.Patient(), s.Recipe(), s.Part())
	if err != nil || canonical != s {
		return zero, errors.New("invalid surgery")
	}
	return surgerySubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), Surgery{s.Patient(), s.Recipe(), s.Part()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitSurgery(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeSurgerySubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid surgery submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitSurgery(ctx, q)
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
	dto, err := projectSurgerySubmission(v)
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
func (s *Server) lookupSurgery(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupSurgerySubmission(ctx, id)
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
	dto, err := projectSurgerySubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
