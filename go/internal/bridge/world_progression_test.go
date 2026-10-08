package bridge

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	rp "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func worldProgressionFixture() *o.WorldProgressionSnapshot {
	return &o.WorldProgressionSnapshot{
		Context: pbContext(),
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
			Id: proto.String("quest-1"), State: rp.QuestStatus_QUEST_STATUS_NOT_YET_ACCEPTED.Enum(),
			RequiresAccepter: proto.Bool(true), CanAccept: proto.Bool(true),
			EligiblePawns: []*c.Ref{{Id: proto.String("pawn-1")}},
			Rewards:       []*o.QuestReward{{ChoiceIndex: proto.Uint32(0)}, {ChoiceIndex: proto.Uint32(1), Favor: proto.Int32(4)}, {ChoiceIndex: proto.Uint32(1), Favor: proto.Int32(2)}},
			FactionId:     proto.String("Faction_5"), MapId: proto.Int32(2),
			TradeRequests: []*o.QuestTradeRequest{{Resource: proto.String("Steel"), Count: proto.Int64(40), Destination: proto.Int32(7)}},
			Snapshot:      &o.SnapshotRef{Context: pbContext(), EntityId: proto.String("quest-1"), Token: proto.String("quest-cas")},
		}},
	}
}

func TestQuestObjectiveCensus(t *testing.T) {
	for kind := o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_UNKNOWN; kind <= o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_EXPIRY; kind++ {
		t.Run(kind.String(), func(t *testing.T) {
			v := worldProgressionFixture()
			v.Quests[0].Objectives = []*o.QuestObjective{{Kind: kind.Enum(), Def: proto.String("Steel"), Stuff: proto.String("WoodLog"), Count: proto.Int64(10), Produced: proto.Int64(3), DeadlineTicks: proto.Int64(6000), UnmetRequirement: proto.String("Bedroom required"), PawnIds: []string{"pawn-1"}}}
			out, err := worldProgressionSelected(v, pbIdentity())
			if err != nil {
				t.Fatal(err)
			}
			fact := out.Quests[0].Objectives[0]
			if fact.Kind != kind || fact.Def != "Steel" || fact.Stuff != "WoodLog" || fact.Count == nil || *fact.Count != 10 || fact.Produced == nil || *fact.Produced != 3 || fact.DeadlineTicks == nil || *fact.DeadlineTicks != 6000 || fact.UnmetRequirement != "Bedroom required" || len(fact.PawnIDs) != 1 {
				t.Fatal(fact)
			}
		})
	}
}

func TestQuestObjectiveRejectsMalformedEvidence(t *testing.T) {
	for name, objective := range map[string]*o.QuestObjective{
		"nil":               nil,
		"missing kind":      {},
		"unknown enum":      {Kind: o.QuestObjectiveKind(99).Enum()},
		"negative count":    {Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_PRODUCE_ITEM.Enum(), Count: proto.Int64(-1)},
		"negative progress": {Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_HARVEST_PLANT.Enum(), Produced: proto.Int64(-1)},
		"negative deadline": {Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_EXPIRY.Enum(), DeadlineTicks: proto.Int64(-1)},
		"invalid pawn":      {Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_LOAD_NAMED_PAWNS.Enum(), PawnIds: []string{""}},
		"duplicate pawn":    {Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_HOST_LODGERS.Enum(), PawnIds: []string{"pawn-1", "pawn-1"}},
	} {
		t.Run(name, func(t *testing.T) {
			v := worldProgressionFixture()
			v.Quests[0].Objectives = []*o.QuestObjective{objective}
			if _, err := worldProgressionSelected(v, pbIdentity()); err == nil {
				t.Fatal("accepted malformed objective")
			}
		})
	}
}

func TestQuestFavorOutsideChoice(t *testing.T) {
	v := worldProgressionFixture()
	v.Quests[0].Rewards = []*o.QuestReward{{Favor: proto.Int32(5)}}
	out, err := worldProgressionSelected(v, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if q := out.Quests[0]; q.ChoiceCount != 0 || len(q.Favor) != 1 || q.Favor[0] != (QuestFavorFact{Choice: -1, Favor: 5}) {
		t.Fatal(q)
	}
	if len(out.Quests[0].Objectives) != 0 {
		t.Fatal("invented objectives")
	}
}

func TestQuestTypedRewards(t *testing.T) {
	v := worldProgressionFixture()
	v.Quests[0].ChoicePartCount = proto.Int32(1)
	v.Quests[0].Rewards = []*o.QuestReward{{ChoiceIndex: proto.Uint32(0), Items: []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(20)}}, Goodwill: proto.Int32(10), Psylink: proto.Int32(1), PermitPoints: proto.Int32(2), Permits: []string{"CallAid"}, TitleDef: proto.String("Knight"), FactionId: proto.String("Faction_5")}}
	out, err := worldProgressionSelected(v, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	reward := out.Quests[0].Rewards[0]
	if reward.Choice != 0 || len(reward.Items) != 1 || reward.Items[0].Count != 20 || reward.Goodwill != 10 || reward.Psylink != 1 || reward.PermitPoints != 2 || len(reward.Permits) != 1 || reward.TitleDef != "Knight" || reward.FactionID != "Faction_5" || out.Quests[0].RewardChoiceParts == nil || *out.Quests[0].RewardChoiceParts != 1 {
		t.Fatal(out.Quests[0])
	}
}

