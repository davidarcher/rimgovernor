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

type researchSelectSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Select    ResearchSelect      `json:"select"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeResearchSelectSubmission(reader io.Reader) (store.ResearchSelectSubmissionRequest, error) {
	var r store.ResearchSelectSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "select")
	if err != nil {
		return r, err
	}
	if err = json.Unmarshal(fields["requestId"], &r.RequestID); err != nil {
		return r, err
	}
	if err = buildingRequestID(r.RequestID); err != nil {
		return r, err
	}
	if r.World, err = buildingWorld(fields["expected"]); err != nil {
		return r, err
	}
	sel, err := buildingFields(fields["select"], "project")
	if err != nil {
		return r, err
	}
	var project string
	if err = json.Unmarshal(sel["project"], &project); err != nil {
		return r, err
	}
	r.Select, err = domain.NewResearchSelect(project)
	return r, err
}
func projectResearchSelectSubmission(v store.ResearchSelectSubmission) (researchSelectSubmissionDTO, error) {
	var zero researchSelectSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid research select submission")
	}
	canonical, err := domain.NewResearchSelect(v.Request.Select.Project())
	if err != nil || canonical != v.Request.Select {
		return zero, errors.New("invalid research select")
	}
	return researchSelectSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), ResearchSelect{v.Request.Select.Project()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitResearchSelect(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeResearchSelectSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid research select submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitResearchSelect(ctx, q)
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
	dto, err := projectResearchSelectSubmission(v)
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
func (s *Server) lookupResearchSelect(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupResearchSelectSubmission(ctx, id)
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
	dto, err := projectResearchSelectSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
