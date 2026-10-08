package observation

import (
	"context"
	"os"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func questSource(base *o.ColonyFactsReply, read bridge.WorldProgressionRead) *projectSource {
	return &projectSource{colonySource: &colonySource{reply: base}, frame: bridge.RoundsFrame{Quests: &read}}
}

func TestRoundsQuestCensusReadsTheFrame(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	base := &o.ColonyFactsReply{}
	if err := protojson.Unmarshal(data, base); err != nil {
		t.Fatal(err)
	}
	identity := &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Paused: proto.Bool(true)}}}
	expected, err := DecodeIdentity(identity)
	if err != nil {
		t.Fatal(err)
	}
	newSource := func() *projectSource {
		return &projectSource{colonySource: &colonySource{reply: base}}
	}
	clock := testkit.NewManualClock(time.Now())
	ctx := context.Background()

	// A source without the world-progression read leaves the census unknown.
	out, err := observeRoundsUnowned(ctx, newSource(), clock, expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := out.Projection.Facts.QuestOffers.Value(); known {
		t.Fatal("quest census known without a source")
	}

	read := bridge.WorldProgressionRead{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Quests: []bridge.QuestOffer{
		{ID: "Quest_4", ScriptDef: "ThreatReward_Raid_Joiner", State: "NotYetAccepted", CanAccept: true, ChoiceCount: 1},
		{ID: "Quest_2", ScriptDef: "TradeRequest", State: "Ongoing"},
	}}
	read.Quests[0].ExpiresInTicks = proto.Int64(600)
	read.Quests[0].EligiblePawnIDs = []string{"Pawn_1"}
	read.Quests[0].Objectives = []bridge.QuestObjectiveFact{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_PRODUCE_ITEM, Def: "ComponentIndustrial", Count: proto.Int64(12), Produced: proto.Int64(3), DeadlineTicks: proto.Int64(900)}}
	out, err = observeRoundsUnowned(ctx, questSource(base, read), clock, expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	want := []policy.JoinerOffer{
		{Quest: "Quest_4", ScriptDef: "ThreatReward_Raid_Joiner", State: "NotYetAccepted", CanAccept: true, ChoiceCount: 1},
		{Quest: "Quest_2", ScriptDef: "TradeRequest", State: "Ongoing"},
	}
	want[0].ExpiresInTicks = domain.Known(int64(600))
	want[0].EligiblePawnIDs = []domain.PawnID{"Pawn_1"}
	want[0].Objectives = []policy.QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_PRODUCE_ITEM, Def: "ComponentIndustrial", Count: domain.Known(int64(12)), Produced: domain.Known(int64(3)), DeadlineTicks: domain.Known(int64(900))}}
	if facts := out.Projection.Facts.QuestOffers; !reflect.DeepEqual(facts, domain.Known(want)) {
		t.Fatal(facts)
	}
	empty := bridge.WorldProgressionRead{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext)}
	out, err = observeRoundsUnowned(ctx, questSource(base, empty), clock, expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if offers, known := out.Projection.Facts.QuestOffers.Value(); !known || len(offers) != 0 {
		t.Fatal("an empty census is a known empty census")
	}
}
