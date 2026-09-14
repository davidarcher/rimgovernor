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

type husbandrySubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Husbandry Husbandry           `json:"husbandry"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeHusbandrySubmission(reader io.Reader) (store.HusbandrySubmissionRequest, error) {
	var q store.HusbandrySubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "husbandry")
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
	husbandry, err := buildingFields(fields["husbandry"], "animal", "method", "trainableDef")
	if err != nil {
		return q, err
	}
	var animal domain.PawnID
	var method domain.HusbandryMethod
	var trainableDef string
	if err = json.Unmarshal(husbandry["animal"], &animal); err != nil {
		return q, err
	}
	if err = json.Unmarshal(husbandry["method"], &method); err != nil {
		return q, err
	}
	if err = json.Unmarshal(husbandry["trainableDef"], &trainableDef); err != nil {
		return q, err
	}
	q.Husbandry, err = domain.NewHusbandry(animal, method, trainableDef)
	return q, err
}
func projectHusbandrySubmission(v store.HusbandrySubmission) (husbandrySubmissionDTO, error) {
	var zero husbandrySubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid husbandry submission")
	}
	h := v.Request.Husbandry
	canonical, err := domain.NewHusbandry(h.Animal(), h.Method(), h.TrainableDef())
	if err != nil || canonical != h {
		return zero, errors.New("invalid husbandry")
	}
	return husbandrySubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), Husbandry{h.Animal(), string(h.Method()), h.TrainableDef()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitHusbandry(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeHusbandrySubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid husbandry submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitHusbandry(ctx, q)
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
	dto, err := projectHusbandrySubmission(v)
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
func (s *Server) lookupHusbandry(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupHusbandrySubmission(ctx, id)
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
	dto, err := projectHusbandrySubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
