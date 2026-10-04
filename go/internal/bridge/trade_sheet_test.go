package bridge

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func tradeSheetLine(id, def string, colony, trader int64) *o.TradeLine {
	return &o.TradeLine{
		LineId: proto.String(id), Definition: &o.DefinitionRef{DefName: proto.String(def)},
		ColonyCount: proto.Int64(colony), TraderCount: proto.Int64(trader),
		BuyPrice: proto.Float64(10), SellPrice: proto.Float64(5),
		TraderWillTrade: proto.Bool(true), Currency: proto.Bool(false),
		Pawn: proto.Bool(false), ProtectedExport: proto.Bool(false),
	}
}

func tradeSheetFixture(lines []*o.TradeLine) *o.TradeSheet {
	return &o.TradeSheet{
		Snapshot:   &o.SnapshotRef{Context: pbContext(), Token: proto.String("sheet-token")},
		SessionId:  proto.String("session-1"),
		Trader:     &c.Ref{Id: proto.String("settlement-1")},
		Negotiator: &c.Ref{Id: proto.String("pawn-1")},
		GiftMode:   proto.Bool(false), CanTradeNow: proto.Bool(true),
		Balance: proto.Float64(-40), ColonyCanAfford: proto.Bool(true), TraderHasEnoughSilver: proto.Bool(true),
		DealSignature: proto.String("deal-1"),
		Lines:         lines,
	}
}

// tradeSheetClient serves the one sheet and records the requests it was
// asked for.
func tradeSheetClient(t *testing.T, sheet *o.TradeSheet) (*Client, *[]*o.TradeSheetRequest) {
	t.Helper()
	seen := &[]*o.TradeSheetRequest{}
	return testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
		if arg.Tool != "rimgovernor/observations_read_trade_sheet" {
			t.Fatal(arg.Tool)
		}
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		q := &o.TradeSheetRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
			t.Fatal(err)
		}
		*seen = append(*seen, q)
		return pbResult(&o.TradeSheetReply{Outcome: &o.TradeSheetReply_Observed{Observed: sheet}}), nil
	}}, time.Second), seen
}

func TestReadTradeSheetReadsTheCompleteSheetOnce(t *testing.T) {
	sheet := tradeSheetFixture([]*o.TradeLine{tradeSheetLine("line-1", "Steel", 100, 200), tradeSheetLine("line-2", "Gold", 0, 5), tradeSheetLine("line-3", "Silver", 900, 400)})
	client, seen := tradeSheetClient(t, sheet)

	out, raw, err := client.ReadTradeSheet(context.Background(), pbIdentity())
	if err != nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	if len(*seen) != 1 {
		t.Fatalf("made %d requests, want 1", len(*seen))
	}
	if q := (*seen)[0]; !q.GetIncludeUntradeable() || q.GetOnlyChanged() {
		t.Fatalf("request asked for a narrowed sheet: %v", q)
	}
	if len(out.Rows) != 3 || out.Rows[0].LineID != "line-1" || out.Rows[2].DefName != "Silver" {
		t.Fatalf("rows %+v, want all three rows in order", out.Rows)
	}
	if out.SessionID != "session-1" || out.DealSignature != "deal-1" ||
		out.Trader != "settlement-1" || out.Negotiator != "pawn-1" || !out.CanTradeNow ||
		out.Balance != -40 || !out.BalanceKnown || !out.ColonyCanAfford || !out.TraderHasSilver {
		t.Fatalf("header %+v differs from the sheet", out)
	}
	row := out.Rows[0]
	if !row.BuyPriceKnown || !row.SellPriceKnown || !row.TraderWillTradeKnown || !row.CurrencyKnown || !row.PawnKnown || !row.ProtectedExportKnown ||
		!row.TraderWillTrade || row.Currency || row.Pawn || row.ProtectedExport || row.ColonyCount != 100 || row.TraderCount != 200 {
		t.Fatalf("row %+v did not carry its native evidence", row)
	}
}

