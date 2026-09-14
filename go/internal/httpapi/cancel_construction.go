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

// cancelConstructionSubmissionDTO reports what a cancellation resolved to.
// planId and actionId are empty and observedAbsent is true when every
// placement of the named intent had already resolved -- built, cancelled or
// failed -- so there was nothing left to withdraw. That is a successful
// outcome, not a failure: it is the wire form of the Python command's
// observed-absent result, and it carries no actionIds because no native work
// was journalled.
type cancelConstructionSubmissionDTO struct {
	RequestID      string              `json:"requestId"`
	Expected       Identity            `json:"expected"`
	IntentID       string              `json:"intentId"`
	SourcePlanID   domain.PlanID       `json:"sourcePlanId"`
	ObservedAbsent bool                `json:"observedAbsent"`
	PlanID         domain.PlanID       `json:"planId,omitempty"`
	ActionID       domain.ActionID     `json:"actionId,omitempty"`
	ActionIDs      []domain.ActionID   `json:"actionIds"`
	Targets        []domain.ActionID   `json:"targets"`
	Revision       domain.PlanRevision `json:"revision,string"`
}

func decodeCancelConstructionSubmission(reader io.Reader) (store.CancelConstructionSubmissionRequest, error) {
	var q store.CancelConstructionSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "intentId")
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
	return q, domain.ValidateRoomIntent(q.IntentID)
}
func projectCancelConstructionSubmission(v store.CancelConstructionSubmission) (cancelConstructionSubmissionDTO, error) {
	var zero cancelConstructionSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil ||
		domain.ValidateRoomIntent(v.Request.IntentID) != nil || buildingRequestID(string(v.Source)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid cancel construction submission")
	}
	ids := v.ActionIDs()
	if v.ObservedAbsent != (v.Plan == "") || v.ObservedAbsent != (v.Action == "") ||
		v.ObservedAbsent != (len(v.Targets) == 0) || len(ids) != len(v.Targets) {
		return zero, errors.New("invalid cancel construction submission")
	}
	if !v.ObservedAbsent {
		if buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || ids[0] != v.Action {
			return zero, errors.New("invalid cancel construction submission")
		}
	}
	for _, id := range append(append([]domain.ActionID{}, ids...), v.Targets...) {
		if buildingRequestID(string(id)) != nil {
			return zero, errors.New("invalid cancel construction submission")
		}
	}
	if ids == nil {
		ids = []domain.ActionID{}
	}
	targets := v.Targets
	if targets == nil {
		targets = []domain.ActionID{}
	}
	return cancelConstructionSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), v.Request.IntentID,
		v.Source, v.ObservedAbsent, v.Plan, v.Action, ids, targets, v.Revision}, nil
}
func (s *Server) submitCancelConstruction(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeCancelConstructionSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid cancel construction submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitCancelConstruction(ctx, q)
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
	dto, err := projectCancelConstructionSubmission(v)
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
func (s *Server) lookupCancelConstruction(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupCancelConstructionSubmission(ctx, id)
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
	dto, err := projectCancelConstructionSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
