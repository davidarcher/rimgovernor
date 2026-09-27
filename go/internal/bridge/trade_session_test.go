package bridge

import (
	"context"
	"testing"
	"time"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func tradeSessionClient(t *testing.T, session *o.TradeSession) *Client {
	t.Helper()
	return testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != tradeSessionTool {
			t.Fatal(arg.Tool)
		}
		return pbResult(&o.TradeSessionReply{Outcome: &o.TradeSessionReply_Observed{Observed: session}}), nil
	}}, time.Second)
}

func TestReadTradeSessionReadsPairOrNone(t *testing.T) {
	for _, c := range []struct {
		name    string
		session *o.TradeSession
		want    TradeSessionRead
		bad     bool
	}{
		{"none", &o.TradeSession{Context: pbContext()}, TradeSessionRead{}, false},
		{"walk", &o.TradeSession{Context: pbContext(), TraderId: proto.String("trader-1"), NegotiatorId: proto.String("pawn-1"), Open: proto.Bool(false)}, TradeSessionRead{Trader: "trader-1", Negotiator: "pawn-1"}, false},
		{"open", &o.TradeSession{Context: pbContext(), TraderId: proto.String("trader-1"), NegotiatorId: proto.String("pawn-1"), Open: proto.Bool(true)}, TradeSessionRead{Trader: "trader-1", Negotiator: "pawn-1", Open: true}, false},
		{"half pair", &o.TradeSession{Context: pbContext(), TraderId: proto.String("trader-1")}, TradeSessionRead{}, true},
		{"open without pair", &o.TradeSession{Context: pbContext(), Open: proto.Bool(true)}, TradeSessionRead{}, true},
	} {
		got, _, err := tradeSessionClient(t, c.session).ReadTradeSession(context.Background(), pbIdentity())
		if (err != nil) != c.bad {
			t.Fatalf("%s: %v", c.name, err)
		}
		if c.bad {
			continue
		}
		if got.Trader != c.want.Trader || got.Negotiator != c.want.Negotiator || got.Open != c.want.Open || got.Context == nil {
			t.Fatalf("%s: %+v", c.name, got)
		}
	}
}
