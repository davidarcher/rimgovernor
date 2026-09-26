package bridge

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// planningViewFixture is a published view of region under ctx: chunks of
// rows rows, each walkable and passable at glow 0.5 except the region's
// first cell, which is fogged. chunk i was captured at captured[i] under
// revision revisions[i] and validated at the publication tick.
func planningViewFixture(ctx *c.ObservationContext, region policy.Rectangle, rows int32, captured []int64, revisions []uint64) *o.PlanningWindowView {
	view := &o.PlanningWindowView{Context: proto.Clone(ctx).(*c.ObservationContext), MapSize: &o.MapSize{Width: proto.Uint32(250), Height: proto.Uint32(250)},
		Region:        &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(region.X), Z: proto.Int32(region.Z)}, Maximum: &c.Cell{X: proto.Int32(region.X + region.Width - 1), Z: proto.Int32(region.Z + region.Height - 1)}},
		AppliedFields: planningWindowFields(), Incarnation: proto.Uint64(3), Revision: proto.Uint64(9), PublishedTick: proto.Int64(ctx.GetTick()), Complete: proto.Bool(true)}
	for i, z := 0, region.Z; z < region.Z+region.Height; i, z = i+1, z+rows {
		maxZ := min(z+rows-1, region.Z+region.Height-1)
		cells := &o.CompactCells{Glow: []float64{0.5}}
		for row := z; row <= maxZ; row++ {
			var data []byte
			for x := region.X; x < region.X+region.Width; x++ {
				if x == region.X && row == region.Z {
					data = append(data, 2, 0)
					continue
				}
				data = append(data, 4|8, 0, 0)
			}
			cells.Rows = append(cells.Rows, data)
		}
		view.Chunks = append(view.Chunks, &o.PlanningWindowChunk{MinZ: proto.Int32(z), MaxZ: proto.Int32(maxZ), Revision: proto.Uint64(revisions[i]), CapturedTick: proto.Int64(captured[i]), ValidatedTick: proto.Int64(ctx.GetTick()), Cells: cells})
	}
	return view
}

var planningViewRegion = policy.Rectangle{X: 10, Z: 20, Width: 3, Height: 5}

func TestDecodePlanningWindowView(t *testing.T) {
	ctx := authorityTestContext(7)
	ctx.Tick = proto.Int64(1000)
	tick := ctx.GetTick()
	request := BundlePlanningWindowViewRequest(planningViewRegion)
	valid := func() *o.PlanningWindowView {
		return planningViewFixture(ctx, planningViewRegion, 2, []int64{tick - 40, tick, tick - 5}, []uint64{4, 9, 8})
	}
	view, err := DecodePlanningWindowView(valid(), request)
	if err != nil || len(view.Cells) != 14 || view.Filtered != 1 || len(view.Chunks) != 3 || view.Region != planningViewRegion {
		t.Fatalf("%+v %v", view, err)
	}
	// Every chunk was validated at publication; two were carried over.
	if view.Validated() != tick || view.Reused() != 2 || view.Chunks[0].Captured != tick-40 {
		t.Fatalf("validated=%d reused=%d chunks=%+v", view.Validated(), view.Reused(), view.Chunks)
	}
	stale := valid()
	stale.Chunks[0].ValidatedTick = proto.Int64(tick - 30)
	if view, err = DecodePlanningWindowView(stale, request); err != nil || view.Validated() != tick-30 {
		t.Fatalf("%+v %v", view, err)
	}
	// A root published frames before the serving hop, while a newer
	// capture runs (#654), is served with its own ages.
	earlier := valid()
	earlier.PublishedTick, earlier.Refreshing = proto.Int64(tick-5), proto.Bool(true)
	for _, chunk := range earlier.Chunks {
		chunk.ValidatedTick = proto.Int64(min(chunk.GetValidatedTick(), tick-5))
		chunk.CapturedTick = proto.Int64(min(chunk.GetCapturedTick(), tick-5))
	}
	if view, err = DecodePlanningWindowView(earlier, request); err != nil || !view.Refreshing || view.Validated() != tick-5 {
		t.Fatalf("earlier root: %+v %v", view, err)
	}
	pending := &o.PlanningWindowView{Context: valid().Context, Region: request.Region, AppliedFields: planningWindowFields(), Complete: proto.Bool(false), Pending: proto.String("capturing")}
	if _, err := DecodePlanningWindowView(pending, request); !errors.Is(err, ErrPlanningViewPending) || errors.Is(err, ErrContract) {
		t.Fatal("pending", err)
	}
	for name, mutate := range map[string]func(*o.PlanningWindowView){
		"region differs":          func(v *o.PlanningWindowView) { v.Region.Maximum.X = proto.Int32(13) },
		"mask differs":            func(v *o.PlanningWindowView) { v.AppliedFields.Terrain = proto.Bool(true) },
		"incomplete":              func(v *o.PlanningWindowView) { v.Complete = proto.Bool(false) },
		"no incarnation":          func(v *o.PlanningWindowView) { v.Incarnation = nil },
		"published after the hop": func(v *o.PlanningWindowView) { v.PublishedTick = proto.Int64(tick + 1) },
		"chunk gap":               func(v *o.PlanningWindowView) { v.Chunks = append(v.Chunks[:1], v.Chunks[2:]...) },
		"rows short":              func(v *o.PlanningWindowView) { v.Chunks = v.Chunks[:2] },
		"validated after publish": func(v *o.PlanningWindowView) { v.Chunks[1].ValidatedTick = proto.Int64(tick + 1) },
		"captured after validate": func(v *o.PlanningWindowView) { v.Chunks[0].CapturedTick = proto.Int64(tick + 1) },
		"chunk revision ahead":    func(v *o.PlanningWindowView) { v.Chunks[2].Revision = proto.Uint64(10) },
		"unread cell": func(v *o.PlanningWindowView) {
			v.Chunks[1].Cells.Rows[0] = append([]byte{1, 0}, v.Chunks[1].Cells.Rows[0][3:]...)
		},
		"chunk without cells": func(v *o.PlanningWindowView) { v.Chunks[1].Cells = nil },
	} {
		v := valid()
		mutate(v)
		if _, err := DecodePlanningWindowView(v, request); !errors.Is(err, ErrContract) {
			t.Error(name, err)
		}
	}
	if BundlePlanningWindowViewRequest(policy.Rectangle{Width: 65, Height: 64}) != nil {
		t.Fatal("a region past the view bound asked as a view")
	}
}

