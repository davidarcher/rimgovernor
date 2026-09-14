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

type travelCaravanSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Travel    TravelCaravan       `json:"travel"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeTravelCaravanSubmission(reader io.Reader) (store.TravelCaravanSubmissionRequest, error) {
	var q store.TravelCaravanSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "travel")
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
	travel, err := buildingFields(fields["travel"], "caravan", "kind", "destinationTile")
	if err != nil {
		return q, err
	}
	var caravan domain.CaravanID
	var kind domain.TravelKind
	var destinationTile int32
	for key, target := range map[string]any{"caravan": &caravan, "kind": &kind, "destinationTile": &destinationTile} {
		if err = json.Unmarshal(travel[key], target); err != nil {
			return q, err
		}
	}
	q.Travel, err = domain.NewTravelCaravan(caravan, kind, destinationTile)
	return q, err
}
func projectTravelCaravanSubmission(v store.TravelCaravanSubmission) (travelCaravanSubmissionDTO, error) {
	var zero travelCaravanSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid travel caravan submission")
	}
	t := v.Request.Travel
	canonical, err := domain.NewTravelCaravan(t.Caravan(), t.Kind(), t.DestinationTile())
	if err != nil || canonical != t {
		return zero, errors.New("invalid travel caravan")
	}
	return travelCaravanSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), TravelCaravan{t.Caravan(), string(t.Kind()), t.DestinationTile()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitTravelCaravan(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeTravelCaravanSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid travel caravan submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitTravelCaravan(ctx, q)
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
	dto, err := projectTravelCaravanSubmission(v)
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
func (s *Server) lookupTravelCaravan(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupTravelCaravanSubmission(ctx, id)
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
	dto, err := projectTravelCaravanSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
