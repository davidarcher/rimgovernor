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

type recoveryServiceSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Service   RecoveryService     `json:"service"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeRecoveryServiceSubmission(reader io.Reader) (store.RecoveryServiceSubmissionRequest, error) {
	var q store.RecoveryServiceSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "service")
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
	service, err := buildingFields(fields["service"], "pawn", "thing", "method")
	if err != nil {
		return q, err
	}
	var pawn domain.PawnID
	var thing string
	var method domain.RecoveryMethod
	if err = json.Unmarshal(service["pawn"], &pawn); err != nil {
		return q, err
	}
	if err = json.Unmarshal(service["thing"], &thing); err != nil {
		return q, err
	}
	if err = json.Unmarshal(service["method"], &method); err != nil {
		return q, err
	}
	q.Service, err = domain.NewRecoveryService(pawn, thing, method)
	return q, err
}
func projectRecoveryServiceSubmission(v store.RecoveryServiceSubmission) (recoveryServiceSubmissionDTO, error) {
	var zero recoveryServiceSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid recovery service submission")
	}
	r := v.Request.Service
	canonical, err := domain.NewRecoveryService(r.Pawn(), r.Thing(), r.Method())
	if err != nil || canonical != r {
		return zero, errors.New("invalid recovery service")
	}
	return recoveryServiceSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), RecoveryService{r.Pawn(), r.Thing(), string(r.Method())}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitRecoveryService(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeRecoveryServiceSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid recovery service submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitRecoveryService(ctx, q)
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
	dto, err := projectRecoveryServiceSubmission(v)
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
func (s *Server) lookupRecoveryService(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupRecoveryServiceSubmission(ctx, id)
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
	dto, err := projectRecoveryServiceSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
