package bridge

import (
	"context"
	"errors"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func settlementGiftJourneyFixture() *o.WorldProgressionSnapshot {
	return &o.WorldProgressionSnapshot{
		Context:      pbContext(),
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
		Caravans: []*o.CaravanState{{
			Caravan: &o.EntityRef{Id: proto.String("caravan-1")},
			Tile:    proto.Int32(42), Moving: proto.Bool(false),
			Pawns:     []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("pawn-1")}}, {Pawn: &o.EntityRef{Id: proto.String("pawn-2")}}},
			Inventory: []*o.Quantity{{DefName: proto.String("Silver"), Units: proto.Int64(500)}},
		}},
	}
}

func settlementGiftWorldFixture() *o.WorldSnapshot {
	return &o.WorldSnapshot{
		Context:      pbContext(),
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
		Settlements: []*o.Settlement{{
			Id: proto.String("settlement-1"), Label: proto.String("Outpost"), Tile: proto.Int32(42), Player: proto.Bool(false),
			FactionId: proto.String("faction-1"), FactionDefName: proto.String("Tribe"), Relation: proto.String("Neutral"), Goodwill: proto.Int32(10),
			Snapshot:        &o.SnapshotRef{Context: pbContext(), EntityId: proto.String("settlement-1"), Token: proto.String("settlement-cas")},
			FactionSnapshot: &o.SnapshotRef{Context: pbContext(), EntityId: proto.String("faction-1"), Token: proto.String("faction-cas")},
		}},
	}
}

func TestReadSettlementGiftTargetSelectsAndValidates(t *testing.T) {
	journey, world := settlementGiftJourneyFixture(), settlementGiftWorldFixture()
	server := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		switch arg.Tool {
		case "rimgovernor/observations_read_world_progression":
			return pbResult(&o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: journey}}), nil
		case "rimgovernor/observations_read_world":
			return pbResult(&o.WorldReply{Outcome: &o.WorldReply_Observed{Observed: world}}), nil
		default:
			t.Fatal(arg.Tool)
			return nil, nil
		}
	}}
	client := testClient(t, server, time.Second)
	target, _, err := client.ReadSettlementGiftTarget(context.Background(), pbIdentity(), "caravan-1")
	if err != nil || target.Caravan != "caravan-1" || target.CaravanTile != 42 || target.CaravanMoving ||
		len(target.CrewIDs) != 2 || target.Silver != 500 || target.Settlement != "settlement-1" ||
		target.FactionID != "faction-1" || target.FactionToken != "faction-cas" || target.Relation != "Neutral" ||
		!target.GoodwillKnown || target.Goodwill != 10 || target.Player {
		t.Fatal(target, err)
	}
	if got := caravanToken("caravan-1", 42, false, []string{"pawn-1", "pawn-2"}); got != target.CaravanToken {
		t.Fatal("caravan token mismatch", got, target.CaravanToken)
	}

	missing := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool == "rimgovernor/observations_read_world_progression" {
			return pbResult(&o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: settlementGiftJourneyFixture()}}), nil
		}
		return pbResult(&o.WorldReply{Outcome: &o.WorldReply_Observed{Observed: settlementGiftWorldFixture()}}), nil
	}}
	if _, _, err := testClient(t, missing, time.Second).ReadSettlementGiftTarget(context.Background(), pbIdentity(), "caravan-missing"); !errors.Is(err, ErrUnavailable) {
		t.Fatal("missing caravan accepted", err)
	}

	moving := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool == "rimgovernor/observations_read_world_progression" {
			j := settlementGiftJourneyFixture()
			j.Caravans[0].Moving = proto.Bool(true)
			return pbResult(&o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: j}}), nil
		}
		return pbResult(&o.WorldReply{Outcome: &o.WorldReply_Observed{Observed: settlementGiftWorldFixture()}}), nil
	}}
	if _, _, err := testClient(t, moving, time.Second).ReadSettlementGiftTarget(context.Background(), pbIdentity(), "caravan-1"); !errors.Is(err, ErrUnavailable) {
		t.Fatal("moving caravan accepted", err)
	}

	noSettlement := &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool == "rimgovernor/observations_read_world_progression" {
			return pbResult(&o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: settlementGiftJourneyFixture()}}), nil
		}
		return pbResult(&o.WorldReply{Outcome: &o.WorldReply_Observed{Observed: &o.WorldSnapshot{Context: pbContext(), Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}}}}}), nil
	}}
	if _, _, err := testClient(t, noSettlement, time.Second).ReadSettlementGiftTarget(context.Background(), pbIdentity(), "caravan-1"); !errors.Is(err, ErrUnavailable) {
		t.Fatal("missing settlement accepted", err)
	}

	if _, _, err := testClient(t, server, time.Second).ReadSettlementGiftTarget(context.Background(), pbIdentity(), ""); !errors.Is(err, ErrContract) {
		t.Fatal("invalid settlement gift target identity accepted", err)
	}
}