func TestReadTradeSheetFavorCurrencyAndSilverSheets(t *testing.T) {
	silver, _, err := func() (TradeSheetRead, Result, error) {
		client, _ := tradeSheetClient(t, tradeSheetFixture([]*o.TradeLine{tradeSheetLine("line-1", "Steel", 1, 0)}))
		return client.ReadTradeSheet(context.Background(), pbIdentity())
	}()
	if err != nil || silver.FavorCurrency || silver.Rows[0].Favor {
		t.Fatalf("a sheet without a currency kind must read as silver: %+v %v", silver, err)
	}

	favorRow := tradeSheetLine("line-2", "", 0, 99999)
	favorRow.Currency, favorRow.Favor, favorRow.BuyPrice, favorRow.SellPrice = proto.Bool(true), proto.Bool(true), nil, nil
	favorRow.TransferCount = proto.Int64(6)
	sheet := tradeSheetFixture([]*o.TradeLine{tradeSheetLine("line-1", "Steel", 1, 0), favorRow})
	sheet.CurrencyKind = o.TradeCurrencyKind_TRADE_CURRENCY_KIND_FAVOR.Enum()
	client, _ := tradeSheetClient(t, sheet)
	out, _, err := client.ReadTradeSheet(context.Background(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if !out.FavorCurrency || out.Rows[0].Favor || !out.Rows[1].Favor || !out.Rows[1].Currency || out.Rows[1].DefName != "" || out.Rows[1].TransferNow != 6 || out.Rows[0].SellPrice != 5 {
		t.Fatalf("favor sheet %+v", out)
	}

	// An empty definition is only the favor currency row's.
	bad := tradeSheetLine("line-3", "", 0, 0)
	client, _ = tradeSheetClient(t, tradeSheetFixture([]*o.TradeLine{bad}))
	if _, _, err := client.ReadTradeSheet(context.Background(), pbIdentity()); err == nil {
		t.Fatal("an empty definition on an ordinary row must be refused")
	}
}

func TestReadTradeSheetFavorPrisonerRowFacts(t *testing.T) {
	prisoner := tradeSheetLine("line-1", "Human", 1, 0)
	prisoner.Pawn, prisoner.PawnId, prisoner.SellPrice = proto.Bool(true), proto.String("Thing_Human1"), proto.Float64(3)
	prisoner.GuestStatus, prisoner.PrisonerSecure, prisoner.PawnDowned = proto.String("Prisoner"), proto.Bool(true), proto.Bool(false)
	prisoner.ExtraHomeFaction = &c.Ref{Id: proto.String("Faction_7")}
	prisoner.ExtraHostFaction = &c.Ref{Id: proto.String("Faction_9")}
	sheet := tradeSheetFixture([]*o.TradeLine{prisoner, tradeSheetLine("line-2", "Steel", 1, 0)})
	sheet.CurrencyKind = o.TradeCurrencyKind_TRADE_CURRENCY_KIND_FAVOR.Enum()
	client, _ := tradeSheetClient(t, sheet)
	out, _, err := client.ReadTradeSheet(context.Background(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	row := out.Rows[0]
	if !row.Pawn || row.ProtectedExport || row.SellPrice != 3 || row.GuestStatus != "Prisoner" || !row.PrisonerSecure || !row.PrisonerSecureKnown ||
		row.PawnDowned || !row.PawnDownedKnown || row.ExtraHomeFaction != "Faction_7" || row.ExtraHostFaction != "Faction_9" {
		t.Fatalf("prisoner row %+v", row)
	}
	if steel := out.Rows[1]; steel.GuestStatus != "" || steel.PrisonerSecureKnown || steel.ExtraHomeFaction != "" {
		t.Fatalf("an ordinary row carries prisoner facts: %+v", steel)
	}

	steel := tradeSheetLine("line-3", "Steel", 1, 0)
	steel.PrisonerSecure = proto.Bool(true)
	client, _ = tradeSheetClient(t, tradeSheetFixture([]*o.TradeLine{steel}))
	if _, _, err := client.ReadTradeSheet(context.Background(), pbIdentity()); err == nil {
		t.Fatal("prisoner facts on a non-pawn row must be refused")
	}
}

func TestReadTradeSheetCarriesAbsentFieldsAsUnknown(t *testing.T) {
	line := tradeSheetLine("line-1", "Steel", 0, 0)
	line.BuyPrice, line.SellPrice, line.TraderWillTrade, line.Currency, line.Pawn, line.ProtectedExport = nil, nil, nil, nil, nil, nil
	sheet := tradeSheetFixture([]*o.TradeLine{line})
	sheet.Balance = nil
	client, _ := tradeSheetClient(t, sheet)

	out, _, err := client.ReadTradeSheet(context.Background(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if out.BalanceKnown {
		t.Fatal("an absent balance must not read as zero")
	}
	row := out.Rows[0]
	if row.BuyPriceKnown || row.SellPriceKnown || row.TraderWillTradeKnown || row.CurrencyKnown || row.PawnKnown || row.ProtectedExportKnown {
		t.Fatalf("row %+v read absent native fields as known values", row)
	}
}

func TestReadTradeSheetRefusesIncompleteSheets(t *testing.T) {
	for name, edit := range map[string]func(*o.TradeSheet){
		"other world":           func(v *o.TradeSheet) { v.Snapshot.Context.Identity.LoadToken = proto.String("other") },
		"duplicate line id":     func(v *o.TradeSheet) { v.Lines = append(v.Lines, v.Lines[0]) },
		"negative colony count": func(v *o.TradeSheet) { v.Lines[0].ColonyCount = proto.Int64(-1) },
		"missing definition":    func(v *o.TradeSheet) { v.Lines[0].Definition = nil },
		"missing line id":       func(v *o.TradeSheet) { v.Lines[0].LineId = nil },
	} {
		t.Run(name, func(t *testing.T) {
			sheet := tradeSheetFixture([]*o.TradeLine{tradeSheetLine("line-1", "Steel", 100, 200)})
			edit(sheet)
			client, _ := tradeSheetClient(t, sheet)
			if out, _, err := client.ReadTradeSheet(context.Background(), pbIdentity()); err == nil {
				t.Fatalf("got %+v, want a refusal rather than a partial sheet", out)
			}
		})
	}
}

func TestReadTradeSheetRejectsInvalidInputs(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
		t.Fatal("invalid request dispatched")
		return nil, nil
	}}, time.Second)
	if _, _, err := client.ReadTradeSheet(context.Background(), nil); err == nil {
		t.Fatal("expected rejection of a missing identity")
	}
}
