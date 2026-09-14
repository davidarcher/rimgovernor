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

// TradeEconomy is the negotiation surface this route family fronts. It is an
// optional capability like WorldEvaluation rather than a method on the shared
// player interface, because a controller built without the trade capability
// has no negotiation driver to advance one and should answer 404 rather than
// accept a request nothing will ever move.
type TradeEconomy interface {
	Submit(context.Context, store.TradeEconomySubmissionRequest) (store.TradeNegotiation, bool, error)
	Lookup(context.Context, string) (store.TradeNegotiation, error)
}

type tradeTargetDTO struct {
	Item         string  `json:"item"`
	Stock        int64   `json:"stock,string"`
	MaxBuy       int64   `json:"maxBuy,string"`
	MaxSell      int64   `json:"maxSell,string"`
	MaxBuyPrice  float64 `json:"maxBuyPrice"`
	MinSellPrice float64 `json:"minSellPrice"`
}
type tradeEconomicPolicyDTO struct {
	Targets       []tradeTargetDTO `json:"targets"`
	SilverReserve int64            `json:"silverReserve,string"`
}
type tradeEvidenceDTO struct {
	Item           string `json:"item"`
	Blocker        string `json:"blocker,omitempty"`
	Matched        bool   `json:"matched"`
	EligibleStock  int64  `json:"eligibleStock,string"`
	RetainedTarget int64  `json:"retainedTarget,string"`
	Count          int64  `json:"count,string"`
	ExportCapacity int64  `json:"exportCapacity,string"`
}
type tradeNegotiationDTO struct {
	RequestID      string                 `json:"requestId"`
	Expected       Identity               `json:"expected"`
	Trader         domain.SettlementID    `json:"trader"`
	Negotiator     domain.PawnID          `json:"negotiator"`
	Policy         tradeEconomicPolicyDTO `json:"policy"`
	MaxSilverSpend int64                  `json:"maxSilverSpend,string"`

	Phase   string `json:"phase"`
	Outcome string `json:"outcome,omitempty"`
	Reason  string `json:"reason,omitempty"`

	OpenPlanID      domain.PlanID       `json:"openPlanId"`
	OpenActionID    domain.ActionID     `json:"openActionId"`
	CurrentPlanID   domain.PlanID       `json:"currentPlanId"`
	CurrentActionID domain.ActionID     `json:"currentActionId"`
	Revision        domain.PlanRevision `json:"revision,string"`

	Lines          []TradeLine          `json:"lines"`
	EconomicFloors []TradeEconomicFloor `json:"economicFloors"`
	Evidence       []tradeEvidenceDTO   `json:"evidence"`
	NetSilver      float64              `json:"netSilver"`
}

func tradePolicyIn(dto tradeEconomicPolicyDTO) domain.TradeEconomicPolicy {
	out := domain.TradeEconomicPolicy{SilverReserve: dto.SilverReserve}
	for _, target := range dto.Targets {
		out.Targets = append(out.Targets, domain.TradeTarget{
			Item: target.Item, Stock: target.Stock, MaxBuy: target.MaxBuy, MaxSell: target.MaxSell,
			MaxBuyPrice: target.MaxBuyPrice, MinSellPrice: target.MinSellPrice,
		})
	}
	return out
}
func tradePolicyOut(p domain.TradeEconomicPolicy) tradeEconomicPolicyDTO {
	out := tradeEconomicPolicyDTO{Targets: []tradeTargetDTO{}, SilverReserve: p.SilverReserve}
	for _, target := range p.Targets {
		out.Targets = append(out.Targets, tradeTargetDTO{
			Item: target.Item, Stock: target.Stock, MaxBuy: target.MaxBuy, MaxSell: target.MaxSell,
			MaxBuyPrice: target.MaxBuyPrice, MinSellPrice: target.MinSellPrice,
		})
	}
	return out
}

