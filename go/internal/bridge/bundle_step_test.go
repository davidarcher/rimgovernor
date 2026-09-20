package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// bundleStepServer answers the bundle with every step family (#593) and
// the dedicated reads with the same rows, counting each.
type bundleStepServer struct {
	*bundleServer
	buildings *o.ListBuildingsReply
	built     *o.ListBuildingsReply
	bills     *o.BillsReply
	zones     *o.ListZonesReply
	traders   *o.TradersReply
	world     *o.WorldProgressionReply
	resources *o.ResourceSourcesReply
	cells     *o.GetCellsReply
}

var bundleStepTools = []string{"rimgovernor/observations_list_buildings", "rimgovernor/observations_read_bills", "rimgovernor/observations_list_zones", "rimgovernor/observations_list_traders", "rimgovernor/observations_read_world_progression", "rimgovernor/observations_list_resource_sources", "rimgovernor/observations_get_cells"}

var bundleStepRegion = policy.Rectangle{X: 10, Z: 20, Width: 3, Height: 2}

func newBundleStepServer(t *testing.T) *bundleStepServer {
	t.Helper()
	context := authorityTestContext(7)
	s := &bundleStepServer{bundleServer: newBundleServer()}
	for _, name := range bundleStepTools {
		s.calls[name] = &atomic.Int64{}
	}
	complete := func(n int) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(uint64(n)), Returned: proto.Uint64(uint64(n)), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	buildings := constructionTestSnapshot()
	buildings.Completeness = complete(1)
	s.buildings = &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: proto.Clone(buildings).(*o.BuildingsSnapshot)}}
	s.built = &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: proto.Clone(buildings).(*o.BuildingsSnapshot)}}
	s.bills = &o.BillsReply{Outcome: &o.BillsReply_Observed{Observed: &o.BillsSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Completeness: complete(0)}}}
	s.zones = &o.ListZonesReply{Outcome: &o.ListZonesReply_Observed{Observed: &o.ZonesSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Zones: []*o.ZoneState{{Id: proto.String("Zone_1"), FoodStorage: proto.Bool(true), Snapshot: &o.SnapshotRef{Context: proto.Clone(context).(*c.ObservationContext), EntityId: proto.String("Zone_1"), Token: proto.String("z")}}}, Completeness: complete(1)}}}
	s.traders = &o.TradersReply{Outcome: &o.TradersReply_Observed{Observed: &o.TradersSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Completeness: complete(0)}}}
	s.world = &o.WorldProgressionReply{Outcome: &o.WorldProgressionReply_Observed{Observed: worldProgressionFixture()}}
	s.resources = &o.ResourceSourcesReply{Outcome: &o.ResourceSourcesReply_Observed{Observed: &o.ResourceSourcesSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Resource: proto.String("Steel"), Storage: validResourceStorage(), Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}}}}}
	s.cells = &o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: windowSnapshot(planningBandRequest(pbIdentity(), bundleStepRegion, 0), nil)}}
	for _, message := range []proto.Message{s.buildings, s.built, s.bills, s.zones, s.traders, s.world, s.resources, s.cells} {
		retagContexts(message, context)
	}
	s.snapshot.Buildings = proto.Clone(s.buildings.GetObserved()).(*o.BuildingsSnapshot)
	s.snapshot.BuiltBuildings = proto.Clone(s.built.GetObserved()).(*o.BuildingsSnapshot)
	s.snapshot.Bills = proto.Clone(s.bills.GetObserved()).(*o.BillsSnapshot)
	s.snapshot.Zones = proto.Clone(s.zones.GetObserved()).(*o.ZonesSnapshot)
	s.snapshot.Traders = proto.Clone(s.traders.GetObserved()).(*o.TradersSnapshot)
	s.snapshot.WorldProgression = proto.Clone(s.world.GetObserved()).(*o.WorldProgressionSnapshot)
	s.snapshot.ResourceSources = []*o.ResourceSourcesSnapshot{proto.Clone(s.resources.GetObserved()).(*o.ResourceSourcesSnapshot)}
	s.snapshot.PlanningWindow = proto.Clone(s.cells.GetObserved()).(*o.CellsSnapshot)
	return s
}

