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

type questAcceptSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Accept    QuestAccept         `json:"accept"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeQuestAcceptSubmission(reader io.Reader) (store.QuestAcceptSubmissionRequest, error) {
	var q store.QuestAcceptSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "accept")
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
	accept, err := buildingFields(fields["accept"], "quest", "accepterPawn", "rewardChoice")
	if err != nil {
		return q, err
	}
	var quest domain.QuestID
	var accepterPawn domain.PawnID
	var rewardChoice int32
	for key, target := range map[string]any{"quest": &quest, "accepterPawn": &accepterPawn, "rewardChoice": &rewardChoice} {
		if err = json.Unmarshal(accept[key], target); err != nil {
			return q, err
		}
	}
	q.Accept, err = domain.NewQuestAccept(quest, accepterPawn, rewardChoice)
	return q, err
}
func projectQuestAcceptSubmission(v store.QuestAcceptSubmission) (questAcceptSubmissionDTO, error) {
	var zero questAcceptSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid quest accept submission")
	}
	a := v.Request.Accept
	canonical, err := domain.NewQuestAccept(a.Quest(), a.AccepterPawn(), a.RewardChoice())
	if err != nil || canonical != a {
		return zero, errors.New("invalid quest accept")
	}
	return questAcceptSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), QuestAccept{a.Quest(), a.AccepterPawn(), a.RewardChoice()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitQuestAccept(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeQuestAcceptSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid quest accept submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitQuestAccept(ctx, q)
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
	dto, err := projectQuestAcceptSubmission(v)
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
func (s *Server) lookupQuestAccept(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupQuestAcceptSubmission(ctx, id)
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
	dto, err := projectQuestAcceptSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
