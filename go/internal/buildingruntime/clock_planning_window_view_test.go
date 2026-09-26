package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

var viewTestRegion = policy.Rectangle{X: 0, Z: 0, Width: 4, Height: 3}

func viewTestContext(tick int64) *c.ObservationContext {
	return &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}, Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(1)}
}

// viewTestBundle is a step bundle at tick carrying a planning window view
// of viewTestRegion in two chunks: rows 0-1 carried over from a capture
// at validated-40 (validated at validated), row 2 captured at tick.
func viewTestBundle(tick, validated int64) (*o.BundleRequest, *o.BundleSnapshot) {
	ctx := viewTestContext(tick)
	chunk := func(minZ, maxZ int32, revision uint64, captured, checked int64) *o.PlanningWindowChunk {
		cells := &o.CompactCells{Glow: []float64{0.25}}
		for z := minZ; z <= maxZ; z++ {
			var row []byte
			for range viewTestRegion.Width {
				row = append(row, 4|8, 0, 0)
			}
			cells.Rows = append(cells.Rows, row)
		}
		return &o.PlanningWindowChunk{MinZ: proto.Int32(minZ), MaxZ: proto.Int32(maxZ), Revision: proto.Uint64(revision), CapturedTick: proto.Int64(captured), ValidatedTick: proto.Int64(checked), Cells: cells}
	}
	request := &o.BundleRequest{PlanningWindowView: bridge.BundlePlanningWindowViewRequest(viewTestRegion)}
	view := &o.PlanningWindowView{Context: proto.Clone(ctx).(*c.ObservationContext), MapSize: &o.MapSize{Width: proto.Uint32(250), Height: proto.Uint32(250)},
		Region: proto.Clone(request.PlanningWindowView.Region).(*o.Rectangle), AppliedFields: &o.CellFields{Terrain: proto.Bool(false), Roof: proto.Bool(true), Visibility: proto.Bool(true), Traversal: proto.Bool(true), Zone: proto.Bool(true), Areas: proto.Bool(false), Things: proto.Bool(false), Designations: proto.Bool(false), Room: proto.Bool(true), Growth: proto.Bool(true)},
		Incarnation: proto.Uint64(2), Revision: proto.Uint64(7), PublishedTick: proto.Int64(tick), Complete: proto.Bool(true),
		Chunks: []*o.PlanningWindowChunk{chunk(0, 1, 5, validated-40, validated), chunk(2, 2, 7, tick, tick)}}
	return request, &o.BundleSnapshot{Context: ctx, PlanningWindowView: view}
}

// TestPlanningWindowViewFillsTheRefresher (#650): the refresher serves a
// decoded view the step's validity accepts without a native read, files
// it as of its oldest chunk validation, and never in the legacy band's
// shape; a stale, foreign or misplaced view is left unused and the window
// is read natively once, as it would be without one.
func TestPlanningWindowViewFillsTheRefresher(t *testing.T) {
	identity := viewTestContext(0).Identity
	scope := facts.Scope{Load: "load", Generation: 1}
	stepAt := func(tick int64, load string) context.Context {
		return domain.WithReadValidity(context.Background(), domain.ReadValidity{Scope: domain.ReadScope{Colony: "colony", Map: 0, Load: domain.LoadID(load), Native: 1}, Tick: domain.Tick(tick)})
	}
	refresher := func(tick, validated int64) (*planningWindow, *planningWindowFake) {
		store := facts.NewStore()
		facts.Put(store, scope, facts.PlanningCells, facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: viewTestRegion}, AsOf: 100, Complete: true, Source: "rimgovernor/observations_get_cells"})
		request, loaded := viewTestBundle(tick, validated)
		native := &planningWindowFake{tick: tick}
		return &planningWindow{native: native, store: store, scope: scope, tick: tick, review: true, view: decodePlanningWindowView(request, loaded)}, native
	}

	w, native := refresher(5000, 4900)
	if w.view == nil || w.view.Reused() != 1 {
		t.Fatalf("view not decoded: %+v", w.view)
	}
	held, err := w.PlanningWindow(stepAt(5000, "load"), identity, viewTestRegion)
	if err != nil || native.reads != 0 || held.AsOf != 4900 || held.Source != planningWindowViewSource || len(held.Value.Cells) != 12 {
		t.Fatalf("%+v %v reads=%d", held, err, native.reads)
	}
	if stored, ok := facts.Get[observation.PlanningCells](w.store, facts.PlanningCells); !ok || stored.AsOf != 4900 || stored.Source != planningWindowViewSource {
		t.Fatalf("stored = %+v", stored)
	}
	// A second ask in the step is the held window, not the view again.
	if _, err = w.PlanningWindow(stepAt(5000, "load"), identity, viewTestRegion); err != nil || native.reads != 0 || w.view != nil {
		t.Fatal(err, native.reads)
	}

	for name, tc := range map[string]struct {
		validated int64
		load      string
		region    policy.Rectangle
	}{
		"stale chunk":   {validated: 5000 - int64(domain.PlanningTickTolerance) - 1, load: "load", region: viewTestRegion},
		"another load":  {validated: 5000, load: "reloaded", region: viewTestRegion},
		"another place": {validated: 5000, load: "load", region: policy.Rectangle{X: 20, Z: 20, Width: 4, Height: 3}},
	} {
		w, native := refresher(5000, tc.validated)
		held, err := w.PlanningWindow(stepAt(5000, tc.load), identity, tc.region)
		if err != nil || native.reads != 1 || held.Source != "rimgovernor/observations_get_cells" || held.AsOf != 5000 {
			t.Errorf("%s: %+v %v reads=%d", name, held, err, native.reads)
		}
	}

	// A view that fails to decode is not offered at all.
	request, loaded := viewTestBundle(5000, 5000)
	loaded.PlanningWindowView.Complete = proto.Bool(false)
	if decodePlanningWindowView(request, loaded) != nil {
		t.Fatal("an incomplete view was offered")
	}
}