func (s *bundleStepServer) handle(ctx context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
	switch arg.Tool {
	case "rimgovernor/observations_list_buildings":
		s.calls[arg.Tool].Add(1)
		var outer struct {
			Request string `json:"request"`
		}
		request := &o.ListBuildingsRequest{}
		if json.Unmarshal(arg.Arguments, &outer) != nil || protojson.Unmarshal([]byte(outer.Request), request) != nil {
			return nil, errors.New("undecodable buildings request")
		}
		if len(request.Statuses) > 0 {
			return pbResult(s.built), nil
		}
		return pbResult(s.buildings), nil
	case "rimgovernor/observations_read_bills":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.bills), nil
	case "rimgovernor/observations_list_zones":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.zones), nil
	case "rimgovernor/observations_list_traders":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.traders), nil
	case "rimgovernor/observations_read_world_progression":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.world), nil
	case "rimgovernor/observations_list_resource_sources":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.resources), nil
	case "rimgovernor/observations_get_cells":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.cells), nil
	}
	return s.bundleServer.handle(ctx, arg)
}

func bundleStepRequest() *o.BundleRequest {
	request := bundleTestRequest()
	request.Buildings, request.BuiltBuildings, request.Bills, request.Zones, request.Traders, request.WorldProgression = proto.Bool(true), proto.Bool(true), proto.Bool(true), proto.Bool(true), proto.Bool(true), proto.Bool(true)
	request.ResourceSources = []string{"Steel"}
	request.PlanningWindow = BundlePlanningWindowRequest(&BundlePlanningWindow{Region: bundleStepRegion})
	return request
}

