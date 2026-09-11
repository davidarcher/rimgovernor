package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"io"
	"net/http"
)

type draftSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Draft     Draft               `json:"draft"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeDraftSubmission(reader io.Reader) (store.DraftSubmissionRequest, error) {
	var q store.DraftSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "draft")
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
	draft, err := buildingFields(fields["draft"], "pawnId")
	if err != nil {
		return q, err
	}
	var pawn domain.PawnID
	if err = json.Unmarshal(draft["pawnId"], &pawn); err != nil {
		return q, err
	}
	q.Draft, err = domain.NewOwnedDraft(pawn)
	return q, err
}
func projectDraftSubmission(v store.DraftSubmission) (draftSubmissionDTO, error) {
	var zero draftSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid draft submission")
	}
	if _, err := domain.NewOwnedDraft(v.Request.Draft.Pawn()); err != nil {
		return zero, err
	}
	return draftSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), Draft{v.Request.Draft.Pawn()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitDraft(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeDraftSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid draft submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitDraft(ctx, q)
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
	dto, err := projectDraftSubmission(v)
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
func (s *Server) lookupDraft(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupDraftSubmission(ctx, id)
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
	dto, err := projectDraftSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