type viewRefusingNative struct {
	*schedulerNative
	requests []*o.BundleRequest
}

// ReadBundle answers as a native without the view: a request naming it is
// not valid ProtoJSON for the method.
func (n *viewRefusingNative) ReadBundle(ctx context.Context, req *o.BundleRequest) (*o.BundleReply, bridge.Result, error) {
	n.requests = append(n.requests, proto.Clone(req).(*o.BundleRequest))
	if req.PlanningWindowView != nil {
		return nil, bridge.Result{}, &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum(), Detail: proto.String("request is not valid ProtoJSON for this method.")}}
	}
	return n.schedulerNative.ReadBundle(ctx, req)
}

// TestReadStepBundleFallsBackToTheLegacyBand (#650): a native that refuses
// the view is asked once more, in the same step, for the legacy delta
// band, and never for the view again; other refusals are not a fallback.
func TestReadStepBundleFallsBackToTheLegacyBand(t *testing.T) {
	s, f := schedulerFixture(t)
	n := &viewRefusingNative{schedulerNative: f}
	s.native = n
	facts.Put(s.facts.store, facts.Scope{Load: "load"}, facts.PlanningCells, facts.Held[observation.PlanningCells]{Value: observation.PlanningCells{Region: viewTestRegion}, AsOf: 100, Complete: true})
	request := &o.BundleRequest{ClockStatus: proto.Bool(true), Emergency: proto.Bool(true), PlanningWindowView: s.planningWindowView()}
	if request.PlanningWindowView == nil {
		t.Fatal("a held window was not asked as a view")
	}
	reply, err := s.readStepBundle(context.Background(), request)
	if err != nil || reply.GetObserved() == nil || len(n.requests) != 2 {
		t.Fatal(reply, err, len(n.requests))
	}
	legacy := n.requests[1]
	if legacy.PlanningWindowView != nil || legacy.PlanningWindow.GetChangedSinceTick() != 100 || !s.facts.viewUnsupported || s.planningWindowView() != nil {
		t.Fatal("fallback", legacy, s.facts.viewUnsupported)
	}
	if viewRefused(errors.New("transport")) || viewRefused(&bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_IDENTITY.Enum()}}) ||
		viewRefused(&bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum(), Detail: proto.String("Bundle events require explicit cursor>=0.")}}) {
		t.Fatal("a refusal other than an invalid request fell back")
	}
}

