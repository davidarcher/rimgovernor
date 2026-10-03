package bridge

import (
	"context"
	"errors"
	"fmt"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func rowBuilding(id string, hp int32) *o.BuildingState {
	return &o.BuildingState{Building: &o.EntityRef{Id: proto.String(id)}, HitPoints: proto.Int32(hp)}
}

func rowFrame(tick int64, buildings *o.BuildingsSnapshot, section string, seq uint64, delta bool, base uint64) *o.BundleSnapshot {
	ctx := &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(7)}
	v := &o.BundleSnapshot{Context: ctx}
	if buildings != nil {
		buildings.Context = ctx
		v.Buildings = buildings
	}
	w := &o.SectionWatermark{Section: proto.String(section), Seq: proto.Uint64(seq), CapturedTick: proto.Int64(tick)}
	if delta {
		w.Delta, w.BaseSeq = proto.Bool(true), proto.Uint64(base)
	}
	v.Watermarks = append(v.Watermarks, w)
	return v
}

// heldHPs is the hold's building table as id to hit points, read through
// the held version as a consumer reads it.
func heldHPs(h *sectionHold) map[string]int32 {
	out := map[string]int32{}
	for id, b := range h.heldAt(nil).buildings.All() {
		out[id] = b.GetHitPoints()
	}
	return out
}

// TestKeyedDeltaMergesAndTombstones (#1348): a delta's changed and new rows
// replace or join the held table, its removed ids leave it, an omitted
// unchanged table is the held one at the frame's tick, a delta against a
// base the hold does not have is a gap that drops the table, and the
// keyframe after the gap restores it. Versions a consumer took earlier are
// never changed by a later delta.
func TestKeyedDeltaMergesAndTombstones(t *testing.T) {
	var h sectionHold
	key := rowFrame(10, &o.BuildingsSnapshot{Buildings: []*o.BuildingState{rowBuilding("a", 1), rowBuilding("b", 2), rowBuilding("c", 3)}}, "buildings", 1, false, 0)
	if held, gap := h.fill(key); held != 0 || gap || len(heldHPs(&h)) != 3 {
		t.Fatalf("keyframe held %d gap %v rows %v", held, gap, heldHPs(&h))
	}
	if key.Buildings == nil || len(key.Buildings.Buildings) != 0 {
		t.Fatalf("a filled frame carries the table's envelope, not its rows: %v", key.Buildings)
	}
	first := h.heldAt(nil).buildings
	delta := rowFrame(11, &o.BuildingsSnapshot{Buildings: []*o.BuildingState{rowBuilding("b", 20), rowBuilding("d", 4)}, Removed: []string{"c"}}, "buildings", 2, true, 1)
	if held, gap := h.fill(delta); held != 1 || gap {
		t.Fatalf("delta held %d gap %v", held, gap)
	}
	got := heldHPs(&h)
	if len(got) != 3 || got["a"] != 1 || got["b"] != 20 || got["d"] != 4 {
		t.Fatalf("merged %v, want a=1 b=20 d=4", got)
	}
	if delta.Buildings.Context.GetTick() != 11 {
		t.Fatalf("merged table at tick %d, want the frame's 11", delta.Buildings.Context.GetTick())
	}
	if first.Len() != 3 || first.At("b").GetHitPoints() != 2 || first.Has("d") || !first.Has("c") {
		t.Fatal("an earlier version changed under a later delta")
	}
	same := rowFrame(12, nil, "buildings", 2, false, 0)
	if held, gap := h.fill(same); held != 1 || gap || len(heldHPs(&h)) != 3 || same.Buildings.Context.GetTick() != 12 {
		t.Fatalf("omitted table held %d gap %v rows %v", held, gap, heldHPs(&h))
	}
	skipped := rowFrame(14, &o.BuildingsSnapshot{Buildings: []*o.BuildingState{rowBuilding("a", 9)}}, "buildings", 4, true, 3)
	if _, gap := h.fill(skipped); !gap || skipped.Buildings != nil || len(heldHPs(&h)) != 0 {
		t.Fatalf("delta past a skipped frame: gap %v table %v", gap, skipped.Buildings)
	}
	later := rowFrame(15, nil, "buildings", 4, false, 0)
	if _, gap := h.fill(later); !gap || later.Buildings != nil {
		t.Fatalf("omission after a gap: gap %v table %v", gap, later.Buildings)
	}
	rekey := rowFrame(16, &o.BuildingsSnapshot{Buildings: []*o.BuildingState{rowBuilding("a", 9)}}, "buildings", 5, false, 0)
	if _, gap := h.fill(rekey); gap || len(heldHPs(&h)) != 1 {
		t.Fatalf("keyframe after the gap: gap %v rows %v", gap, heldHPs(&h))
	}
}

