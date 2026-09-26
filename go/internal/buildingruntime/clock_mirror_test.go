package buildingruntime

import (
	"context"
	"fmt"
	"math/rand"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// trackingNative answers the entity reads the way a native with entity
// tracking does (#358): a delta lists the rows changed at or after the
// since tick and the ids removed since, answering a since older than the
// one-day tombstone window in full inline (#795). Buildings and bills carry the same rows as zones.
type trackingNative struct {
	tick    int64
	rows    map[string]*o.ZoneState
	changed map[string]int64
	removed map[string]int64
	asks    []int64
}

func (n *trackingNative) read(since int64) (bridge.EntityRows[*o.ZoneState], error) {
	n.asks = append(n.asks, since)
	if since > 0 && n.tick-since > 60000 {
		since = 0
	}
	out := bridge.EntityRows[*o.ZoneState]{Context: &c.ObservationContext{Tick: proto.Int64(n.tick)}, Rows: map[string]*o.ZoneState{}, Delta: since > 0}
	for id, row := range n.rows {
		if since == 0 || n.changed[id] >= since {
			out.Rows[id] = row
		} else {
			out.Unchanged++
		}
	}
	for id, at := range n.removed {
		if since > 0 && at >= since {
			out.Removed = append(out.Removed, id)
		}
	}
	return out, nil
}

func (n *trackingNative) ReadZones(_ context.Context, _ *c.Identity, since int64) (bridge.EntityRows[*o.ZoneState], bridge.Result, error) {
	rows, err := n.read(since)
	return rows, bridge.Result{}, err
}

func (n *trackingNative) ReadBuildings(_ context.Context, _ *c.Identity, since int64) (bridge.EntityRows[*o.BuildingState], bridge.Result, error) {
	return bridge.EntityRows[*o.BuildingState]{Context: &c.ObservationContext{Tick: proto.Int64(n.tick)}, Rows: map[string]*o.BuildingState{}, Delta: since > 0}, bridge.Result{}, nil
}

func (n *trackingNative) ReadBillStacks(_ context.Context, _ *c.Identity, since int64) (bridge.EntityRows[*o.BillStack], bridge.Result, error) {
	return bridge.EntityRows[*o.BillStack]{Context: &c.ObservationContext{Tick: proto.Int64(n.tick)}, Rows: map[string]*o.BillStack{}, Delta: since > 0}, bridge.Result{}, nil
}

// The mirrored section a review serves is the projection a whole read of
// the same tick gives (#795): across reviews that upsert, change and
// remove rows, refreshed by deltas and the occasional keyframe, the held
// section never drifts from the full read, and most refreshes are deltas.
func TestMirroredSectionMatchesWholeRead(t *testing.T) {
	ctx := context.Background()
	f := newClockFacts(nil, nil)
	scope := facts.Scope{Load: "load", Generation: 1}
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	native := &trackingNative{tick: 1, rows: map[string]*o.ZoneState{}, changed: map[string]int64{}, removed: map[string]int64{}}
	rng := rand.New(rand.NewSource(795))
	for review := 0; review < 300; review++ {
		native.tick += int64(1 + rng.Intn(600))
		for i := rng.Intn(4); i > 0; i-- {
			id := fmt.Sprintf("Zone_%d", rng.Intn(40))
			if rng.Intn(4) == 0 {
				if _, ok := native.rows[id]; ok {
					delete(native.rows, id)
					delete(native.changed, id)
					native.removed[id] = native.tick
				}
				continue
			}
			native.rows[id] = zoneState(id, fmt.Sprintf("label-%d", rng.Intn(6)))
			native.changed[id] = native.tick
			delete(native.removed, id)
		}
		refreshEntitySections(ctx, native, f, identity, scope, native.tick, entitySectionsCarried{})
		held, ok := facts.Get[EntitySection[*o.ZoneState]](f.store, facts.Zones)
		whole, _ := native.read(0)
		native.asks = native.asks[:len(native.asks)-1]
		if !ok || held.AsOf != native.tick {
			t.Fatalf("review %d: held=%v as of %d at tick %d", review, ok, held.AsOf, native.tick)
		}
		if drift := bridge.EntityDrift(held.Value, whole.Rows); drift != 0 {
			t.Fatalf("review %d: the mirrored section differs from the whole read by %d rows", review, drift)
		}
	}
	full := 0
	for _, since := range native.asks {
		if since == 0 {
			full++
		}
	}
	if full*3 > len(native.asks) {
		t.Fatalf("%d of %d reads were whole: the reviews did not refresh by delta", full, len(native.asks))
	}
}
