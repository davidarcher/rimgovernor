package bridge

import (
	"context"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// TestReadBuildingsServesTheHeldTable: through the stream the
// building list read is the held table version, so no list reply is
// encoded for it.
func TestReadBuildingsServesTheHeldTable(t *testing.T) {
	client, server, ring := frameClient(t)
	frame := func(tick int64, rows *o.BuildingsSnapshot, seq uint64, delta bool, base uint64) *o.BundleSnapshot {
		v := proto.Clone(server.snapshot).(*o.BundleSnapshot)
		retagContexts(v, &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(7)})
		v.Buildings = rows
		rows.Context = v.Context
		w := &o.SectionWatermark{Section: proto.String("buildings"), Seq: proto.Uint64(seq), CapturedTick: proto.Int64(tick)}
		if delta {
			w.Delta, w.BaseSeq = proto.Bool(true), proto.Uint64(base)
		}
		v.Watermarks = append(v.Watermarks, w)
		return v
	}
	ring.publish(t, frame(12, &o.BuildingsSnapshot{Buildings: []*o.BuildingState{rowBuilding("a", 1), rowBuilding("b", 2)}}, 1, false, 0), 0)
	rows, _, err := client.ReadBuildings(context.Background(), pbIdentity())
	if err != nil || rows.Rows.Len() != 2 || rows.AsOf() != 12 {
		t.Fatalf("keyframe: %d rows, %v", rows.Rows.Len(), err)
	}
	ring.publish(t, frame(13, &o.BuildingsSnapshot{Buildings: []*o.BuildingState{rowBuilding("a", 9)}, Removed: []string{"b"}}, 2, true, 1), 0)
	next, _, err := client.ReadBuildings(context.Background(), pbIdentity())
	if err != nil || next.Rows.Len() != 1 || next.Rows.At("a").GetHitPoints() != 9 || next.AsOf() != 13 {
		t.Fatalf("delta: %v rows %v", err, next.Rows.Keys())
	}
	if rows.Rows.Len() != 2 || rows.Rows.At("a").GetHitPoints() != 1 {
		t.Fatal("the version read before the delta changed")
	}
	s := client.frames
	s.mu.Lock()
	defer s.mu.Unlock()
	for key, reply := range s.table {
		if reply.payload != nil {
			t.Fatalf("%s %s was encoded by a held table read", key.method, key.request)
		}
	}
}
