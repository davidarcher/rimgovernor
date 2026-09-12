package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestColonyDisasterPreservesServiceUnknowns(t *testing.T) {
	v := &o.ColonyFactsSnapshot{Context: &c.ObservationContext{Tick: proto.Int64(12)}, Environment: []*o.EnvironmentCondition{{Id: proto.String("1"), DefName: proto.String("ColdSnap")}}}
	f := policy.RoutineFacts{}
	colonyDisaster(v, &f)
	if rows, k := f.DisasterConditions.Value(); !k || len(rows) != 1 || f.DisasterTick != 12 {
		t.Fatal(f)
	}
	if _, k := f.RecoveryBuildings.Value(); k {
		t.Fatal("missing census became empty")
	}
	b := &o.BuildingState{Building: &o.EntityRef{Id: proto.String("wall")}, UsesHitPoints: proto.Bool(true), HitPoints: proto.Int32(40), MaxHitPoints: proto.Int32(100), Burning: proto.Bool(false), Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}, Service: &o.BuildingServiceState{BrokenDown: proto.Bool(false)}}
	v.Recovery = &o.RecoveryReply{Outcome: &o.RecoveryReply_Observed{Observed: &o.RecoverySnapshot{Buildings: []*o.BuildingState{b}}}}
	f = policy.RoutineFacts{}
	colonyDisaster(v, &f)
	pending, err := policy.RecoveryPending(f.RecoveryBuildings)
	if _, k := pending.Value(); err != nil || k {
		t.Fatal("missing fuel component evidence recovered", err)
	}
	b.Service.Issues = []*o.ReadIssue{{Field: proto.String("fuel"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
	f = policy.RoutineFacts{}
	colonyDisaster(v, &f)
	pending, err = policy.RecoveryPending(f.RecoveryBuildings)
	rows, k := pending.Value()
	if err != nil || !k || len(rows) != 1 || rows[0].Method != policy.RecoveryRepair {
		t.Fatal(rows, k, err)
	}
	v.Environment = nil
	v.Issues = []*o.ReadIssue{{Field: proto.String("environment")}}
	f = policy.RoutineFacts{}
	colonyDisaster(v, &f)
	if _, k := f.DisasterConditions.Value(); k {
		t.Fatal("missing environment became clear")
	}
}