// stepReads issues the reads the step families answer under ctx and
// reports how many crossed the bridge.
func (s *bundleStepServer) stepReads(t *testing.T, ctx context.Context, client *Client) int64 {
	t.Helper()
	before := s.stepCalls()
	if _, _, err := client.ReadBuildings(ctx, pbIdentity(), 0); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadConstructionBuildings(ctx, pbIdentity(), nil); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadBillStacks(ctx, pbIdentity(), 0); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadZoneSection(ctx, pbIdentity(), 0); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ListTraders(ctx, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadWorldProgression(ctx, pbIdentity(), false); err != nil {
		t.Fatal(err)
	}
	if _, _, _, err := client.ReadResourceSources(ctx, pbIdentity(), "Steel"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadPlanningWindow(ctx, pbIdentity(), bundleStepRegion, 0); err != nil {
		t.Fatal(err)
	}
	return s.stepCalls() - before
}

func (s *bundleStepServer) stepCalls() int64 {
	var n int64
	for _, name := range bundleStepTools {
		n += s.calls[name].Load()
	}
	return n
}

// TestBundleReadSeedsTheStepFamilies (#593): a bundle that carries the
// step families serves the entity refreshers' full reads, the built
// census, the traders, the world progression, each resource's sources
// and the planning window band from the step cache; families the native
// left out are read natively, exactly as before.
func TestBundleReadSeedsTheStepFamilies(t *testing.T) {
	server := newBundleStepServer(t)
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	ctx := WithStepReadCache(context.Background(), NewStepReadCache())
	reply, _, err := client.ReadBundle(ctx, bundleStepRequest())
	if err != nil || reply.GetObserved().GetBuildings() == nil || reply.GetObserved().GetPlanningWindow() == nil {
		t.Fatal(reply, err)
	}
	if n := server.stepReads(t, ctx, client); n != 0 {
		t.Fatal("seeded step families crossed the bridge", n)
	}
	// Another shape is another key: a delta, another resource, another
	// band, storage included.
	if _, _, err = client.ReadBuildings(ctx, pbIdentity(), 5); err != nil || server.calls["rimgovernor/observations_list_buildings"].Load() != 1 {
		t.Fatal(err, "delta served from the bundle")
	}
	server.resources.GetObserved().Resource = proto.String("Plasteel")
	server.resources.GetObserved().Storage.Resource = proto.String("Plasteel")
	if _, _, _, err = client.ReadResourceSources(ctx, pbIdentity(), "Plasteel"); err != nil || server.calls["rimgovernor/observations_list_resource_sources"].Load() != 1 {
		t.Fatal(err, "another resource served from the bundle")
	}
	if _, _, err = client.ReadWorldProgression(ctx, pbIdentity(), true); err != nil || server.calls["rimgovernor/observations_read_world_progression"].Load() != 1 {
		t.Fatal(err, "storage read served from the bundle")
	}
	// Families the native omitted leave the request satisfied and the
	// dedicated reads native.
	server = newBundleStepServer(t)
	server.snapshot.Buildings, server.snapshot.BuiltBuildings, server.snapshot.Bills, server.snapshot.Zones, server.snapshot.Traders, server.snapshot.WorldProgression, server.snapshot.ResourceSources, server.snapshot.PlanningWindow = nil, nil, nil, nil, nil, nil, nil, nil
	client = testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	ctx = WithStepReadCache(context.Background(), NewStepReadCache())
	if _, _, err = client.ReadBundle(ctx, bundleStepRequest()); err != nil {
		t.Fatal(err)
	}
	if n := server.stepReads(t, ctx, client); n != 8 {
		t.Fatal("omitted families served without a read", n)
	}
	// A planning window delta rides under the delta's key.
	server = newBundleStepServer(t)
	server.snapshot.PlanningWindow.AsOfTick = proto.Int64(server.snapshot.Context.GetTick())
	server.snapshot.PlanningWindow.Cells = nil
	server.snapshot.PlanningWindow.Completeness = &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(0), Returned: proto.Uint64(0), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	server.snapshot.PlanningWindow.Unchanged = proto.Uint32(6)
	client = testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	ctx = WithStepReadCache(context.Background(), NewStepReadCache())
	request := bundleStepRequest()
	request.PlanningWindow = BundlePlanningWindowRequest(&BundlePlanningWindow{Region: bundleStepRegion, Since: 5})
	if _, _, err = client.ReadBundle(ctx, request); err != nil {
		t.Fatal(err)
	}
	window, _, err := client.ReadPlanningWindow(ctx, pbIdentity(), bundleStepRegion, 5)
	if err != nil || !window.Delta || window.Unchanged != 6 || server.calls["rimgovernor/observations_get_cells"].Load() != 0 {
		t.Fatalf("%+v %v calls=%d", window, err, server.calls["rimgovernor/observations_get_cells"].Load())
	}
}

func TestBundleReadValidatesTheStepFamilies(t *testing.T) {
	cases := map[string]struct {
		request func(*o.BundleRequest)
		mutate  func(*o.BundleSnapshot)
	}{
		"buildings unrequested": {request: func(r *o.BundleRequest) { r.Buildings = nil }},
		"built unrequested":     {request: func(r *o.BundleRequest) { r.BuiltBuildings = proto.Bool(false) }},
		"bills unrequested":     {request: func(r *o.BundleRequest) { r.Bills = nil }},
		"zones unrequested":     {request: func(r *o.BundleRequest) { r.Zones = nil }},
		"traders unrequested":   {request: func(r *o.BundleRequest) { r.Traders = nil }},
		"world unrequested":     {request: func(r *o.BundleRequest) { r.WorldProgression = nil }},
		"resource unrequested":  {request: func(r *o.BundleRequest) { r.ResourceSources = []string{"Plasteel"} }},
		"window unrequested":    {request: func(r *o.BundleRequest) { r.PlanningWindow = nil }},
		"buildings tick":        {mutate: func(s *o.BundleSnapshot) { s.Buildings.Context.Tick = proto.Int64(13) }},
		"bills identity":        {mutate: func(s *o.BundleSnapshot) { s.Bills.Context.Identity.LoadToken = proto.String("other") }},
		"traders context":       {mutate: func(s *o.BundleSnapshot) { s.Traders.Context = nil }},
		"resource context":      {mutate: func(s *o.BundleSnapshot) { s.ResourceSources[0].Context.Tick = proto.Int64(13) }},
		"window tick":           {mutate: func(s *o.BundleSnapshot) { s.PlanningWindow.Context.Tick = proto.Int64(13) }},
	}
	for name, tc := range cases {
		server := newBundleStepServer(t)
		if tc.mutate != nil {
			tc.mutate(server.snapshot)
		}
		request := bundleStepRequest()
		if tc.request != nil {
			tc.request(request)
		}
		client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
		if reply, _, err := client.ReadBundle(context.Background(), request); !errors.Is(err, ErrContract) || reply != nil {
			t.Fatal(name, reply, err)
		}
	}
}

// TestStepAsksDecodesTheBundleShapes (#593): the reads a step asked
// through its cache, in the shapes the bundle answers, come back as the
// next bundle's asks; other shapes do not.
func TestStepAsksDecodesTheBundleShapes(t *testing.T) {
	server := newBundleStepServer(t)
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	cache := NewStepReadCache()
	ctx := WithStepReadCache(context.Background(), cache)
	if asks := cache.StepAsks(); !asks.Empty() {
		t.Fatal(asks)
	}
	if _, _, err := client.ReadConstructionBuildings(ctx, pbIdentity(), nil); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadConstructionBuildings(ctx, pbIdentity(), []string{"wall"}); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ListTraders(ctx, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadWorldProgression(ctx, pbIdentity(), true); err != nil {
		t.Fatal(err)
	}
	for _, resource := range []string{"Steel", "Steel"} {
		if _, _, _, err := client.ReadResourceSources(ctx, pbIdentity(), resource); err != nil {
			t.Fatal(err)
		}
	}
	wide := policy.Rectangle{X: 0, Z: 0, Width: 300, Height: 300}
	server.cells = &o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: windowSnapshot(planningBandRequest(pbIdentity(), policy.Rectangle{X: 0, Z: 0, Width: 300, Height: 218}, 0), nil)}}
	retagContexts(server.cells, authorityTestContext(7))
	if _, _, err := client.ReadPlanningWindow(ctx, pbIdentity(), wide, 0); err == nil {
		t.Fatal("a banded window read must not decode as one band")
	}
	server.cells = &o.GetCellsReply{Outcome: &o.GetCellsReply_Observed{Observed: windowSnapshot(planningBandRequest(pbIdentity(), bundleStepRegion, 0), nil)}}
	retagContexts(server.cells, authorityTestContext(7))
	if _, _, err := client.ReadPlanningWindow(ctx, pbIdentity(), bundleStepRegion, 0); err != nil {
		t.Fatal(err)
	}
	asks := cache.StepAsks()
	if !asks.BuiltBuildings || !asks.Traders || asks.WorldProgression || len(asks.Resources) != 1 || asks.Resources[0] != "Steel" || asks.PlanningWindow == nil || asks.PlanningWindow.Region != bundleStepRegion || asks.PlanningWindow.Since != 0 {
		t.Fatalf("%+v", asks)
	}
	// The asks name what the next bundle can carry, and a bundle carrying
	// them makes the same reads hits (the loop closes).
	request := bundleTestRequest()
	request.BuiltBuildings, request.Traders = proto.Bool(asks.BuiltBuildings), proto.Bool(asks.Traders)
	request.ResourceSources, request.PlanningWindow = asks.Resources, BundlePlanningWindowRequest(asks.PlanningWindow)
	server.snapshot.Buildings, server.snapshot.Bills, server.snapshot.Zones, server.snapshot.WorldProgression = nil, nil, nil, nil
	cache = NewStepReadCache()
	ctx = WithStepReadCache(context.Background(), cache)
	if _, _, err := client.ReadBundle(ctx, request); err != nil {
		t.Fatal(err)
	}
	before := server.stepCalls()
	if _, _, err := client.ReadConstructionBuildings(ctx, pbIdentity(), nil); err != nil {
		t.Fatal(err)
	}
	if _, _, _, err := client.ReadResourceSources(ctx, pbIdentity(), "Steel"); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadPlanningWindow(ctx, pbIdentity(), bundleStepRegion, 0); err != nil {
		t.Fatal(err)
	}
	if n := server.stepCalls() - before; n != 0 {
		t.Fatal("asked families crossed the bridge", n)
	}
	if next := cache.StepAsks(); !next.BuiltBuildings || len(next.Resources) != 1 || next.PlanningWindow == nil || next.Traders {
		t.Fatalf("%+v", next)
	}
}

// BundlePlanningWindowRequest carries a band the bundle can answer and
// declines a region wider than one band or outside the map.
func TestBundlePlanningWindowRequest(t *testing.T) {
	if BundlePlanningWindowRequest(nil) != nil || BundlePlanningWindowRequest(&BundlePlanningWindow{Region: policy.Rectangle{X: -1, Z: 0, Width: 2, Height: 2}}) != nil || BundlePlanningWindowRequest(&BundlePlanningWindow{Region: policy.Rectangle{Width: 300, Height: 300}}) != nil {
		t.Fatal("declined regions carried")
	}
	request := BundlePlanningWindowRequest(&BundlePlanningWindow{Region: bundleStepRegion, Since: 9})
	if request.GetRegion().GetMinimum().GetX() != 10 || request.GetRegion().GetMaximum().GetX() != 12 || request.GetRegion().GetMaximum().GetZ() != 21 || request.GetChangedSinceTick() != 9 {
		t.Fatal(request)
	}
	if BundlePlanningWindowRequest(&BundlePlanningWindow{Region: bundleStepRegion}).ChangedSinceTick != nil {
		t.Fatal("full read carries a since tick")
	}
}
