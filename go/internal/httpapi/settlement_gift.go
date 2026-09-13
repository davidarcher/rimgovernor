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

type settlementGiftSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Gift      SettlementGift      `json:"gift"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func decodeSettlementGiftSubmission(reader io.Reader) (store.SettlementGiftSubmissionRequest, error) {
	var q store.SettlementGiftSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "gift")
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
	gift, err := buildingFields(fields["gift"], "caravan", "settlement", "faction", "crewIds", "silver")
	if err != nil {
		return q, err
	}
	var caravan domain.CaravanID
	var settlement domain.SettlementID
	var faction domain.FactionID
	var crew []domain.PawnID
	var silver int32
	if err = json.Unmarshal(gift["caravan"], &caravan); err != nil {
		return q, err
	}
	if err = json.Unmarshal(gift["settlement"], &settlement); err != nil {
		return q, err
	}
	if err = json.Unmarshal(gift["faction"], &faction); err != nil {
		return q, err
	}
	if err = json.Unmarshal(gift["crewIds"], &crew); err != nil {
		return q, err
	}
	if err = json.Unmarshal(gift["silver"], &silver); err != nil {
		return q, err
	}
	q.Gift, err = domain.NewSettlementGift(caravan, settlement, faction, crew, silver)
	return q, err
}
func projectSettlementGiftSubmission(v store.SettlementGiftSubmission) (settlementGiftSubmissionDTO, error) {
	var zero settlementGiftSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid settlement gift submission")
	}
	g := v.Request.Gift
	canonical, err := domain.NewSettlementGift(g.Caravan(), g.Settlement(), g.Faction(), g.CrewIDs(), g.Silver())
	if err != nil || canonical != g {
		return zero, errors.New("invalid settlement gift")
	}
	return settlementGiftSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), SettlementGift{g.Caravan(), g.Settlement(), g.Faction(), g.CrewIDs(), g.Silver()}, v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitSettlementGift(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeSettlementGiftSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid settlement gift submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitSettlementGift(ctx, q)
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
	dto, err := projectSettlementGiftSubmission(v)
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
func (s *Server) lookupSettlementGift(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupSettlementGiftSubmission(ctx, id)
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
	dto, err := projectSettlementGiftSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
