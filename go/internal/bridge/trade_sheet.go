package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// tradeSheetPageLimit is the per-request row cap ReadTradeSheet asks for, and
// tradeSheetMaximumPages bounds how many such pages it will follow before
// refusing. A real trader's inventory is a few hundred rows; these bounds keep
// one read finite without ever silently returning a partial sheet, which
// economic selection must never act on (see policy.SelectTrade).
const (
	tradeSheetPageLimit    = 512
	tradeSheetMaximumPages = 16
	tradeSheetMaximumRows  = tradeSheetPageLimit * tradeSheetMaximumPages
)

// TradeSheetRow is one validated trade sheet line: the native line id
// SetTradeLines addresses, the definition it trades, both sides' current
// counts, both prices and the eligibility flags economic selection reads. It
// is the Go form of the row shape trade_policy.py's select_trade inspects
// (defName/colonyCount/traderCount/buyPrice/sellPrice/traderWillTrade/isPawn/
// isCurrency/protectedExport), with each optional proto field's presence
// carried explicitly so an absent field is never read as a zero.
type TradeSheetRow struct {
	Food         *o.TradeFoodFacts
	LineID       string
	DefName      string
	Stuff        string
	ColonyCount  int64
	TraderCount  int64
	BuyPrice     float64
	SellPrice    float64
	MarketValue  float64
	TransferNow  int64
	MinimumCount int64
	MaximumCount int64

	BuyPriceKnown        bool
	SellPriceKnown       bool
	TraderWillTrade      bool
	TraderWillTradeKnown bool
	Currency             bool
	CurrencyKnown        bool
	Pawn                 bool
	PawnKnown            bool
	ProtectedExport      bool
	ProtectedExportKnown bool
}

// TradeSheetRead is one complete, unfiltered read of the currently open
// session's trade sheet. Completeness is a precondition, not a report: this
// read returns an error rather than a partial sheet whenever native filtered,
// truncated or failed to read any row, because an incomplete inventory view is
// unusable for an economic decision -- exactly the guard
// trade_policy.py's select_trade makes with its
// omittedByRowCap/omittedByFilter check.
type TradeSheetRead struct {
	Context         *c.ObservationContext
	SessionID       string
	SessionToken    string
	Trader          string
	Negotiator      string
	GiftMode        bool
	CanTradeNow     bool
	Balance         float64
	BalanceKnown    bool
	ColonyCanAfford bool
	TraderHasSilver bool
	DealSignature   string
	Rows            []TradeSheetRow
}

// ReadTradeSheet reads the whole trade sheet of one already-open session,
// following native's own pagination cursor until the census is complete. It
// always asks for untradeable rows to be included and never applies a name
// filter, so the returned sheet is the complete unfiltered inventory economic
// selection requires; a reply that still reports filtered or unreadable rows
// is refused.
func (client *Client) ReadTradeSheet(ctx context.Context, identity *c.Identity, session string) (TradeSheetRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return TradeSheetRead{}, Result{}, err
	}
	if validID(session) != nil {
		return TradeSheetRead{}, Result{}, contract("invalid trade sheet session identity")
	}
	identity = proto.Clone(identity).(*c.Identity)
	var out TradeSheetRead
	seen := map[string]bool{}
	cursor := ""
	var raw Result
	for page := 0; page < tradeSheetMaximumPages; page++ {
		request := &o.TradeSheetRequest{
			Scope:              &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
			SessionId:          proto.String(session),
			IncludeUntradeable: proto.Bool(true),
			OnlyChanged:        proto.Bool(false),
			Page:               &c.PageRequest{Limit: proto.Uint32(tradeSheetPageLimit)},
		}
		if cursor != "" {
			request.Page.Cursor = proto.String(cursor)
		}
		reply := &o.TradeSheetReply{}
		var err error
		raw, err = client.protoRead(ctx, "rimgovernor/observations_read_trade_sheet", request, reply)
		if err != nil {
			return TradeSheetRead{}, raw, err
		}
		if err = buildingUnknown(reply); err != nil {
			return TradeSheetRead{}, raw, err
		}
		var sheet *o.TradeSheet
		switch v := reply.Outcome.(type) {
		case *o.TradeSheetReply_Failure:
			return TradeSheetRead{}, raw, failure(v.Failure, raw)
		case *o.TradeSheetReply_Unavailable:
			return TradeSheetRead{}, raw, unavailable(v.Unavailable, raw)
		case *o.TradeSheetReply_Observed:
			sheet = v.Observed
		default:
			return TradeSheetRead{}, raw, contract("trade sheet outcome missing")
		}
		header, next, err := tradeSheetPage(sheet, identity, session, seen, &out.Rows)
		if err != nil {
			return TradeSheetRead{}, raw, err
		}
		if page == 0 {
			out.Context, out.SessionID, out.SessionToken = header.Context, header.SessionID, header.SessionToken
			out.Trader, out.Negotiator, out.GiftMode, out.CanTradeNow = header.Trader, header.Negotiator, header.GiftMode, header.CanTradeNow
			out.Balance, out.BalanceKnown = header.Balance, header.BalanceKnown
			out.ColonyCanAfford, out.TraderHasSilver, out.DealSignature = header.ColonyCanAfford, header.TraderHasSilver, header.DealSignature
		} else if header.SessionID != out.SessionID || header.SessionToken != out.SessionToken ||
			header.Trader != out.Trader || header.Negotiator != out.Negotiator ||
			header.DealSignature != out.DealSignature || header.Context.GetTick() != out.Context.GetTick() {
			// A sheet that moved mid-pagination is a different economic
			// picture, not a continuation of this one.
			return TradeSheetRead{}, raw, contract("trade sheet changed during pagination")
		}
		if next == "" {
			return out, raw, ctx.Err()
		}
		cursor = next
	}
	return TradeSheetRead{}, raw, contract("trade sheet exceeds pagination bound")
}

