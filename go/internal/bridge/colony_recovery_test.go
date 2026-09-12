package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func recoveryCensus() *o.ColonyFactsSnapshot {
	context := &c.ObservationContext{Identity: &c.Identity{MapId: proto.Int32(0)}, Tick: proto.Int64(10)}
	counts := &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	b := &o.BuildingState{Building: &o.EntityRef{Id: proto.String("wall"), MapId: proto.Int32(0)}, UsesHitPoints: proto.Bool(true), HitPoints: proto.Int32(50), MaxHitPoints: proto.Int32(100), Burning: proto.Bool(false), Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}, Service: &o.BuildingServiceState{BrokenDown: proto.Bool(false), Fuel: proto.Float64(0), TargetFuel: proto.Float64(10)}}
	return &o.ColonyFactsSnapshot{Context: context, MapSize: &o.MapSize{Width: proto.Uint32(10), Height: proto.Uint32(10)}, Recovery: &o.RecoveryReply{Outcome: &o.RecoveryReply_Observed{Observed: &o.RecoverySnapshot{Context: proto.Clone(context).(*c.ObservationContext), Buildings: []*o.BuildingState{b}, Completeness: counts}}}}
}
func TestColonyRecoveryBoundary(t *testing.T) {
	for _, name := range []string{"valid", "unknown", "partial", "stale", "duplicate", "foreign", "hp", "nonfinite", "extra", "conflict"} {
		t.Run(name, func(t *testing.T) {
			v := recoveryCensus()
			r := v.Recovery.GetObserved()
			b := r.Buildings[0]
			switch name {
			case "unknown":
				b.Service.Fuel = nil
				b.Service.TargetFuel = nil
				b.Burning = nil
			case "partial":
				r.Completeness.Page.Complete = proto.Bool(false)
			case "stale":
				r.Context.Tick = proto.Int64(9)
			case "duplicate":
				r.Buildings = append(r.Buildings, proto.Clone(b).(*o.BuildingState))
				r.Completeness.Matched = proto.Uint64(2)
				r.Completeness.Returned = proto.Uint64(2)
			case "foreign":
				b.Building.MapId = proto.Int32(1)
			case "hp":
				b.HitPoints = proto.Int32(101)
			case "nonfinite":
				b.Service.Fuel = proto.Float64(math.Inf(1))
			case "extra":
				b.Service.PowerOn = proto.Bool(true)
			case "conflict":
				b.Service.Issues = []*o.ReadIssue{{Field: proto.String("fuel"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}
			}
			err := validateColonyRecovery(v)
			if (err == nil) != (name == "valid" || name == "unknown") {
				t.Fatal(err)
			}
		})
	}
}
