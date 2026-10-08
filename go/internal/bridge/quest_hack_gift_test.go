package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestQuestHackGiftNativeEvidence(t *testing.T) {
	valid := &o.QuestObjective{HackTargets: []*o.QuestHackTarget{{Id: proto.String("Thing_1"), Spawned: proto.Bool(true), MapId: proto.Int32(0), Hackable: &o.HackableState{ProgressPercent: proto.Float64(.5), Autohack: proto.Bool(false)}, EligiblePawnIds: []string{"Pawn_1"}}}, GiftRequest: &o.QuestGiftRequest{RecipientId: proto.String("Pawn_2"), Def: proto.String("Silver"), MapId: proto.Int32(0), Remaining: proto.Int64(4), PawnIds: []string{"Pawn_2"}}, HackRisk: &o.QuestHackRisk{FactionId: proto.String("Faction_1"), Hostile: proto.Bool(false)}}
	var fact QuestObjectiveFact
	if err := decodeQuestHackGift(valid, &fact); err != nil {
		t.Fatal(err)
	}
	if fact.HackTargets[0].Hackable.Autohack == nil || fact.Gift.Remaining == nil || fact.HackRisk.Hostile == nil {
		t.Fatal("explicit false and zero-map facts lost")
	}
	valid.HackTargets[0].Hackable.ProgressPercent = proto.Float64(.8)
	if fact.HackTargets[0].Hackable.GetProgressPercent() != .5 {
		t.Fatal("decoded evidence aliases source")
	}
	for _, mutate := range []func(*o.QuestObjective){
		func(r *o.QuestObjective) { r.HackTargets[0].MapId = nil },
		func(r *o.QuestObjective) { r.HackTargets[0].Hackable.ProgressPercent = proto.Float64(-.1) },
		func(r *o.QuestObjective) { r.HackTargets[0].EligiblePawnIds = []string{"Pawn_1", "Pawn_1"} },
		func(r *o.QuestObjective) { r.GiftRequest.Remaining = proto.Int64(-1) },
		func(r *o.QuestObjective) { r.HackRisk.FactionId = proto.String("") },
	} {
		r := proto.Clone(valid).(*o.QuestObjective)
		mutate(r)
		var f QuestObjectiveFact
		if decodeQuestHackGift(r, &f) == nil {
			t.Fatal("malformed native evidence accepted")
		}
	}
}
