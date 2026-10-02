package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func recoveryCensus() *o.ColonyFactsSnapshot {
	context := &c.ObservationContext{Identity: &c.Identity{MapId: proto.Int32(0)}, Tick: proto.Int64(10)}
	b := &o.EntityRef{Id: proto.String("wall"), MapId: proto.Int32(0)}
	return &o.ColonyFactsSnapshot{Context: context, MapSize: &o.MapSize{Width: proto.Uint32(10), Height: proto.Uint32(10)}, Recovery: &o.RecoveryReply{Outcome: &o.RecoveryReply_Observed{Observed: &o.RecoverySnapshot{Context: proto.Clone(context).(*c.ObservationContext), Buildings: []*o.EntityRef{b}}}}}
}

func TestColonyRecoveryBoundary(t *testing.T) {
	for _, name := range []string{"valid", "stale", "duplicate", "foreign", "extra"} {
		t.Run(name, func(t *testing.T) {
			v := recoveryCensus()
			r := v.Recovery.GetObserved()
			b := r.Buildings[0]
			switch name {
			case "stale":
				r.Context.Tick = proto.Int64(9)
			case "duplicate":
				r.Buildings = append(r.Buildings, proto.Clone(b).(*o.EntityRef))
			case "foreign":
				b.MapId = proto.Int32(1)
			case "extra":
				b.Snapshot = &o.SnapshotRef{Token: proto.String("t")}
			}
			err := validateColonyRecovery(v)
			if (err == nil) != (name == "valid") {
				t.Fatal(err)
			}
		})
	}
}