func decodeTradeEconomySubmission(reader io.Reader) (store.TradeEconomySubmissionRequest, error) {
	var q store.TradeEconomySubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "trader", "negotiator", "policy", "maxSilverSpend")
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
	if err = json.Unmarshal(fields["trader"], &q.Trader); err != nil {
		return q, err
	}
	if err = json.Unmarshal(fields["negotiator"], &q.Negotiator); err != nil {
		return q, err
	}
	var spend struct {
		Value int64 `json:"maxSilverSpend,string"`
	}
	if err = json.Unmarshal(fields["maxSilverSpend"], &spend.Value); err != nil {
		return q, err
	}
	q.MaxSilverSpend = spend.Value
	var dto tradeEconomicPolicyDTO
	if err = json.Unmarshal(fields["policy"], &dto); err != nil {
		return q, err
	}
	q.Policy = tradePolicyIn(dto)
	if err = q.Policy.Validate(); err != nil {
		return q, err
	}
	return q, nil
}

func projectTradeNegotiation(v store.TradeNegotiation) (tradeNegotiationDTO, error) {
	var zero tradeNegotiationDTO
	if buildingRequestID(v.Request.RequestID) != nil || v.Request.World.Validate() != nil ||
		buildingRequestID(string(v.OpenPlan)) != nil || buildingRequestID(string(v.OpenAction)) != nil ||
		buildingRequestID(string(v.CurrentPlan)) != nil || buildingRequestID(string(v.CurrentAction)) != nil || v.Revision == 0 {
		return zero, errors.New("invalid trade negotiation")
	}
	if v.Request.Policy.Validate() != nil {
		return zero, errors.New("invalid trade economic policy")
	}
	out := tradeNegotiationDTO{
		RequestID: v.Request.RequestID, Expected: playerWorldDTO(v.Request.World),
		Trader: v.Request.Trader, Negotiator: v.Request.Negotiator,
		Policy: tradePolicyOut(v.Request.Policy), MaxSilverSpend: v.Request.MaxSilverSpend,
		Phase: string(v.Phase), Outcome: string(v.Outcome), Reason: v.Reason,
		OpenPlanID: v.OpenPlan, OpenActionID: v.OpenAction,
		CurrentPlanID: v.CurrentPlan, CurrentActionID: v.CurrentAction, Revision: v.Revision,
		Lines: []TradeLine{}, EconomicFloors: []TradeEconomicFloor{}, Evidence: []tradeEvidenceDTO{},
		NetSilver: v.NetSilver,
	}
	out.Lines = append(out.Lines, tradeLinesOut(v.Selected)...)
	out.EconomicFloors = append(out.EconomicFloors, tradeFloorsOut(v.Floors)...)
	for _, row := range v.Evidence {
		out.Evidence = append(out.Evidence, tradeEvidenceDTO{
			Item: row.Item, Blocker: row.Blocker, Matched: row.Matched,
			EligibleStock: row.EligibleStock, RetainedTarget: row.RetainedTarget,
			Count: row.Count, ExportCapacity: row.ExportCapacity,
		})
	}
	return out, nil
}

func (s *Server) submitTradeEconomy(w http.ResponseWriter, r *http.Request, ctx context.Context) {
	if s.config.TradeEconomy == nil {
		s.failure(w, r, 404, "not_found", "Economic trade is not enabled")
		return
	}
	q, err := decodeTradeEconomySubmission(r.Body)
	if err != nil {
		s.failure(w, r, 400, "invalid_request", "Invalid trade economy submission")
		return
	}
	if err = ctx.Err(); err != nil {
		s.readFailure(w, r, err)
		return
	}
	v, created, err := s.config.TradeEconomy.Submit(ctx, q)
	if err == nil {
		err = ctx.Err()
	}
	if err != nil {
		status, failure := playerFailure(err)
		s.write(w, r, status, failure)
		return
	}
	if v.Request.RequestID != q.RequestID {
		s.readFailure(w, r, errors.New("mismatched submission"))
		return
	}
	dto, err := projectTradeNegotiation(v)
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

func (s *Server) lookupTradeEconomy(w http.ResponseWriter, r *http.Request, ctx context.Context, id string) {
	if s.config.TradeEconomy == nil {
		s.failure(w, r, 404, "not_found", "Economic trade is not enabled")
		return
	}
	v, err := s.config.TradeEconomy.Lookup(ctx, id)
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
	dto, err := projectTradeNegotiation(v)
	if err != nil {
		s.readFailure(w, r, err)
		return
	}
	s.write(w, r, 200, dto)
}
