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

// relocateConstructionSubmissionDTO reports what a relocation committed.
//
// planId is the one plan holding both halves. cancelActionIds are the
// withdrawals of the superseded construction's live orders and buildActionIds
// the replacement's placements; the latter cannot be admitted until the former
// have completed, which is recorded as action dependencies inside that plan and
// enforced at admission rather than restated here.
//
// fastPath true means nothing of the superseded construction had ever been
// dispatched, so there was no native order to withdraw: cancelActionId and
// cancelActionIds are then empty and targets is empty. That is the wire form of
// the Python command's never-issued early exit, and it is a successful outcome,
// not a degraded one.
type relocateConstructionSubmissionDTO struct {
	RequestID       string              `json:"requestId"`
	Expected        Identity            `json:"expected"`
	IntentID        string              `json:"intentId"`
	Replacement     RoomShell           `json:"replacement"`
	SourcePlanID    domain.PlanID       `json:"sourcePlanId"`
	FastPath        bool                `json:"fastPath"`
	PlanID          domain.PlanID       `json:"planId"`
	CancelActionID  domain.ActionID     `json:"cancelActionId,omitempty"`
	BuildActionID   domain.ActionID     `json:"buildActionId"`
	CancelActionIDs []domain.ActionID   `json:"cancelActionIds"`
	BuildActionIDs  []domain.ActionID   `json:"buildActionIds"`
	Targets         []domain.ActionID   `json:"targets"`
	Revision        domain.PlanRevision `json:"revision,string"`
}

func decodeRelocateConstructionSubmission(reader io.Reader) (store.RelocateConstructionSubmissionRequest, error) {
	var q store.RelocateConstructionSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "intentId", "replacement")
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
	q.Replacement, err = decodeRoomShellFields(fields["replacement"])
	return q, err
}

func projectRelocateConstructionSubmission(v store.RelocateConstructionSubmission) (relocateConstructionSubmissionDTO, error) {
	var zero relocateConstructionSubmissionDTO
	cancels, builds := v.CancelActionIDs(), v.BuildActionIDs()
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil ||
		domain.ValidateRoomIntent(v.Request.IntentID) != nil || buildingRequestID(string(v.Source)) != nil ||
		buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.BuildAction)) != nil ||
		v.Revision == 0 || len(builds) == 0 || builds[0] != v.BuildAction {
		return zero, errors.New("invalid relocate construction submission")
	}
	if v.FastPath != (len(v.Targets) == 0) || v.FastPath != (v.CancelAction == "") || len(cancels) != len(v.Targets) {
		return zero, errors.New("invalid relocate construction submission")
	}
	if !v.FastPath && (buildingRequestID(string(v.CancelAction)) != nil || cancels[0] != v.CancelAction) {
		return zero, errors.New("invalid relocate construction submission")
	}
	for _, id := range append(append(append([]domain.ActionID{}, cancels...), builds...), v.Targets...) {
		if buildingRequestID(string(id)) != nil {
			return zero, errors.New("invalid relocate construction submission")
		}
	}
	if cancels == nil {
		cancels = []domain.ActionID{}
	}
	targets := v.Targets
	if targets == nil {
		targets = []domain.ActionID{}
	}
	return relocateConstructionSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), v.Request.IntentID,
		roomShellDTO(v.Request.Replacement), v.Source, v.FastPath, v.Plan, v.CancelAction, v.BuildAction,
		cancels, builds, targets, v.Revision}, nil
}

func (s *Server) submitRelocateConstruction(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeRelocateConstructionSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid relocate construction submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitRelocateConstruction(ctx, q)
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
	dto, err := projectRelocateConstructionSubmission(v)
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

func (s *Server) lookupRelocateConstruction(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupRelocateConstructionSubmission(ctx, id)
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
	dto, err := projectRelocateConstructionSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