func TestQuestTypedRewardRejectsMalformedEvidence(t *testing.T) {
	for name, reward := range map[string]*o.QuestReward{
		"negative psylink":       {Psylink: proto.Int32(-1)},
		"negative permit points": {PermitPoints: proto.Int32(-1)},
		"nil item":               {Items: []*o.Quantity{nil}},
		"missing item units":     {Items: []*o.Quantity{{DefName: proto.String("Steel")}}},
		"negative item units":    {Items: []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(-1)}}},
		"duplicate permit":       {Permits: []string{"CallAid", "CallAid"}},
		"invalid permit":         {Permits: []string{""}},
	} {
		t.Run(name, func(t *testing.T) {
			v := worldProgressionFixture()
			v.Quests[0].Rewards = []*o.QuestReward{reward}
			if _, err := worldProgressionSelected(v, pbIdentity()); err == nil {
				t.Fatal("accepted malformed reward")
			}
		})
	}
}
func TestReadWorldProgressionAcceptsValidObservation(t *testing.T) {
	snapshot := worldProgressionFixture()
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*callResult, error) {
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
		if !q.GetIncludeStorage() {
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
	if !caravan.FoodDaysKnown || caravan.FoodDays != 2.5 || caravan.Inventory["Silver"] != 50 || caravan.Inventory["Steel"] != 75 {
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
		quest.ChoiceCount != 2 || quest.FactionID != "Faction_5" || !quest.MapKnown || quest.MapID != 2 ||
		len(quest.Favor) != 1 || quest.Favor[0] != (QuestFavorFact{Choice: 1, Favor: 6}) || len(quest.EligiblePawnIDs) != 1 || quest.EligiblePawnIDs[0] != "pawn-1" ||
		quest.SnapshotToken != "quest-cas" || len(quest.TradeRequests) != 1 || quest.TradeRequests[0].DestinationTile != 7 {
		t.Fatal(quest)
	}
	if len(quest.TradeRequests) != 1 || quest.TradeRequests[0].Resource != "Steel" || quest.TradeRequests[0].Count != 40 || quest.TradeRequests[0].DestinationTile != 7 {
		t.Fatal(quest.TradeRequests)
	}
}
func TestReadWorldProgressionRejectsInvalidInputs(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
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
		"missing map id":      func(v *o.WorldProgressionSnapshot) { v.Maps[0].Id = nil },
		"missing map tile":    func(v *o.WorldProgressionSnapshot) { v.Maps[0].Tile = nil },
		"negative map tile":   func(v *o.WorldProgressionSnapshot) { v.Maps[0].Tile = proto.Int32(-1) },
		"missing map home":    func(v *o.WorldProgressionSnapshot) { v.Maps[0].Home = nil },
		"missing map pawn id": func(v *o.WorldProgressionSnapshot) { v.Maps[0].Pawns[0].Pawn.Id = nil },
		"duplicate map pawn":  func(v *o.WorldProgressionSnapshot) { v.Maps[0].Pawns = append(v.Maps[0].Pawns, v.Maps[0].Pawns[0]) },
		"pawn on two maps": func(v *o.WorldProgressionSnapshot) {
			v.Maps[1].Pawns = append(v.Maps[1].Pawns, v.Maps[0].Pawns[0])
		},
		"missing caravan id":    func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Caravan.Id = nil },
		"duplicate caravan":     func(v *o.WorldProgressionSnapshot) { v.Caravans = append(v.Caravans, v.Caravans[0]) },
		"negative caravan tile": func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Tile = proto.Int32(-1) },
		"missing pawn id":       func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Pawns[0].Pawn.Id = nil },
		"duplicate pawn": func(v *o.WorldProgressionSnapshot) {
			v.Caravans[0].Pawns = append(v.Caravans[0].Pawns, v.Caravans[0].Pawns[0])
		},
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
		"negative food days": func(v *o.WorldProgressionSnapshot) { v.Caravans[0].FoodDays = proto.Float64(-1) },
		"duplicate inventory def": func(v *o.WorldProgressionSnapshot) {
			v.Caravans[0].Inventory = append(v.Caravans[0].Inventory, v.Caravans[0].Inventory[0])
		},
		"missing inventory units":  func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Inventory[0].Units = nil },
		"negative inventory units": func(v *o.WorldProgressionSnapshot) { v.Caravans[0].Inventory[0].Units = proto.Int64(-1) },
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
			client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
				return pbResult(&o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: snapshot}}), nil
			}}, time.Second)
			if _, _, err := client.ReadWorldProgression(context.Background(), pbIdentity(), false); err == nil {
				t.Fatal("malformed world progression accepted")
			}
		})
	}
}
