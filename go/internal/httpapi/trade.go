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

type tradeSubmissionDTO struct {
	RequestID string              `json:"requestId"`
	Expected  Identity            `json:"expected"`
	Trade     Trade               `json:"trade"`
	PlanID    domain.PlanID       `json:"planId"`
	ActionID  domain.ActionID     `json:"actionId"`
	Revision  domain.PlanRevision `json:"revision,string"`
}

func tradeLinesIn(lines []TradeLine) []domain.TradeLine {
	if lines == nil {
		return nil
	}
	out := make([]domain.TradeLine, len(lines))
	for i, l := range lines {
		out[i] = domain.TradeLine{LineID: l.LineID, AbsoluteCount: l.AbsoluteCount}
	}
	return out
}
func tradeLinesOut(lines []domain.TradeLine) []TradeLine {
	if lines == nil {
		return nil
	}
	out := make([]TradeLine, len(lines))
	for i, l := range lines {
		out[i] = TradeLine{LineID: l.LineID, AbsoluteCount: l.AbsoluteCount}
	}
	return out
}
func tradeFloorsIn(floors []TradeEconomicFloor) []domain.TradeEconomicFloor {
	if floors == nil {
		return nil
	}
	out := make([]domain.TradeEconomicFloor, len(floors))
	for i, f := range floors {
		out[i] = domain.TradeEconomicFloor{DefName: f.DefName, Count: f.Count}
	}
	return out
}
func tradeFloorsOut(floors []domain.TradeEconomicFloor) []TradeEconomicFloor {
	if floors == nil {
		return nil
	}
	out := make([]TradeEconomicFloor, len(floors))
	for i, f := range floors {
		out[i] = TradeEconomicFloor{DefName: f.DefName, Count: f.Count}
	}
	return out
}

// tradeDecode builds the domain.Trade the selected Kind requires. Unlike
// most other submission decoders, Trade's wire shape is a tagged union
// (kind selects which fields are meaningful) rather than one fixed field
// set, so this bypasses buildingFields' exact-key-set check and instead
// leans on domain.newTrade's own per-kind field-presence validation via
// the appropriate NewTradeX constructor.
func tradeDecode(dto Trade) (domain.Trade, error) {
	switch dto.Kind {
	case domain.TradeOpen:
		return domain.NewTradeOpen(dto.Trader, dto.Negotiator, dto.GiftMode)
	case domain.TradeSetLines:
		return domain.NewTradeSetLines(tradeLinesIn(dto.Lines), dto.AllowPawns)
	case domain.TradeAccept:
		return domain.NewTradeAccept(dto.ExpectedDealSignature, tradeFloorsIn(dto.EconomicFloors), dto.AllowEmpty, dto.ReceiveQuest)
	case domain.TradeEnd:
		return domain.NewTradeEnd(dto.EndKind, dto.ReceiveQuest)
	default:
		return domain.Trade{}, errors.New("invalid trade kind")
	}
}
func tradeEncode(t domain.Trade) Trade {
	return Trade{
		Kind: t.Kind(), Trader: t.Trader(), Negotiator: t.Negotiator(), GiftMode: t.GiftMode(),
		Lines: tradeLinesOut(t.Lines()), AllowPawns: t.AllowPawns(), ExpectedDealSignature: t.ExpectedDealSignature(),
		EconomicFloors: tradeFloorsOut(t.EconomicFloors()), AllowEmpty: t.AllowEmpty(), EndKind: t.EndKind(), ReceiveQuest: t.ReceiveQuest(),
	}
}

func decodeTradeSubmission(reader io.Reader) (store.TradeSubmissionRequest, error) {
	var q store.TradeSubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "trade")
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
	var dto Trade
	if err = json.Unmarshal(fields["trade"], &dto); err != nil {
		return q, err
	}
	q.Trade, err = tradeDecode(dto)
	return q, err
}
func projectTradeSubmission(v store.TradeSubmission) (tradeSubmissionDTO, error) {
	var zero tradeSubmissionDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil || buildingRequestID(string(v.Plan)) != nil || buildingRequestID(string(v.Action)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid trade submission")
	}
	canonical, err := tradeDecode(tradeEncode(v.Request.Trade))
	if err != nil || canonical != v.Request.Trade {
		return zero, errors.New("invalid trade")
	}
	return tradeSubmissionDTO{v.Request.RequestID, playerWorldDTO(v.Request.World), tradeEncode(v.Request.Trade), v.Plan, v.Action, v.Revision}, nil
}
func (s *Server) submitTrade(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	q, err := decodeTradeSubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid trade submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.player.SubmitTrade(ctx, q)
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
	dto, err := projectTradeSubmission(v)
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
func (s *Server) lookupTrade(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	v, err := s.controls.LookupTradeSubmission(ctx, id)
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
	dto, err := projectTradeSubmission(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
