package bridge

import (
	"errors"
	"testing"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

func ruleFired() *k.RuleFired {
	return &k.RuleFired{RuleId: proto.String("hunt-chain"), Job: proto.String("Hunt"), Radius: proto.Uint32(40),
		ActorId: proto.String("Thing_Human1"), TargetId: proto.String("Thing_Deer2"), Tick: proto.Int64(900)}
}

// The native rule events arrive with or without an epoch, so they carry no
// owner.
func TestClockRuleEventsAreOwnerlessFacts(t *testing.T) {
	owned := clockTestEpoch().Owner
	fired := &k.Event{Event: &k.Event_RuleFired{RuleFired: ruleFired()}}
	expired := &k.Event{Event: &k.Event_RuleLeaseExpired{RuleLeaseExpired: &k.RuleLeaseExpired{ExpiresAtTick: proto.Int64(2500), Deactivated: proto.Uint32(1)}}}
	for name, row := range map[string]*k.Event{"fired": fired, "expired": expired, "fired owned": {Owner: owned, Event: fired.Event}} {
		if err := clockEventsPage(clockWatchPage(row), clockEventsRequest()); err != nil {
			t.Fatal(name, err)
		}
	}
	noActor := ruleFired()
	noActor.ActorId = nil
	negative := ruleFired()
	negative.Tick = proto.Int64(-1)
	for name, row := range map[string]*k.Event{
		"no actor":       {Event: &k.Event_RuleFired{RuleFired: noActor}},
		"negative tick":  {Event: &k.Event_RuleFired{RuleFired: negative}},
		"no deactivated": {Event: &k.Event_RuleLeaseExpired{RuleLeaseExpired: &k.RuleLeaseExpired{ExpiresAtTick: proto.Int64(2500)}}},
	} {
		if err := clockEventsPage(clockWatchPage(row), clockEventsRequest()); !errors.Is(err, ErrContract) {
			t.Fatal(name, err)
		}
	}
}
