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

type caravanDepartureSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Departure CaravanDeparture    `json:"departure"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeCaravanDepartureSubmission(reader io.Reader) (store.CaravanDepartureSubmissionRequest, error) {
	var q store.CaravanDepartureSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "departure")
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
	departure, err := buildingFields(fields["departure"], "crew", "cargo", "destinationTile")
	if err != nil {
		return q, err
	}
	var crew []domain.PawnID
	if err = json.Unmarshal(departure["crew"], &crew); err != nil {
		return q, err
	}
	var cargoDTO []CargoItem
	if err = json.Unmarshal(departure["cargo"], &cargoDTO); err != nil {
		return q, err
	}
	cargo := make([]domain.CargoItem, len(cargoDTO))
	for i, item := range cargoDTO {
		cargo[i] = domain.CargoItem{Definition: item.Definition, Count: item.Count}
	}
	var destinationTile int32
	if err = json.Unmarshal(departure["destinationTile"], &destinationTile); err != nil {
		return q, err
	}
	q.Departure, err = domain.NewCaravanDeparture(crew, cargo, destinationTile)
	return q, err
}
func projectCaravanDepartureSubmission(v store.CaravanDepartureSubmission) (caravanDepartureSubmissionDTO, error) {
	var zero caravanDepartureSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid caravan departure submission")
	}
	d := v.Request.Departure
	canonical, err := domain.NewCaravanDeparture(d.Crew(), d.Cargo(), d.DestinationTile())
	if err != nil || canonical != d {
		return zero, errors.New("invalid caravan departure")
	}
	cargo := d.Cargo()
	cargoDTO := make([]CargoItem, len(cargo))
	for i, item := range cargo {
		cargoDTO[i] = CargoItem{Definition: item.Definition, Count: item.Count}
	}
	return caravanDepartureSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), CaravanDeparture{d.Crew(), cargoDTO, d.DestinationTile()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitCaravanDeparture(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeCaravanDepartureSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid caravan departure submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitCaravanDeparture(ctx, q)
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
	dto, err := projectCaravanDepartureSubmission(v)
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
func (s *Server) lookupCaravanDeparture(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupCaravanDepartureSubmission(ctx, id)
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
	dto, err := projectCaravanDepartureSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