// tradeSheetPage validates one page's header and appends its rows. next is the
// cursor to follow, or "" when this page completed the census.
func tradeSheetPage(v *o.TradeSheet, identity *c.Identity, session string, seen map[string]bool, rows *[]TradeSheetRow) (TradeSheetRead, string, error) {
	if v == nil || v.Snapshot == nil {
		return TradeSheetRead{}, "", contract("trade sheet snapshot missing")
	}
	if err := ValidateContext(v.Snapshot.Context); err != nil {
		return TradeSheetRead{}, "", err
	}
	if !sameIdentity(v.Snapshot.Context.Identity, identity) || validID(v.Snapshot.GetToken()) != nil {
		return TradeSheetRead{}, "", contract("trade sheet world or token mismatch")
	}
	if v.GetSessionId() != session {
		return TradeSheetRead{}, "", contract("trade sheet addresses a different session")
	}
	if !diagnostic(v.DealSignature) {
		return TradeSheetRead{}, "", contract("trade sheet deal signature invalid")
	}
	counts := v.Completeness
	if counts == nil || counts.Page == nil || counts.Returned == nil || counts.GetReturned() != uint64(len(v.Lines)) {
		return TradeSheetRead{}, "", contract("trade sheet completeness missing")
	}
	// Python treats any filtered-away or unreadable row as making the whole
	// sheet unusable for an economic decision; so does this.
	if counts.Filtered == nil || counts.GetFiltered() != 0 || counts.Unreadable == nil || counts.GetUnreadable() != 0 {
		return TradeSheetRead{}, "", contract("trade sheet omitted rows")
	}
	next := counts.Page.GetNextCursor()
	if next == "" && !counts.Page.GetComplete() {
		return TradeSheetRead{}, "", contract("incomplete trade sheet page")
	}
	if len(*rows)+len(v.Lines) > tradeSheetMaximumRows {
		return TradeSheetRead{}, "", contract("trade sheet rows exceed bound")
	}
	header := TradeSheetRead{
		Context: v.Snapshot.Context, SessionID: v.GetSessionId(), SessionToken: v.Snapshot.GetToken(),
		Trader: v.GetTrader().GetId(), Negotiator: v.GetNegotiator().GetId(), GiftMode: v.GetGiftMode(),
		CanTradeNow: v.GetCanTradeNow(), Balance: v.GetBalance(), BalanceKnown: v.Balance != nil,
		ColonyCanAfford: v.GetColonyCanAfford(), TraderHasSilver: v.GetTraderHasEnoughSilver(),
		DealSignature: v.GetDealSignature(),
	}
	for _, line := range v.Lines {
		row, err := tradeSheetRow(line)
		if err != nil {
			return TradeSheetRead{}, "", err
		}
		if seen[row.LineID] {
			return TradeSheetRead{}, "", contract("duplicate trade sheet line id")
		}
		seen[row.LineID] = true
		*rows = append(*rows, row)
	}
	return header, next, nil
}

func tradeSheetRow(v *o.TradeLine) (TradeSheetRow, error) {
	if v == nil || validID(v.GetLineId()) != nil || v.Definition == nil || validID(v.Definition.GetDefName()) != nil {
		return TradeSheetRow{}, contract("invalid trade sheet line")
	}
	if !diagnostic(v.Stuff) || !diagnostic(v.Category) {
		return TradeSheetRow{}, contract("trade sheet line text invalid")
	}
	if v.GetColonyCount() < 0 || v.GetTraderCount() < 0 {
		return TradeSheetRow{}, contract("trade sheet line count negative")
	}
	var food *o.TradeFoodFacts
	if f := v.Food; f != nil {
		if math.IsNaN(f.Nutrition) || math.IsInf(f.Nutrition, 0) || f.Nutrition <= 0 ||
			f.IngredientClass < o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_MEAT || f.IngredientClass > o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_ANY ||
			f.Crop && (f.Prepared || f.IngredientClass != o.FoodIngredientClass_FOOD_INGREDIENT_CLASS_VEGETABLE) || v.GetPawn() || v.GetCurrency() {
			return TradeSheetRow{}, contract("invalid trade food classification")
		}
		food = proto.CloneOf(f)
	}
	return TradeSheetRow{
		Food:   food,
		LineID: v.GetLineId(), DefName: v.Definition.GetDefName(), Stuff: v.GetStuff(),
		ColonyCount: v.GetColonyCount(), TraderCount: v.GetTraderCount(),
		BuyPrice: v.GetBuyPrice(), SellPrice: v.GetSellPrice(), MarketValue: v.GetMarketValue(),
		TransferNow: v.GetTransferCount(), MinimumCount: v.GetMinimumCount(), MaximumCount: v.GetMaximumCount(),
		BuyPriceKnown: v.BuyPrice != nil, SellPriceKnown: v.SellPrice != nil,
		TraderWillTrade: v.GetTraderWillTrade(), TraderWillTradeKnown: v.TraderWillTrade != nil,
		Currency: v.GetCurrency(), CurrencyKnown: v.Currency != nil,
		Pawn: v.GetPawn(), PawnKnown: v.Pawn != nil,
		ProtectedExport: v.GetProtectedExport(), ProtectedExportKnown: v.ProtectedExport != nil,
	}, nil
}
