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

type questSource struct {
	*projectSource
	read bridge.WorldProgressionRead
	err  error
}

func (s *questSource) ReadWorldProgression(context.Context, *c.Identity, bool) (bridge.WorldProgressionRead, bridge.Result, error) {
	return s.read, bridge.Result{}, s.err
}

func TestRoutineQuestCensusStaysInsideBracket(t *testing.T) {
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
	out, err := ObserveRoutine(ctx, newSource(), clock, expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := out.Projection.Facts.QuestOffers.Value(); known {
		t.Fatal("quest census known without a source")
	}

	read := bridge.WorldProgressionRead{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Quests: []bridge.QuestOffer{
		{ID: "Quest_4", ScriptDef: "ThreatReward_Raid_Joiner", State: "NotYetAccepted", CanAccept: true, ChoiceCount: 1},
		{ID: "Quest_2", ScriptDef: "TradeRequest", State: "Ongoing", HasTradeRequest: true},
	}}
	out, err = ObserveRoutine(ctx, &questSource{projectSource: newSource(), read: read}, clock, expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	want := []policy.JoinerOffer{
		{Quest: "Quest_4", ScriptDef: "ThreatReward_Raid_Joiner", State: "NotYetAccepted", CanAccept: true, ChoiceCount: 1},
		{Quest: "Quest_2", ScriptDef: "TradeRequest", State: "Ongoing"},
	}
	if facts := out.Projection.Facts.QuestOffers; !reflect.DeepEqual(facts, domain.Known(want)) {
		t.Fatal(facts)
	}
	empty := bridge.WorldProgressionRead{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext)}
	out, err = ObserveRoutine(ctx, &questSource{projectSource: newSource(), read: empty}, clock, expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if offers, known := out.Projection.Facts.QuestOffers.Value(); !known || len(offers) != 0 {
		t.Fatal("an empty census is a known empty census")
	}

	// A census from a different colony boundary invalidates the reading.
	changed := bridge.WorldProgressionRead{Context: proto.Clone(read.Context).(*c.ObservationContext), Quests: read.Quests}
	changed.Context.Identity.ColonyId = proto.String("other")
	if _, err = ObserveRoutine(ctx, &questSource{projectSource: newSource(), read: changed}, clock, expected, time.Second); err == nil {
		t.Fatal("changed colony accepted")
	}
	if _, err = ObserveRoutine(ctx, &questSource{projectSource: newSource(), err: context.DeadlineExceeded}, clock, expected, time.Second); err == nil {
		t.Fatal("failed quest read accepted")
	}
}
