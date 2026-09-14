package bridge

import (
	"context"
	"encoding/json"
	"fmt"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
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

func tradeSheetFixture(lines []*o.TradeLine, complete bool, next string) *o.TradeSheet {
	page := &c.PageInfo{Complete: proto.Bool(complete)}
	if next != "" {
		page.NextCursor = proto.String(next)
	}
	return &o.TradeSheet{
		Snapshot:   &o.SnapshotRef{Context: pbContext(), Token: proto.String("sheet-token")},
		SessionId:  proto.String("session-1"),
		Trader:     &o.EntityRef{Id: proto.String("settlement-1")},
		Negotiator: &o.EntityRef{Id: proto.String("pawn-1")},
		GiftMode:   proto.Bool(false), CanTradeNow: proto.Bool(true),
		Balance: proto.Float64(-40), ColonyCanAfford: proto.Bool(true), TraderHasEnoughSilver: proto.Bool(true),
		DealSignature: proto.String("deal-1"),
		Lines:         lines,
		Completeness: &o.Completeness{
			Page: page, Matched: proto.Uint64(uint64(len(lines))), Returned: proto.Uint64(uint64(len(lines))),
			Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0),
		},
	}
}

// tradeSheetClient serves one reply per call, in order, and records the
// requests it was asked for.
func tradeSheetClient(t *testing.T, pages []*o.TradeSheet) (*Client, *[]*o.TradeSheetRequest) {
	t.Helper()
	seen := &[]*o.TradeSheetRequest{}
	return testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
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
		if len(*seen) > len(pages) {
			t.Fatalf("native asked for page %d of %d", len(*seen), len(pages))
		}
		return pbResult(&o.TradeSheetReply{Outcome: &o.TradeSheetReply_Observed{Observed: pages[len(*seen)-1]}}), nil
	}}, time.Second), seen
}

func TestReadTradeSheetFollowsPaginationToCompletion(t *testing.T) {
	first := tradeSheetFixture([]*o.TradeLine{tradeSheetLine("line-1", "Steel", 100, 200), tradeSheetLine("line-2", "Gold", 0, 5)}, false, "cursor-2")
	second := tradeSheetFixture([]*o.TradeLine{tradeSheetLine("line-3", "Silver", 900, 400)}, true, "")
	client, seen := tradeSheetClient(t, []*o.TradeSheet{first, second})

	out, raw, err := client.ReadTradeSheet(context.Background(), pbIdentity(), "session-1")
	if err != nil || len(raw.Envelope) == 0 {
		t.Fatal(err)
	}
	if len(*seen) != 2 {
		t.Fatalf("made %d requests, want 2: the cursor must be followed", len(*seen))
	}
	// Every page must ask for the complete unfiltered sheet, and only the
	// second may carry a cursor.
	for i, q := range *seen {
		if !q.GetIncludeUntradeable() || q.GetOnlyChanged() || q.GetSessionId() != "session-1" || q.Page.GetLimit() != tradeSheetPageLimit {
			t.Fatalf("request %d asked for a narrowed sheet: %v", i, q)
		}
	}
	if (*seen)[0].Page.GetCursor() != "" || (*seen)[1].Page.GetCursor() != "cursor-2" {
		t.Fatalf("cursors %q/%q, want ''/'cursor-2'", (*seen)[0].Page.GetCursor(), (*seen)[1].Page.GetCursor())
	}
	if len(out.Rows) != 3 || out.Rows[0].LineID != "line-1" || out.Rows[2].DefName != "Silver" {
		t.Fatalf("rows %+v, want all three pages' rows in order", out.Rows)
	}
	if out.SessionID != "session-1" || out.SessionToken != "sheet-token" || out.DealSignature != "deal-1" ||
		out.Trader != "settlement-1" || out.Negotiator != "pawn-1" || !out.CanTradeNow ||
		out.Balance != -40 || !out.BalanceKnown || !out.ColonyCanAfford || !out.TraderHasSilver {
		t.Fatalf("header %+v is not the first page's header", out)
	}
	row := out.Rows[0]
	if !row.BuyPriceKnown || !row.SellPriceKnown || !row.TraderWillTradeKnown || !row.CurrencyKnown || !row.PawnKnown || !row.ProtectedExportKnown ||
		!row.TraderWillTrade || row.Currency || row.Pawn || row.ProtectedExport || row.ColonyCount != 100 || row.TraderCount != 200 {
		t.Fatalf("row %+v did not carry its native evidence", row)
	}
}

