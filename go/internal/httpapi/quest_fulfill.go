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

type questFulfillSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Fulfill   QuestFulfill        `json:"fulfill"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeQuestFulfillSubmission(reader io.Reader) (store.QuestFulfillSubmissionRequest, error) {
	var q store.QuestFulfillSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "fulfill")
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
	fulfill, err := buildingFields(fields["fulfill"], "quest", "caravan", "crewIds")
	if err != nil {
		return q, err
	}
	var quest domain.QuestID
	var caravan domain.CaravanID
	var crew []domain.PawnID
	if err = json.Unmarshal(fulfill["quest"], &quest); err != nil {
		return q, err
	}
	if err = json.Unmarshal(fulfill["caravan"], &caravan); err != nil {
		return q, err
	}
	if err = json.Unmarshal(fulfill["crewIds"], &crew); err != nil {
		return q, err
	}
	q.Fulfill, err = domain.NewQuestFulfill(quest, caravan, crew)
	return q, err
}
func projectQuestFulfillSubmission(v store.QuestFulfillSubmission) (questFulfillSubmissionDTO, error) {
	var zero questFulfillSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid quest fulfill submission")
	}
	f := v.Request.Fulfill
	canonical, err := domain.NewQuestFulfill(f.Quest(), f.Caravan(), f.CrewIDs())
	if err != nil || canonical != f {
		return zero, errors.New("invalid quest fulfill")
	}
	return questFulfillSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), QuestFulfill{f.Quest(), f.Caravan(), f.CrewIDs()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitQuestFulfill(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeQuestFulfillSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid quest fulfill submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitQuestFulfill(ctx, q)
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
	dto, err := projectQuestFulfillSubmission(v)
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
func (s *Server) lookupQuestFulfill(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupQuestFulfillSubmission(ctx, id)
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
	dto, err := projectQuestFulfillSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
