package buildingruntime

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	factsstore "github.com/davidarcher/RimGovernor/go/internal/facts"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func clockFactsPage(events ...*k.Event) *k.EventsPage { return &k.EventsPage{Events: events} }

func clockFactsOutcome(action string) *k.Event {
	return &k.Event{Event: &k.Event_OperationOutcome{OperationOutcome: &k.OperationOutcome{Attempt: &c.AttemptKey{ActionId: proto.String(action), AttemptId: proto.Uint64(1)}}}}
}

// TestClockPageInvalidation: a watched construction outcome drops the
// families a build changes; an outcome of an attempt the scheduler did not
// arm, an authority change, an epoch start or a stop drops everything; an
// ObservationInvalidated event drops exactly the families it names, and an
// unknown family is treated as everything rather than ignored.
func TestClockPageInvalidation(t *testing.T) {
	facts := newClockFacts(nil, nil)
	facts.remember([]clockWorkItem{{Action: "build", Kind: domain.BuildingAction, Attempt: 1}, {Action: "unarmed", Kind: domain.HaulAction}})
	cases := []struct {
		name     string
		page     *k.EventsPage
		all      bool
		families []bridge.FactFamily
	}{
		{"none", clockFactsPage(&k.Event{Event: &k.Event_SpeedChanged{SpeedChanged: &k.SpeedChanged{}}}), false, nil},
		{"construction", clockFactsPage(clockFactsOutcome("build")), false, []bridge.FactFamily{bridge.FactColony, bridge.FactRooms, bridge.FactPawns}},
		{"unknown-attempt", clockFactsPage(clockFactsOutcome("unarmed")), true, nil},
		{"authority", clockFactsPage(&k.Event{Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{}}}), true, nil},
		{"started", clockFactsPage(&k.Event{Event: &k.Event_Started{Started: &k.EpochStarted{}}}), true, nil},
		{"stopped", clockFactsPage(clockFactsOutcome("build"), &k.Event{Event: &k.Event_Stopped{Stopped: &k.StopEvent{}}}), true, nil},
		{"invalidated", clockFactsPage(
			&k.Event{Event: &k.Event_ObservationInvalidated{ObservationInvalidated: &k.ObservationInvalidated{Families: []k.FactFamily{k.FactFamily_FACT_FAMILY_RESEARCH, k.FactFamily_FACT_FAMILY_DEFINITIONS}}}},
			&k.Event{Event: &k.Event_ObservationInvalidated{ObservationInvalidated: &k.ObservationInvalidated{Families: []k.FactFamily{k.FactFamily_FACT_FAMILY_RESEARCH}}}},
		), false, []bridge.FactFamily{bridge.FactResearch, bridge.FactDefinitions}},
		{"invalidated-unknown", clockFactsPage(&k.Event{Event: &k.Event_ObservationInvalidated{ObservationInvalidated: &k.ObservationInvalidated{Families: []k.FactFamily{k.FactFamily(99)}}}}), true, nil},
	}
	for _, tc := range cases {
		all, families := clockPageInvalidation(tc.page, facts.kindOf)
		if all != tc.all || !reflect.DeepEqual(families, tc.families) {
			t.Fatalf("%s: all=%v families=%v", tc.name, all, families)
		}
	}
	// apply drives the parent cache and the state store alike: after a
	// construction outcome the research section survives and the rooms
	// section is gone; an authority change empties the store.
	facts.cache.Invalidate()
	scope := factsstore.Scope{Load: "l", Generation: 1}
	factsstore.Put(facts.store, scope, factsstore.Research, factsstore.Held[int]{AsOf: 1, Source: "r"})
	factsstore.Put(facts.store, scope, factsstore.Rooms, factsstore.Held[int]{AsOf: 1, Source: "r"})
	before := facts.cache.Stats().Invalidations
	facts.apply(cases[1].page)
	facts.apply(cases[0].page)
	if got := facts.cache.Stats().Invalidations; got != before+1 {
		t.Fatalf("apply invalidated %d times", got-before)
	}
	if _, ok := factsstore.Get[int](facts.store, factsstore.Rooms); ok {
		t.Fatal("rooms survived a construction outcome")
	}
	if _, ok := factsstore.Get[int](facts.store, factsstore.Research); !ok {
		t.Fatal("research dropped by a construction outcome")
	}
	facts.apply(cases[3].page)
	if facts.store.Len() != 0 {
		t.Fatal("an authority change must empty the store")
	}
}

// TestClockFactsRememberBounded: the watched-kind memory never grows past
// its bound; overflow clears it, which only broadens later invalidation.
func TestClockFactsRememberBounded(t *testing.T) {
	facts := newClockFacts(nil, nil)
	for i := 0; i < clockFactsWatchedMax+5; i++ {
		facts.remember([]clockWorkItem{{Action: domain.ActionID(string(rune('a'+i%26)) + string(rune('a'+i/26))), Kind: domain.BuildingAction, Attempt: 1}})
	}
	if n := len(facts.watched); n == 0 || n > clockFactsWatchedMax {
		t.Fatal(n)
	}
}
