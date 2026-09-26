package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func deltaContext(tick int64) *c.ObservationContext {
	return &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), MapId: proto.Int32(0)}, Tick: proto.Int64(tick)}
}

func deltaFacts(tick int64, yield float64) *o.ColonyFactsSnapshot {
	ctx := deltaContext(tick)
	source := func(id string) *o.EntityRef {
		return &o.EntityRef{Id: proto.String(id), DefName: proto.String("Plant_TreeOak"), Snapshot: &o.SnapshotRef{Context: proto.Clone(ctx).(*c.ObservationContext), EntityId: proto.String(id), Token: proto.String("t-" + id)}}
	}
	return &o.ColonyFactsSnapshot{Context: ctx, Biome: proto.String("TemperateForest"),
		Acquisition: []*o.AcquisitionFacts{{Source: source("Tree1"), Resource: proto.String("WoodLog"), Yield: proto.Float64(yield)}, {Source: source("Tree2"), Resource: proto.String("WoodLog"), Yield: proto.Float64(25)}},
		Planning:    &o.PlanningSection{},
		Upkeep:      &o.UpkeepSection{Outcome: &o.UpkeepSection_Unavailable{Unavailable: &c.Unavailable{Detail: proto.String("upkeep")}}},
	}
}

// A delta restores every held part from its base: a whole field, a keyed
// element left as a stub, and the base's contexts restamped with the
// reply's, so the result equals the full reply at the new tick.
func TestMergeHeldRestoresParts(t *testing.T) {
	base := deltaFacts(100, 10)
	full := deltaFacts(200, 12)
	reply := proto.Clone(full).(*o.ColonyFactsSnapshot)
	reply.Upkeep = nil
	reply.Acquisition[1] = &o.AcquisitionFacts{Source: &o.EntityRef{Id: proto.String("Tree2")}}
	if err := mergeHeld(reply.ProtoReflect(), base, []string{"30", "23[Tree2]"}); err != nil {
		t.Fatal(err)
	}
	if !proto.Equal(reply, full) {
		t.Fatalf("merged %v\nwant %v", reply, full)
	}
}

// A held path the base cannot answer fails the merge, so the read is
// retried in full rather than served with a hole.
func TestMergeHeldRefusesUnresolvedPaths(t *testing.T) {
	base := deltaFacts(100, 10)
	for _, path := range []string{"30", "23[Tree3]", "23[Tree1].1", "999", "x", "23[]"} {
		reply := deltaFacts(200, 10)
		base.Upkeep = nil
		if err := mergeHeld(reply.ProtoReflect(), base, []string{path}); err == nil {
			t.Errorf("path %q merged", path)
		}
	}
}

// Every deltaResyncEvery-th read of a shape goes without a token, and a
// dropped shape starts over in full.
func deltaAt(tracker string, tick int64, seq uint64) *o.SectionDelta {
	return &o.SectionDelta{Tracker: proto.String(tracker), AsOf: &o.Watermark{Tick: proto.Int64(tick), Seq: proto.Uint64(seq)}}
}

// Every deltaResyncEvery-th read of a request goes without an ask, the
// ask names the newest reply's tracker and watermark, and a dropped
// request starts over in full.
func TestDeltaStoreAsksAndResyncs(t *testing.T) {
	var store deltaStore
	if got := store.ask("k"); got != nil {
		t.Fatalf("first ask %v", got)
	}
	store.put("k", deltaAt("a", 100, 1), &o.PawnSnapshot{})
	full := 0
	for i := 0; i < 2*deltaResyncEvery; i++ {
		if ask := store.ask("k"); ask == nil {
			full++
		} else if ask.GetTracker() != "a" || ask.GetSince().GetTick() != 100 || ask.GetSince().GetSeq() != 1 {
			t.Fatalf("ask %v", ask)
		}
	}
	if full != 2 {
		t.Fatalf("full reads %d", full)
	}
	for seq := uint64(2); seq < 10; seq++ {
		store.put("k", deltaAt("a", 100, seq), &o.PawnSnapshot{})
	}
	if store.base("k", deltaMark("a", deltaAt("a", 100, 1).AsOf)) != nil || store.base("k", deltaMark("a", deltaAt("a", 100, 9).AsOf)) == nil {
		t.Fatal("store keeps the newest replies only")
	}
	if store.base("k", deltaMark("b", deltaAt("b", 100, 9).AsOf)) != nil {
		t.Fatal("another tracker's watermark matched")
	}
	store.drop("k")
	if store.ask("k") != nil {
		t.Fatal("dropped request asked since")
	}
}

// A tombstoned element still listed after the merge fails the delta; one
// gone from its list, or under an absent parent, passes.
func TestRemovedAbsentChecksTombstones(t *testing.T) {
	facts := deltaFacts(200, 10)
	if err := removedAbsent(facts.ProtoReflect(), []string{"23[Tree9]", "32.1.3.2[Human1]"}); err != nil {
		t.Fatal(err)
	}
	if err := removedAbsent(facts.ProtoReflect(), []string{"23[Tree1]"}); err == nil {
		t.Fatal("listed tombstone accepted")
	}
	if err := removedAbsent(facts.ProtoReflect(), []string{"23"}); err == nil {
		t.Fatal("unkeyed tombstone accepted")
	}
}

// The store key ignores the token and the bundle's moving parts.
func TestDeltaKeyIgnoresMovingParts(t *testing.T) {
	a := &o.BundleRequest{ColonyFacts: proto.Bool(true), Events: &o.BundleEventsRequest{AfterCursor: proto.Int64(3)}, PlanningWindow: &o.BundlePlanningWindowRequest{ChangedSinceTick: proto.Int64(5)}}
	b := &o.BundleRequest{ColonyFacts: proto.Bool(true), ChangedSince: &o.SectionDeltaAsk{Tracker: proto.String("x")}, Events: &o.BundleEventsRequest{AfterCursor: proto.Int64(9)}, PlanningWindow: &o.BundlePlanningWindowRequest{ChangedSinceTick: proto.Int64(7)}}
	if deltaKey("m", a) != deltaKey("m", b) {
		t.Fatal("moving parts shape the key")
	}
	if deltaKey("m", a) == deltaKey("m", &o.BundleRequest{Population: proto.Bool(true)}) {
		t.Fatal("sections do not shape the key")
	}
}
