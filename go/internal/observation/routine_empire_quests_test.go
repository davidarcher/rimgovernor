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

type royaltySource struct {
	*projectSource
	facts *policy.RoyaltyFacts
	now   int64
}

func (r *royaltySource) RoyaltyFacts(_ context.Context, _ *c.Identity, now int64) (*policy.RoyaltyFacts, error) {
	r.now = now
	return r.facts, nil
}

func TestRoutineQuestCensusJoinsFactionAndMapAndReadsRoyalty(t *testing.T) {
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
	home := int32(expected.Map)
	favor := []bridge.QuestFavorFact{{Choice: 1, Favor: 4}}
	read := bridge.WorldProgressionRead{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext),
		Factions: []bridge.FactionFact{{ID: "Faction_1", Hostile: true}, {ID: "Faction_2"}},
		Quests: []bridge.QuestOffer{
			{ID: "Quest_1", State: "NotYetAccepted", FactionID: "Faction_1", MapID: home, MapKnown: true},
			{ID: "Quest_2", State: "NotYetAccepted", FactionID: "Faction_2", MapID: home + 1, MapKnown: true, Favor: favor},
			{ID: "Quest_3", State: "NotYetAccepted", FactionID: "Faction_2", MapID: home, MapKnown: true, Favor: favor},
			{ID: "Quest_4", State: "NotYetAccepted", FactionID: "Faction_2"},
			{ID: "Quest_5", State: "NotYetAccepted", FactionID: "Faction_9", MapID: home, MapKnown: true},
		}}
	facts := &policy.RoyaltyFacts{}
	source := &royaltySource{projectSource: questSource(base, read), facts: facts}
	out, err := observeRoutineUnowned(context.Background(), source, testkit.NewManualClock(time.Now()), expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	offers, _ := out.Projection.Facts.QuestOffers.Value()
	type join struct{ hostile, hostileKnown, onMap bool }
	got := map[domain.QuestID]join{}
	for _, offer := range offers {
		hostile, known := offer.FactionHostile.Value()
		got[offer.Quest] = join{hostile, known, offer.OnMap}
	}
	want := map[domain.QuestID]join{"Quest_1": {true, true, true}, "Quest_2": {false, true, false}, "Quest_3": {false, true, true}, "Quest_4": {false, true, false}, "Quest_5": {false, false, true}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("%+v", got)
	}
	if offers[2].Favor[0] != (policy.QuestFavor{Choice: 1, Favor: 4}) {
		t.Fatalf("%+v", offers[2])
	}
	// The colonists' own royalty rides the pawn rows (#1876): this frame has
	// none, so royalty stays unknown.
	if _, known := out.Projection.Facts.Royalty.Value(); known {
		t.Fatal("royalty known without pawn rows")
	}
	if source.now != base.GetObserved().GetContext().GetTick() {
		t.Fatal("royalty read not at the frame tick")
	}
}