// TestKeyedDeltaMergesPawnsAndThings: the pawn and thing tables merge by
// their own row ids.
func TestKeyedDeltaMergesPawnsAndThings(t *testing.T) {
	var h sectionHold
	pawn := func(id string) *o.PawnState { return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}} }
	thing := func(id string, n int64) *o.Thing {
		return &o.Thing{Thing: &o.EntityRef{Id: proto.String(id)}, StackCount: proto.Int64(n)}
	}
	ctx := func(tick int64) *c.ObservationContext {
		return &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(7)}
	}
	marks := func(delta bool, seq, base uint64) []*o.SectionWatermark {
		var out []*o.SectionWatermark
		for _, name := range []string{"pawns", "things"} {
			w := &o.SectionWatermark{Section: proto.String(name), Seq: proto.Uint64(seq)}
			if delta {
				w.Delta, w.BaseSeq = proto.Bool(true), proto.Uint64(base)
			}
			out = append(out, w)
		}
		return out
	}
	h.fill(&o.BundleSnapshot{Context: ctx(1), Watermarks: marks(false, 1, 0),
		Pawns:  &o.PawnSnapshot{Pawns: []*o.PawnState{pawn("p1"), pawn("p2")}},
		Things: &o.ThingsSnapshot{Things: []*o.Thing{thing("t1", 5), thing("t2", 6)}}})
	v := &o.BundleSnapshot{Context: ctx(2), Watermarks: marks(true, 2, 1),
		Pawns:  &o.PawnSnapshot{Pawns: []*o.PawnState{pawn("p3")}, Removed: []string{"p1"}},
		Things: &o.ThingsSnapshot{Things: []*o.Thing{thing("t1", 50)}, Removed: []string{"t2"}}}
	if held, gap := h.fill(v); held != 2 || gap {
		t.Fatalf("held %d gap %v", held, gap)
	}
	tables := h.heldAt(ctx(2))
	if tables.pawns.Len() != 2 || tables.pawns.Has("p1") || !tables.pawns.Has("p2") || !tables.pawns.Has("p3") {
		t.Fatalf("pawns %v", tables.pawns.Keys())
	}
	if tables.things.Len() != 1 || tables.things.At("t1").GetStackCount() != 50 {
		t.Fatalf("things %v", tables.things.Keys())
	}
}

// TestHeldTableDeltaCostIsIndependentOfSize (#1578): applying a one-row
// delta allocates the same few trie nodes whether the held table has 100
// rows or 100000, and leaves the earlier version intact.
func TestHeldTableDeltaCostIsIndependentOfSize(t *testing.T) {
	cost := func(rows int) float64 {
		var h sectionHold
		all := make([]*o.BuildingState, rows)
		for i := range all {
			all[i] = rowBuilding(fmt.Sprintf("b%d", i), 1)
		}
		h.fill(rowFrame(1, &o.BuildingsSnapshot{Buildings: all}, "buildings", 1, false, 0))
		table := h.buildingTable()
		before := table.rows.table
		n := 0
		allocs := testing.AllocsPerRun(50, func() {
			n++
			table.rows.apply([]*o.BuildingState{rowBuilding(fmt.Sprintf("b%d", n%rows), int32(n))}, nil, nil)
		})
		if before.Len() != rows || before.At("b0").GetHitPoints() != 1 {
			t.Fatal("the version taken before the deltas changed")
		}
		return allocs
	}
	small, large := cost(100), cost(100000)
	t.Logf("allocations per one-row delta: %.0f at 100 rows, %.0f at 100000", small, large)
	if large > small+10 {
		t.Fatalf("delta cost grew with the table: %.0f vs %.0f", small, large)
	}
}

