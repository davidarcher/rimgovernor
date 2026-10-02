package bridge

import (
	"context"
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
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

	// A pawn row's purchase facts (#1037): the offered pawn's load id,
	// skills with passions, and whether it can do violence. Absent on
	// every other row, and on a pawn native could not read.
	PawnID               string
	Skills               []TradeSheetSkill
	ViolenceCapable      bool
	ViolenceCapableKnown bool

	// A non-pawn row's first colony thing and its quality (#1194): a
	// packed sculpture's row matches its packed item by ThingID.
	ThingID      string
	Quality      int32
	QualityKnown bool
}

// TradeSheetSkill is one skill of an offered pawn.
type TradeSheetSkill struct {
	Name     string
	Level    int32
	Passion  string
	Disabled bool
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

// ReadTradeSheet reads the whole trade sheet of the live session,
// following native's own pagination cursor until the census is complete. It
// always asks for untradeable rows to be included and never applies a name
// filter, so the returned sheet is the complete unfiltered inventory economic
// selection requires; a reply that still reports filtered or unreadable rows
// is refused.
func (client *Client) ReadTradeSheet(ctx context.Context, identity *c.Identity) (TradeSheetRead, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return TradeSheetRead{}, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	var out TradeSheetRead
	seen := map[string]bool{}
	request := &o.TradeSheetRequest{
		Scope:              &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
		IncludeUntradeable: proto.Bool(true),
		OnlyChanged:        proto.Bool(false),
	}
	reply := &o.TradeSheetReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_read_trade_sheet", request, reply)
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
	header, err := tradeSheetPage(sheet, identity, seen, &out.Rows)
	if err != nil {
		return TradeSheetRead{}, raw, err
	}
	out.Context, out.SessionID = header.Context, header.SessionID
	out.Trader, out.Negotiator, out.GiftMode, out.CanTradeNow = header.Trader, header.Negotiator, header.GiftMode, header.CanTradeNow
	out.Balance, out.BalanceKnown = header.Balance, header.BalanceKnown
	out.ColonyCanAfford, out.TraderHasSilver, out.DealSignature = header.ColonyCanAfford, header.TraderHasSilver, header.DealSignature
	return out, raw, ctx.Err()
}

// tradeSheetPage validates the sheet's header and appends its rows.
func tradeSheetPage(v *o.TradeSheet, identity *c.Identity, seen map[string]bool, rows *[]TradeSheetRow) (TradeSheetRead, error) {
	if v == nil || v.Snapshot == nil {
		return TradeSheetRead{}, contract("trade sheet snapshot missing")
	}
	if err := ValidateContext(v.Snapshot.Context); err != nil {
		return TradeSheetRead{}, err
	}
	if !sameIdentity(v.Snapshot.Context.Identity, identity) || validID(v.GetSessionId()) != nil {
		return TradeSheetRead{}, contract("trade sheet world or token mismatch")
	}
	if !diagnostic(v.DealSignature) {
		return TradeSheetRead{}, contract("trade sheet deal signature invalid")
	}
	header := TradeSheetRead{
		Context: v.Snapshot.Context, SessionID: v.GetSessionId(),
		Trader: v.GetTrader().GetId(), Negotiator: v.GetNegotiator().GetId(), GiftMode: v.GetGiftMode(),
		CanTradeNow: v.GetCanTradeNow(), Balance: v.GetBalance(), BalanceKnown: v.Balance != nil,
		ColonyCanAfford: v.GetColonyCanAfford(), TraderHasSilver: v.GetTraderHasEnoughSilver(),
		DealSignature: v.GetDealSignature(),
	}
	for _, line := range v.Lines {
		row, err := tradeSheetRow(line)
		if err != nil {
			return TradeSheetRead{}, err
		}
		if seen[row.LineID] {
			return TradeSheetRead{}, contract("duplicate trade sheet line id")
		}
		seen[row.LineID] = true
		*rows = append(*rows, row)
	}
	return header, nil
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
	if !v.GetPawn() && (len(v.Skills) != 0 || v.ViolenceCapable != nil || v.GetPawnId() != "") {
		return TradeSheetRow{}, contract("pawn facts on a non-pawn trade line")
	}
	if v.GetPawn() && (v.ThingId != nil || v.Quality != nil) || !diagnostic(v.ThingId) || v.Quality != nil && !validQuality(v.GetQuality()) {
		return TradeSheetRow{}, contract("invalid trade sheet thing facts")
	}
	if !diagnostic(v.PawnId) {
		return TradeSheetRow{}, contract("trade sheet pawn id invalid")
	}
	var skills []TradeSheetSkill
	for _, skill := range v.Skills {
		if skill.GetDefinition() == nil || validID(skill.GetDefinition().GetDefName()) != nil || skill.Level == nil || skill.Passion != nil && PassionName(skill.GetPassion()) == "" {
			return TradeSheetRow{}, contract("invalid trade sheet pawn skill")
		}
		skills = append(skills, TradeSheetSkill{Name: skill.GetDefinition().GetDefName(), Level: skill.GetLevel(), Passion: PassionName(skill.GetPassion()), Disabled: skill.GetDisabled()})
	}
	return TradeSheetRow{
		PawnID: v.GetPawnId(), Skills: skills, ViolenceCapable: v.GetViolenceCapable(), ViolenceCapableKnown: v.ViolenceCapable != nil,
		Food: food, ThingID: v.GetThingId(), Quality: v.GetQuality(), QualityKnown: v.Quality != nil,
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
