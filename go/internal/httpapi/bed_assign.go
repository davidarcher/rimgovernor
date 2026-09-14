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

type bedAssignSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Assign    BedAssign           `json:"assign"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

// decodeBedAssignSubmission maps the wire "previousBed" string onto
// domain.PreviousBed's clear/known oneof: an empty string means the pawn
// currently owns no bed, matching the convention validID already enforces
// (no valid bed identity is ever empty).
func decodeBedAssignSubmission(reader io.Reader) (store.BedAssignSubmissionRequest, error) {
	var q store.BedAssignSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "assign")
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
	assign, err := buildingFields(fields["assign"], "pawn", "bed", "previousBed")
	if err != nil {
		return q, err
	}
	var pawn domain.PawnID
	var bed, previousBed string
	if err = json.Unmarshal(assign["pawn"], &pawn); err != nil {
		return q, err
	}
	if err = json.Unmarshal(assign["bed"], &bed); err != nil {
		return q, err
	}
	if err = json.Unmarshal(assign["previousBed"], &previousBed); err != nil {
		return q, err
	}
	var previous domain.PreviousBed
	if previousBed == "" {
		previous = domain.ClearPreviousBed()
	} else if previous, err = domain.KnownPreviousBed(previousBed); err != nil {
		return q, err
	}
	q.Assign, err = domain.NewBedAssign(pawn, bed, previous)
	return q, err
}
func projectBedAssignSubmission(v store.BedAssignSubmission) (bedAssignSubmissionDTO, error) {
	var zero bedAssignSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid bed assign submission")
	}
	a := v.Request.Assign
	canonical, err := domain.NewBedAssign(a.Pawn(), a.Bed(), a.PreviousBed())
	if err != nil || canonical != a {
		return zero, errors.New("invalid bed assign")
	}
	previousBed := a.PreviousBed().ID()
	return bedAssignSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), BedAssign{a.Pawn(), a.Bed(), previousBed}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitBedAssign(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeBedAssignSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid bed assign submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitBedAssign(ctx, q)
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
	dto, err := projectBedAssignSubmission(v)
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
func (s *Server) lookupBedAssign(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupBedAssignSubmission(ctx, id)
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
	dto, err := projectBedAssignSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