// TestPlanningWindowViewServesAPannedWindow (#706): a planner that panned
// past the view's slack is served the view's overlapping rows plus one
// native read per uncovered strip, filed as of the oldest of them; a
// region the view does not overlap is read natively in full.
func TestPlanningWindowViewServesAPannedWindow(t *testing.T) {
	identity := viewTestContext(0).Identity
	scope := facts.Scope{Load: "load", Generation: 1}
	viewRegion := policy.Rectangle{X: 0, Z: 0, Width: 10, Height: 10}
	step := domain.WithReadValidity(context.Background(), domain.ReadValidity{Scope: domain.ReadScope{Colony: "colony", Map: 0, Load: "load", Native: 1}, Tick: 5000})
	refresher := func() (*planningWindow, *planningWindowFake) {
		view := &bridge.PlanningWindowView{Context: viewTestContext(5000), Region: viewRegion, Incarnation: 2, Revision: 7, PublishedTick: 5000,
			Chunks: []bridge.PlanningViewChunk{{MinZ: 0, MaxZ: 9, Revision: 5, Captured: 4800, Validated: 4900}}}
		for z := range viewRegion.Height {
			for x := range viewRegion.Width {
				view.Cells = append(view.Cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true)})
			}
		}
		native := &planningWindowFake{tick: 5000}
		return &planningWindow{native: native, store: facts.NewStore(), scope: scope, tick: 5000, review: true, view: view}, native
	}
	for name, tc := range map[string]struct {
		region     policy.Rectangle
		strips     []policy.Rectangle
		viewCells  int
		fullReads  int
		wantSource string
	}{
		"z pan":      {region: policy.Rectangle{X: 0, Z: 6, Width: 10, Height: 10}, strips: []policy.Rectangle{{X: 0, Z: 10, Width: 10, Height: 6}}, viewCells: 40, wantSource: planningWindowViewSource},
		"x pan":      {region: policy.Rectangle{X: -6, Z: 0, Width: 10, Height: 10}, strips: []policy.Rectangle{{X: -6, Z: 0, Width: 6, Height: 10}}, viewCells: 40, wantSource: planningWindowViewSource},
		"diagonal":   {region: policy.Rectangle{X: 6, Z: 6, Width: 10, Height: 10}, strips: []policy.Rectangle{{X: 6, Z: 10, Width: 10, Height: 6}, {X: 10, Z: 6, Width: 6, Height: 4}}, viewCells: 16, wantSource: planningWindowViewSource},
		"no overlap": {region: policy.Rectangle{X: 20, Z: 20, Width: 10, Height: 10}, fullReads: 1, wantSource: "rimgovernor/observations_get_cells"},
	} {
		w, native := refresher()
		held, err := w.PlanningWindow(step, identity, tc.region)
		if err != nil || held.Source != tc.wantSource || held.Value.Region != tc.region {
			t.Errorf("%s: %+v %v", name, held, err)
			continue
		}
		if tc.fullReads > 0 {
			if native.reads != tc.fullReads || native.region != tc.region || held.AsOf != 5000 {
				t.Errorf("%s: reads=%d region=%+v asOf=%d", name, native.reads, native.region, held.AsOf)
			}
			continue
		}
		strips := planningWindowUncovered(tc.region, func() policy.Rectangle { r, _ := planningWindowOverlap(viewRegion, tc.region); return r }())
		if native.reads != len(tc.strips) || len(strips) != len(tc.strips) || held.AsOf != 4900 || w.view != nil {
			t.Errorf("%s: reads=%d strips=%+v asOf=%d", name, native.reads, strips, held.AsOf)
			continue
		}
		covered := int32(0)
		for i, strip := range strips {
			if strip != tc.strips[i] {
				t.Errorf("%s: strip %d = %+v, want %+v", name, i, strip, tc.strips[i])
			}
			covered += strip.Width * strip.Height
		}
		// The strips and the view's rows tile the region exactly; the
		// fake answers one row per strip read.
		if int(covered)+tc.viewCells != int(tc.region.Width*tc.region.Height) || len(held.Value.Cells) != tc.viewCells+len(tc.strips) {
			t.Errorf("%s: %d view + %d read cells for %d", name, tc.viewCells, covered, tc.region.Width*tc.region.Height)
		}
		for _, row := range held.Value.Cells {
			if !planningRectContains(tc.region, row.Cell) {
				t.Errorf("%s: row %+v outside the region", name, row.Cell)
			}
		}
	}
}