func rowPawn(id string) *o.PawnState {
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}}
}

// TestHeldPawnTableValidatesChangedRows (#1578): rows are validated as they
// arrive and a bad row fails the table until a delta replaces or removes it.
func TestHeldPawnTableValidatesChangedRows(t *testing.T) {
	var h sectionHold
	pawnFrame := func(tick int64, p *o.PawnSnapshot, seq uint64, delta bool, base uint64) *o.BundleSnapshot {
		ctx := &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(7)}
		v := &o.BundleSnapshot{Context: ctx, Pawns: p}
		w := &o.SectionWatermark{Section: proto.String("pawns"), Seq: proto.Uint64(seq)}
		if delta {
			w.Delta, w.BaseSeq = proto.Bool(true), proto.Uint64(base)
		}
		v.Watermarks = []*o.SectionWatermark{w}
		return v
	}
	h.fill(pawnFrame(1, &o.PawnSnapshot{Pawns: []*o.PawnState{rowPawn("a"), rowPawn("b")}}, 1, false, 0))
	if err := h.heldAt(nil).err(); err != nil {
		t.Fatal(err)
	}
	bad := rowPawn("b")
	bad.AnimalState = &o.AnimalState{Contained: proto.Bool(false), MinimumHandlingSkill: proto.Int32(-1)}
	h.fill(pawnFrame(2, &o.PawnSnapshot{Pawns: []*o.PawnState{bad, rowPawn("c")}}, 2, true, 1))
	if h.heldAt(nil).err() == nil {
		t.Fatal("an invalid changed row was accepted")
	}
	h.fill(pawnFrame(3, &o.PawnSnapshot{Removed: []string{"b"}}, 3, true, 2))
	tables := h.heldAt(nil)
	if err := tables.err(); err != nil {
		t.Fatalf("after the bad row left: %v", err)
	}
	if got := tables.pawns.Keys(); len(got) != 2 || got[0] != "a" || got[1] != "c" {
		t.Fatalf("held pawns %v", got)
	}
}

// TestFramesBuildNothingUnread (#1578): a delta frame encodes no reply
// until a reader asks for it.
func TestFramesBuildNothingUnread(t *testing.T) {
	s := &frameStream{}
	frame := func(v *o.BundleSnapshot) []byte {
		payload, err := proto.Marshal(v)
		if err != nil {
			t.Fatal(err)
		}
		return payload
	}
	rows := []*o.BuildingState{rowBuilding("a", 1), rowBuilding("b", 2)}
	if _, _, _, _, _, gap, _, _, err := s.frameTable(frame(rowFrame(1, &o.BuildingsSnapshot{Buildings: rows}, "buildings", 1, false, 0))); err != nil || gap {
		t.Fatal(err, gap)
	}
	table, _, _, _, _, _, _, _, err := s.frameTable(frame(rowFrame(2, &o.BuildingsSnapshot{Buildings: []*o.BuildingState{rowBuilding("a", 9)}}, "buildings", 2, true, 1)))
	if err != nil || len(table) == 0 {
		t.Fatal(err, len(table))
	}
	for key, reply := range table {
		if reply.payload != nil || reply.build == nil {
			t.Fatalf("%s encoded before a read", key.method)
		}
	}
}