func TestReadTradeSheetCarriesAbsentFieldsAsUnknown(t *testing.T) {
	line := tradeSheetLine("line-1", "Steel", 0, 0)
	line.BuyPrice, line.SellPrice, line.TraderWillTrade, line.Currency, line.Pawn, line.ProtectedExport = nil, nil, nil, nil, nil, nil
	sheet := tradeSheetFixture([]*o.TradeLine{line}, true, "")
	sheet.Balance = nil
	client, _ := tradeSheetClient(t, []*o.TradeSheet{sheet})

	out, _, err := client.ReadTradeSheet(context.Background(), pbIdentity(), "session-1")
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
		"filtered rows":         func(v *o.TradeSheet) { v.Completeness.Filtered = proto.Uint64(3) },
		"filtered unknown":      func(v *o.TradeSheet) { v.Completeness.Filtered = nil },
		"unreadable rows":       func(v *o.TradeSheet) { v.Completeness.Unreadable = proto.Uint64(1) },
		"unreadable unknown":    func(v *o.TradeSheet) { v.Completeness.Unreadable = nil },
		"returned disagrees":    func(v *o.TradeSheet) { v.Completeness.Returned = proto.Uint64(9) },
		"completeness missing":  func(v *o.TradeSheet) { v.Completeness = nil },
		"page missing":          func(v *o.TradeSheet) { v.Completeness.Page = nil },
		"not complete, no next": func(v *o.TradeSheet) { v.Completeness.Page.Complete = proto.Bool(false) },
		"other session":         func(v *o.TradeSheet) { v.SessionId = proto.String("session-2") },
		"missing token":         func(v *o.TradeSheet) { v.Snapshot.Token = nil },
		"other world":           func(v *o.TradeSheet) { v.Snapshot.Context.Identity.LoadToken = proto.String("other") },
		"duplicate line id":     func(v *o.TradeSheet) { v.Lines = append(v.Lines, v.Lines[0]) },
		"negative colony count": func(v *o.TradeSheet) { v.Lines[0].ColonyCount = proto.Int64(-1) },
		"missing definition":    func(v *o.TradeSheet) { v.Lines[0].Definition = nil },
		"missing line id":       func(v *o.TradeSheet) { v.Lines[0].LineId = nil },
	} {
		t.Run(name, func(t *testing.T) {
			sheet := tradeSheetFixture([]*o.TradeLine{tradeSheetLine("line-1", "Steel", 100, 200)}, true, "")
			edit(sheet)
			client, _ := tradeSheetClient(t, []*o.TradeSheet{sheet})
			if out, _, err := client.ReadTradeSheet(context.Background(), pbIdentity(), "session-1"); err == nil {
				t.Fatalf("got %+v, want a refusal rather than a partial sheet", out)
			}
		})
	}
}

// TestReadTradeSheetRefusesSheetsThatMoveMidPagination guards the one thing
// pagination can silently get wrong: stitching two different economic pictures
// into one sheet.
func TestReadTradeSheetRefusesSheetsThatMoveMidPagination(t *testing.T) {
	for name, edit := range map[string]func(*o.TradeSheet){
		"deal signature": func(v *o.TradeSheet) { v.DealSignature = proto.String("deal-2") },
		"session token":  func(v *o.TradeSheet) { v.Snapshot.Token = proto.String("other-token") },
		"trader":         func(v *o.TradeSheet) { v.Trader = &o.EntityRef{Id: proto.String("settlement-2")} },
		"negotiator":     func(v *o.TradeSheet) { v.Negotiator = &o.EntityRef{Id: proto.String("pawn-2")} },
	} {
		t.Run(name, func(t *testing.T) {
			first := tradeSheetFixture([]*o.TradeLine{tradeSheetLine("line-1", "Steel", 100, 200)}, false, "cursor-2")
			second := tradeSheetFixture([]*o.TradeLine{tradeSheetLine("line-2", "Gold", 1, 1)}, true, "")
			edit(second)
			client, _ := tradeSheetClient(t, []*o.TradeSheet{first, second})
			if out, _, err := client.ReadTradeSheet(context.Background(), pbIdentity(), "session-1"); err == nil {
				t.Fatalf("got %+v, want a refusal when the sheet moved mid-pagination", out)
			}
		})
	}
}

func TestReadTradeSheetRefusesUnboundedPagination(t *testing.T) {
	// Native that never stops advancing its cursor must be cut off rather than
	// read forever.
	pages := make([]*o.TradeSheet, 0, tradeSheetMaximumPages)
	for i := range tradeSheetMaximumPages {
		pages = append(pages, tradeSheetFixture([]*o.TradeLine{tradeSheetLine(fmt.Sprintf("line-%d", i), "Steel", 1, 1)}, false, "cursor-next"))
	}
	client, seen := tradeSheetClient(t, pages)
	if _, _, err := client.ReadTradeSheet(context.Background(), pbIdentity(), "session-1"); err == nil {
		t.Fatal("expected a refusal once the pagination bound is exceeded")
	}
	if len(*seen) != tradeSheetMaximumPages {
		t.Fatalf("made %d requests, want exactly the %d-page bound", len(*seen), tradeSheetMaximumPages)
	}
}

func TestReadTradeSheetRejectsInvalidInputs(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("invalid request dispatched")
		return nil, nil
	}}, time.Second)
	if _, _, err := client.ReadTradeSheet(context.Background(), pbIdentity(), ""); err == nil {
		t.Fatal("expected rejection of an empty session identity")
	}
	if _, _, err := client.ReadTradeSheet(context.Background(), nil, "session-1"); err == nil {
		t.Fatal("expected rejection of a missing identity")
	}
}
