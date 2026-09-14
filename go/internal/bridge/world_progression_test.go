package bridge

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func worldProgressionFixture() *o.WorldProgressionSnapshot {
	return &o.WorldProgressionSnapshot{
		Context:      pbContext(),
		Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}},
		Maps: []*o.WorldMap{
			{Id: proto.Int32(1), Tile: proto.Int32(7), Home: proto.Bool(true), Pawns: []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("pawn-1")}}}},
			{Id: proto.Int32(2), Tile: proto.Int32(9), Home: proto.Bool(false), Pawns: []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("pawn-2")}}}},
		},
		Caravans: []*o.CaravanState{{
			Caravan: &o.EntityRef{Id: proto.String("caravan-1")},
			Tile:    proto.Int32(42), Moving: proto.Bool(true),
			Pawns:      []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("pawn-1")}, Dead: proto.Bool(false), Downed: proto.Bool(false)}},
			FoodDays:   proto.Float64(2.5),
			Inventory:  []*o.Quantity{{DefName: proto.String("Silver"), Units: proto.Int64(50)}, {DefName: proto.String("Steel"), Units: proto.Int64(75)}},
			HomeRoutes: []*o.WorldRoute{{Destination: proto.Int32(7), Reachable: proto.Bool(true), EstimatedTicks: proto.Int64(6000)}},
		}},
		Quests: []*o.QuestState{{
			Id: proto.String("quest-1"), State: proto.String("NotYetAccepted"),
			RequiresAccepter: proto.Bool(true), CanAccept: proto.Bool(true),
			EligiblePawns: []*o.EntityRef{{Id: proto.String("pawn-1")}},
			Rewards:       []*o.QuestReward{{ChoiceIndex: proto.Uint32(0)}},
			TradeRequests: []*o.QuestTradeRequest{{Resource: proto.String("Steel"), Count: proto.Int64(40), Destination: proto.Int32(7)}},
			Snapshot:      &o.SnapshotRef{Context: pbContext(), EntityId: proto.String("quest-1"), Token: proto.String("quest-cas")},
		}},
	}
}
func TestReadWorldProgressionAcceptsValidObservation(t *testing.T) {
	snapshot := worldProgressionFixture()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		if arg.Tool != "rimgovernor/observations_read_world_progression" {
			t.Fatal(arg.Tool)
		}
		var outer struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &outer); err != nil {
			t.Fatal(err)
		}
		q := &o.WorldProgressionRequest{}
		if err := protojson.Unmarshal([]byte(outer.Request), q); err != nil {
			t.Fatal(err)
		}
		if !q.GetIncludeStorage() || q.Page.GetLimit() != 256 {
			t.Fatal(q)
		}
		return pbResult(&o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: snapshot}}), nil
	}}, time.Second)
	out, raw, err := client.ReadWorldProgression(context.Background(), pbIdentity(), true)
	if err != nil || len(raw.Envelope) == 0 || len(out.Caravans) != 1 || out.Caravans[0].ID != "caravan-1" ||
		out.Caravans[0].Tile != 42 || !out.Caravans[0].Moving || len(out.Caravans[0].PawnIDs) != 1 || out.Caravans[0].PawnIDs[0] != "pawn-1" {
		t.Fatal(out, err)
	}
	caravan := out.Caravans[0]
	if !caravan.FoodDaysKnown || caravan.FoodDays != 2.5 || caravan.Silver != 50 || caravan.Inventory["Silver"] != 50 || caravan.Inventory["Steel"] != 75 {
		t.Fatal(caravan)
	}
	if len(caravan.Pawns) != 1 || caravan.Pawns[0].ID != "pawn-1" || !caravan.Pawns[0].DeadKnown || caravan.Pawns[0].Dead ||
		!caravan.Pawns[0].DownedKnown || caravan.Pawns[0].Downed {
		t.Fatal(caravan.Pawns)
	}
	if len(caravan.HomeRoutes) != 1 || caravan.HomeRoutes[0].DestinationMapID != 7 || !caravan.HomeRoutes[0].Reachable ||
		!caravan.HomeRoutes[0].EstimatedTicksKnown || caravan.HomeRoutes[0].EstimatedTicks != 6000 {
		t.Fatal(caravan.HomeRoutes)
	}
	if len(out.Maps) != 2 {
		t.Fatal(out.Maps)
	}
	home, foreign := out.Maps[0], out.Maps[1]
	if home.ID != 1 || home.Tile != 7 || !home.Home || len(home.PawnIDs) != 1 || home.PawnIDs[0] != "pawn-1" {
		t.Fatal(home)
	}
	if foreign.ID != 2 || foreign.Tile != 9 || foreign.Home || len(foreign.PawnIDs) != 1 || foreign.PawnIDs[0] != "pawn-2" {
		t.Fatal(foreign)
	}
	if len(out.Quests) != 1 {
		t.Fatal(out.Quests)
	}
	quest := out.Quests[0]
	if quest.ID != "quest-1" || quest.State != "NotYetAccepted" || !quest.RequiresAccepter || !quest.CanAccept ||
		quest.ChoiceCount != 1 || !quest.HasTradeRequest || len(quest.EligiblePawnIDs) != 1 || quest.EligiblePawnIDs[0] != "pawn-1" ||
		quest.SnapshotToken != "quest-cas" || !quest.TradeDestinationKnown || quest.TradeDestinationTile != 7 {
		t.Fatal(quest)
	}
	if len(quest.TradeRequests) != 1 || quest.TradeRequests[0].Resource != "Steel" || quest.TradeRequests[0].Count != 40 || quest.TradeRequests[0].DestinationTile != 7 {
		t.Fatal(quest.TradeRequests)
	}
}
func TestReadWorldProgressionRejectsInvalidInputs(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("invalid request dispatched")
		return nil, nil
	}}, time.Second)
	if _, _, err := client.ReadWorldProgression(context.Background(), nil, false); err == nil {
		t.Fatal("expected rejection")
	}
}
func TestReadWorldProgressionMalformedEvidence(t *testing.T) {
	edits := map[string]func(*o.WorldProgressionSnapshot){
		"world":               func(v *o.WorldProgressionSnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
		"partial page":        func(v *o.WorldProgressionSnapshot) { v.Completeness.Page.Complete = proto.Bool(false) },
		"missing map id":      func(v *o.WorldProgressionSnapshot) { v.Maps[0].Id = nil },
		"missing map tile":    func(v *o.WorldProgressionSnapshot) { v.Maps[0].Tile = nil },
		"negative map tile":   func(v *o.WorldProgressionSnapshot) { v.Maps[0].Tile = proto.Int32(-1) },
		"missing map home":    func(v *o.WorldProgressionSnapshot) { v.Maps[0].Home = nil },
		"missing map pawn id": func(v *o.WorldProgressionSnapshot) { v.Maps[0].Pawns[0].Pawn.Id = nil },
		"duplicate map pawn":  func(v *o.WorldProgressionSnapshot) { v.Maps[0].Pawns = append(v.Maps[0].Pawns, v.Maps[0].Pawns[0]) },
		"pawn on two maps": func(v *o.WorldProgressionSnapshot) {
			v.Maps[1].Pawns = append(v.Maps[1].Pawns, v.Maps[0].Pawns[0])
		},
		"missing caravan id":  func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Caravan.Id = nil },
		"duplicate caravan":   func(v *o.WorldProgressionSnapshot) { v.Caravans = append(v.Caravans, v.Caravans[0]) },
		"negative caravan tile": func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Tile = proto.Int32(-1) },
		"missing pawn id":     func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Pawns[0].Pawn.Id = nil },
		"duplicate pawn":      func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Pawns = append(v.Caravans[0].Pawns, v.Caravans[0].Pawns[0]) },
		"missing quest id":    func(v *o.WorldProgressionSnapshot) { v.Quests[0].Id = nil },
		"duplicate quest":     func(v *o.WorldProgressionSnapshot) { v.Quests = append(v.Quests, v.Quests[0]) },
		"missing quest state": func(v *o.WorldProgressionSnapshot) { v.Quests[0].State = nil },
		"missing requires accepter": func(v *o.WorldProgressionSnapshot) {
			v.Quests[0].RequiresAccepter = nil
		},
		"missing can accept": func(v *o.WorldProgressionSnapshot) { v.Quests[0].CanAccept = nil },
		"missing quest snapshot": func(v *o.WorldProgressionSnapshot) {
			v.Quests[0].Snapshot = nil
		},
		"quest snapshot entity mismatch": func(v *o.WorldProgressionSnapshot) {
			v.Quests[0].Snapshot.EntityId = proto.String("other-quest")
		},
		"invalid quest snapshot token": func(v *o.WorldProgressionSnapshot) {
			v.Quests[0].Snapshot.Token = proto.String("")
		},
		"missing eligible quest pawn id": func(v *o.WorldProgressionSnapshot) {
			v.Quests[0].EligiblePawns[0].Id = nil
		},
		"duplicate eligible quest pawn": func(v *o.WorldProgressionSnapshot) {
			v.Quests[0].EligiblePawns = append(v.Quests[0].EligiblePawns, v.Quests[0].EligiblePawns[0])
		},
		"negative food days":         func(v *o.WorldProgressionSnapshot) { v.Caravans[0].FoodDays = proto.Float64(-1) },
		"duplicate inventory def":    func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Inventory = append(v.Caravans[0].Inventory, v.Caravans[0].Inventory[0]) },
		"missing inventory units":    func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Inventory[0].Units = nil },
		"negative inventory units":   func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Inventory[0].Units = proto.Int64(-1) },
		"missing home route destination": func(v *o.WorldProgressionSnapshot) {
			v.Caravans[0].HomeRoutes[0].Destination = nil
		},
		"missing home route reachable": func(v *o.WorldProgressionSnapshot) {
			v.Caravans[0].HomeRoutes[0].Reachable = nil
		},
		"negative home route ticks": func(v *o.WorldProgressionSnapshot) {
			v.Caravans[0].HomeRoutes[0].EstimatedTicks = proto.Int64(-1)
		},
		"negative trade request count": func(v *o.WorldProgressionSnapshot) {
			v.Quests[0].TradeRequests[0].Count = proto.Int64(-1)
		},
		"negative trade request destination": func(v *o.WorldProgressionSnapshot) {
			v.Quests[0].TradeRequests[0].Destination = proto.Int32(-1)
		},
	}
	for name, edit := range edits {
		t.Run(name, func(t *testing.T) {
			snapshot := worldProgressionFixture()
			edit(snapshot)
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(&o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: snapshot}}), nil
			}}, time.Second)
			if _, _, err := client.ReadWorldProgression(context.Background(), pbIdentity(), false); err == nil {
				t.Fatal("malformed world progression accepted")
			}
		})
	}
}