// TestKeyedDeltaIsSmaller reports a synthetic steady colony: a 600-row
// building table of which 20 rows change per frame (no recorded frames
// exist to measure; native sizes are unmeasured).
func TestKeyedDeltaIsSmaller(t *testing.T) {
	var all, changed []*o.BuildingState
	for i := 0; i < 600; i++ {
		all = append(all, rowBuilding(fmt.Sprintf("Building_Wall%d", i), 300))
		if i%30 == 0 {
			changed = append(changed, rowBuilding(fmt.Sprintf("Building_Wall%d", i), 299))
		}
	}
	whole, _ := proto.Marshal(&o.BuildingsSnapshot{Buildings: all})
	delta, _ := proto.Marshal(&o.BuildingsSnapshot{Buildings: changed})
	t.Logf("whole table %d bytes, delta of %d rows %d bytes", len(whole), len(changed), len(delta))
	if len(delta)*10 > len(whole) {
		t.Fatalf("delta %d bytes is not a tenth of the whole %d", len(delta), len(whole))
	}
}

// TestFramesKeyedKeyframeOnSeqGap: through the stream, a building delta
// whose base the stream does not hold is not served and asks native for a
// keyframe; the keyframe restores the table.
func TestFramesKeyedKeyframeOnSeqGap(t *testing.T) {
	client, server, ring := frameClient(t)
	base := server.snapshot
	frame := func(tick int64, rows *o.BuildingsSnapshot, seq uint64, delta bool, baseSeq uint64) *o.BundleSnapshot {
		v := proto.Clone(base).(*o.BundleSnapshot)
		retagContexts(v, &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(7)})
		v.Buildings = rows
		if rows != nil {
			rows.Context = v.Context
		}
		w := &o.SectionWatermark{Section: proto.String("buildings"), Seq: proto.Uint64(seq), CapturedTick: proto.Int64(tick)}
		if delta {
			w.Delta, w.BaseSeq = proto.Bool(true), proto.Uint64(baseSeq)
		}
		v.Watermarks = append(v.Watermarks, w)
		return v
	}
	ring.publish(t, frame(12, &o.BuildingsSnapshot{Buildings: []*o.BuildingState{rowBuilding("a", 1), rowBuilding("b", 2)}}, 1, false, 0), 0)
	rows, _, err := client.ReadBuildings(context.Background(), pbIdentity())
	if err != nil || rows.Rows.Len() != 2 {
		t.Fatalf("keyframe: %d rows, %v", rows.Rows.Len(), err)
	}
	<-server.opens // the subscription's open
	ring.publish(t, frame(13, &o.BuildingsSnapshot{Buildings: []*o.BuildingState{rowBuilding("a", 10)}, Removed: []string{"b"}}, 2, true, 1), 0)
	rows, _, err = client.ReadBuildings(context.Background(), pbIdentity())
	if err != nil || rows.Rows.Len() != 1 || rows.Rows.At("a").GetHitPoints() != 10 {
		t.Fatalf("delta: %v rows %v", err, rows.Rows)
	}
	ring.publish(t, frame(20, &o.BuildingsSnapshot{Buildings: []*o.BuildingState{rowBuilding("a", 11)}}, 5, true, 4), 0)
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	if _, _, err := client.ReadBuildings(ctx, pbIdentity()); !errors.Is(err, ErrUnavailable) {
		t.Fatalf("delta past a gap: %v, want unavailable", err)
	}
	select {
	case request := <-server.opens:
		if !request.GetKeyframe() {
			t.Fatalf("open %v, want a keyframe request", request)
		}
	case <-time.After(time.Second):
		t.Fatal("no keyframe requested")
	}
	ring.publish(t, frame(21, &o.BuildingsSnapshot{Buildings: []*o.BuildingState{rowBuilding("a", 12), rowBuilding("z", 1)}}, 6, false, 0), 0)
	if rows, _, err = client.ReadBuildings(context.Background(), pbIdentity()); err != nil || rows.Rows.Len() != 2 {
		t.Fatalf("after the keyframe: %d rows, %v", rows.Rows.Len(), err)
	}
}
