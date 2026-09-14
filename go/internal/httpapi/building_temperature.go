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

type buildingTemperatureSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Patch     BuildingTemperature `json:"patch"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeBuildingTemperatureSubmission(reader io.Reader) (store.BuildingTemperatureSubmissionRequest, error) {
	var q store.BuildingTemperatureSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "patch")
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
	patch, err := buildingFields(fields["patch"], "thing", "celsius", "before")
	if err != nil {
		return q, err
	}
	var thing, before string
	var celsius float64
	if err = json.Unmarshal(patch["thing"], &thing); err != nil {
		return q, err
	}
	if err = json.Unmarshal(patch["celsius"], &celsius); err != nil {
		return q, err
	}
	if err = json.Unmarshal(patch["before"], &before); err != nil {
		return q, err
	}
	q.Patch, err = domain.NewBuildingTemperature(thing, celsius, before)
	return q, err
}
func projectBuildingTemperatureSubmission(v store.BuildingTemperatureSubmission) (buildingTemperatureSubmissionDTO, error) {
	var zero buildingTemperatureSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid building temperature submission")
	}
	b := v.Request.Patch
	canonical, err := domain.NewBuildingTemperature(b.Thing(), b.Celsius(), b.BeforeToken())
	if err != nil || canonical != b {
		return zero, errors.New("invalid building temperature")
	}
	return buildingTemperatureSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), BuildingTemperature{b.Thing(), b.Celsius(), b.BeforeToken()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitBuildingTemperature(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeBuildingTemperatureSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid building temperature submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitBuildingTemperature(ctx, q)
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
	dto, err := projectBuildingTemperatureSubmission(v)
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
func (s *Server) lookupBuildingTemperature(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupBuildingTemperatureSubmission(ctx, id)
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
	dto, err := projectBuildingTemperatureSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