// TestBundleViewNeverSeedsTheBand (#650): the view rides only when asked,
// under the bundle's own context, and never fills the same-tick planning
// window band's cache key: that read still crosses the bridge.
func TestBundleViewNeverSeedsTheBand(t *testing.T) {
	server := newBundleStepServer(t)
	server.snapshot.PlanningWindow = nil
	ctx := server.snapshot.Context
	server.snapshot.PlanningWindowView = planningViewFixture(ctx, bundleStepRegion, 8, []int64{ctx.GetTick()}, []uint64{9})
	request := bundleStepRequest()
	request.PlanningWindow, request.PlanningWindowView = nil, BundlePlanningWindowViewRequest(bundleStepRegion)
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	step := WithStepReadCache(context.Background(), NewStepReadCache())
	reply, _, err := client.ReadBundle(step, request)
	if err != nil || reply.GetObserved().GetPlanningWindowView() == nil {
		t.Fatal(reply, err)
	}
	if _, err := DecodePlanningWindowView(reply.GetObserved().GetPlanningWindowView(), request.PlanningWindowView); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadPlanningWindow(step, pbIdentity(), bundleStepRegion, 0); err != nil || server.calls["rimgovernor/observations_get_cells"].Load() != 1 {
		t.Fatal("the view seeded the planning window band", err)
	}
	for name, mutate := range map[string]func(*o.BundleRequest, *o.BundleSnapshot){
		"view unrequested": func(r *o.BundleRequest, _ *o.BundleSnapshot) { r.PlanningWindowView = nil },
		"view tick":        func(_ *o.BundleRequest, s *o.BundleSnapshot) { s.PlanningWindowView.Context.Tick = proto.Int64(13) },
		"view identity": func(_ *o.BundleRequest, s *o.BundleSnapshot) {
			s.PlanningWindowView.Context.Identity.LoadToken = proto.String("other")
		},
	} {
		server := newBundleStepServer(t)
		server.snapshot.PlanningWindow = nil
		server.snapshot.PlanningWindowView = planningViewFixture(ctx, bundleStepRegion, 8, []int64{ctx.GetTick()}, []uint64{9})
		request := bundleStepRequest()
		request.PlanningWindow, request.PlanningWindowView = nil, BundlePlanningWindowViewRequest(bundleStepRegion)
		mutate(request, server.snapshot)
		client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
		if reply, _, err := client.ReadBundle(context.Background(), request); !errors.Is(err, ErrContract) || reply != nil {
			t.Error(name, reply, err)
		}
	}
}
