package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
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

func TestColonyDisasterCarriesRemainingTicks(t *testing.T) {
	v := &o.ColonyFactsSnapshot{Context: &c.ObservationContext{Tick: proto.Int64(12)}, Environment: []*o.EnvironmentCondition{
		{Id: proto.String("1"), DefName: proto.String("ToxicFallout"), TicksLeft: proto.Int64(90000)},
		{Id: proto.String("2"), DefName: proto.String("VolcanicWinter"), Permanent: proto.Bool(true), TicksLeft: proto.Int64(5)},
		{Id: proto.String("3"), DefName: proto.String("ColdSnap"), TicksLeft: proto.Int64(-1)},
		{Id: proto.String("4"), DefName: proto.String("Eclipse")},
	}}
	f := policy.RoutineFacts{}
	colonyDisaster(v, &f)
	rows, k := f.DisasterConditions.Value()
	if !k || len(rows) != 4 {
		t.Fatal(rows, k)
	}
	if left, known := rows[0].RemainingTicks().Value(); !known || left != 90000 || rows[0].Permanent {
		t.Fatal("timed condition lost its remaining ticks", rows[0])
	}
	if _, known := rows[1].RemainingTicks().Value(); known || !rows[1].Permanent {
		t.Fatal("permanent condition kept a duration", rows[1])
	}
	for _, row := range rows[2:] {
		if _, known := row.RemainingTicks().Value(); known {
			t.Fatal("unreported or negative duration became known", row)
		}
	}
	if _, err := policy.ReviewDisaster(f.DisasterConditions, domain.Unknown[[]policy.RecoveryBuilding](), policy.FootholdGates{}, nil, 12); err != nil {
		t.Fatal(err)
	}
}
