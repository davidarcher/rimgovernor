package bridge

import (
	"context"
	"encoding/json"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

func bundleTestRequest() *o.BundleRequest {
	return &o.BundleRequest{ClockStatus: proto.Bool(true), Emergency: proto.Bool(true), Events: &o.BundleEventsRequest{AfterCursor: proto.Int64(0), Limit: proto.Uint32(128)}}
}

// bundleTestSnapshot is a full bundle at generation 7, tick 12: every
// section is the fixture the dedicated read's tests use.
func bundleTestSnapshot() *o.BundleSnapshot {
	emergency := emergencyFixture()
	emergency.Context = authorityTestContext(7)
	emergency.Colonists.Context = authorityTestContext(7)
	return &o.BundleSnapshot{Context: authorityTestContext(7), Paused: proto.Bool(false), ClockStatus: clockTestStatus(), Emergency: emergency, Events: clockEventPage(2)}
}

// bundleServer answers the bundle read with its snapshot and counts every
// native call by tool; the dedicated reads answer too, so a seeded step
// cache can be told apart from a round trip.
type bundleServer struct {
	snapshot *o.BundleSnapshot
	calls    map[string]*atomic.Int64
	request  *o.BundleRequest
}

func newBundleServer() *bundleServer {
	s := &bundleServer{snapshot: bundleTestSnapshot(), calls: map[string]*atomic.Int64{}}
	for _, name := range []string{bundleMethod, "rimgovernor/lifecycle_read_tick", "rimgovernor/observations_read_status", "rimgovernor/clock_read_status"} {
		s.calls[name] = &atomic.Int64{}
	}
	return s
}

func (s *bundleServer) handle(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
	if counter := s.calls[arg.Tool]; counter != nil {
		counter.Add(1)
	}
	var wrapper struct {
		Request string `json:"request"`
	}
	if err := json.Unmarshal(arg.Arguments, &wrapper); err != nil {
		return nil, err
	}
	switch arg.Tool {
	case bundleMethod:
		s.request = &o.BundleRequest{}
		if err := protojson.Unmarshal([]byte(wrapper.Request), s.request); err != nil {
			return nil, err
		}
		return pbResult(&o.BundleReply{Outcome: &o.BundleReply_Observed{Observed: s.snapshot}}), nil
	case "rimgovernor/lifecycle_read_tick":
		return pbResult(&l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: s.snapshot.Context, Paused: s.snapshot.Paused}}}), nil
	case "rimgovernor/observations_read_status":
		return pbResult(&o.StatusReply{Outcome: &o.StatusReply_Observed{Observed: s.snapshot.Emergency}}), nil
	case "rimgovernor/clock_read_status":
		return pbResult(&k.StatusReply{Outcome: &k.StatusReply_Status{Status: s.snapshot.ClockStatus}}), nil
	}
	return &mcp.CallToolResult{IsError: true}, nil
}

func TestBundleReadCarriesEverySectionOfOneTick(t *testing.T) {
	server := newBundleServer()
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	reply, raw, err := client.ReadBundle(context.Background(), bundleTestRequest())
	if err != nil || len(raw.Envelope) == 0 || !proto.Equal(server.request, bundleTestRequest()) {
		t.Fatal(reply, err, server.request)
	}
	observed := reply.GetObserved()
	if observed.GetContext().GetTick() != 12 || observed.GetPaused() || observed.GetClockStatus().GetRunning() == nil || len(observed.GetEvents().Events) != 2 {
		t.Fatal(observed)
	}
	emergency, err := BundleEmergency(observed)
	if err != nil || !proto.Equal(emergency.Context, authorityTestContext(7)) {
		t.Fatal(emergency, err)
	}
	if complete, known := emergency.Facts.ColonistsComplete.Value(); !complete || !known {
		t.Fatal("emergency section lost its completeness")
	}
	request := BundleEventsRequest(observed.Context.Identity, bundleTestRequest().Events)
	if !proto.Equal(request, clockEventsRequest()) {
		t.Fatal(request)
	}
	// A scoped bundle names the identity, and the sections the request left
	// out are absent.
	server.snapshot = &o.BundleSnapshot{Context: authorityTestContext(7), Paused: proto.Bool(true)}
	reply, _, err = client.ReadBundle(context.Background(), &o.BundleRequest{Scope: &o.ReadScope{ExpectedIdentity: pbIdentity()}})
	if err != nil || !reply.GetObserved().GetPaused() || reply.GetObserved().ClockStatus != nil || server.request.Scope == nil {
		t.Fatal(reply, err)
	}
	if _, err = BundleEmergency(reply.GetObserved()); err == nil {
		t.Fatal("emergency decoded from a bundle without the section")
	}
}

func TestBundleReadValidatesRequestAndSections(t *testing.T) {
	server := newBundleServer()
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	for name, request := range map[string]*o.BundleRequest{
		"nil":            nil,
		"scope identity": {Scope: &o.ReadScope{ExpectedIdentity: &c.Identity{ColonyId: proto.String("colony")}}},
		"events cursor":  {Events: &o.BundleEventsRequest{AfterCursor: proto.Int64(-1), Limit: proto.Uint32(1)}},
		"events limit":   {Events: &o.BundleEventsRequest{AfterCursor: proto.Int64(0), Limit: proto.Uint32(129)}},
		"events wait":    {Events: &o.BundleEventsRequest{AfterCursor: proto.Int64(0), Limit: proto.Uint32(1), WaitMs: proto.Uint32(ClockEventsMaxWaitMs + 1)}},
	} {
		if _, _, err := client.ReadBundle(context.Background(), request); !errors.Is(err, ErrContract) {
			t.Fatal(name, err)
		}
	}
	if server.calls[bundleMethod].Load() != 0 {
		t.Fatal("an invalid request crossed the bridge")
	}
	cases := map[string]func(*o.BundleSnapshot){
		"pause missing":         func(s *o.BundleSnapshot) { s.Paused = nil },
		"section unrequested":   func(s *o.BundleSnapshot) { s.Events = nil },
		"clock status tick":     func(s *o.BundleSnapshot) { s.ClockStatus.Context.Tick = proto.Int64(13) },
		"clock status identity": func(s *o.BundleSnapshot) { s.ClockStatus.Context.Identity.LoadToken = proto.String("other") },
		"emergency tick": func(s *o.BundleSnapshot) {
			s.Emergency.Context.Tick = proto.Int64(13)
			s.Emergency.Colonists.Context.Tick = proto.Int64(13)
		},
		"emergency identity":       func(s *o.BundleSnapshot) { s.Emergency.Context.Identity.LoadToken = proto.String("other") },
		"emergency section":        func(s *o.BundleSnapshot) { s.Emergency.Threats = nil },
		"events tick":              func(s *o.BundleSnapshot) { s.Events.Context.Tick = proto.Int64(13) },
		"events page":              func(s *o.BundleSnapshot) { s.Events.NextCursor = nil },
		"scope identity mismatch":  func(s *o.BundleSnapshot) { s.Context.Identity.LoadToken = proto.String("other") },
		"context invalid":          func(s *o.BundleSnapshot) { s.Context.Tick = proto.Int64(-1) },
		"clock status unavailable": func(s *o.BundleSnapshot) { s.ClockStatus.State = nil },
	}
	for name, mutate := range cases {
		server.snapshot = bundleTestSnapshot()
		mutate(server.snapshot)
		request := bundleTestRequest()
		request.Scope = &o.ReadScope{ExpectedIdentity: pbIdentity()}
		reply, _, err := client.ReadBundle(context.Background(), request)
		if err == nil || reply != nil {
			t.Fatal(name, reply, err)
		}
	}
	// A clock status the native reports unavailable is the same refusal
	// ReadClockStatus reports, with the reply kept for the raw result.
	server.snapshot = bundleTestSnapshot()
	server.snapshot.ClockStatus = &k.Status{Context: authorityTestContext(7), State: &k.Status_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum()}}}
	if reply, _, err := client.ReadBundle(context.Background(), bundleTestRequest()); !errors.Is(err, ErrUnavailable) || reply == nil {
		t.Fatal(reply, err)
	}
	for _, outcome := range []*o.BundleReply{
		{Outcome: &o.BundleReply_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum()}}},
		{Outcome: &o.BundleReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_IDENTITY.Enum()}}},
		{},
	} {
		refusing := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) { return pbResult(outcome), nil }}, time.Second)
		if _, _, err := refusing.ReadBundle(context.Background(), bundleTestRequest()); err == nil {
			t.Fatal(outcome)
		}
	}
}

// TestBundleReadSeedsTheStepCache: the bundle itself is never memoized, but
// its tick and emergency sections serve the step's Tick and ReadEmergency
// reads and the parent's identity and emergency families, which their
// invalidation drops as if the dedicated reads had filled them.
func TestBundleReadSeedsTheStepCache(t *testing.T) {
	server := newBundleServer()
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	parent := NewFactCache()
	cache := NewChildReadCache(parent)
	ctx := WithStepReadCache(context.Background(), cache)
	for i := 0; i < 2; i++ {
		if _, _, err := client.ReadBundle(ctx, bundleTestRequest()); err != nil {
			t.Fatal(err)
		}
	}
	if server.calls[bundleMethod].Load() != 2 {
		t.Fatal("bundle memoized", server.calls[bundleMethod].Load())
	}
	tick, _, err := client.Tick(ctx)
	if err != nil || tick.GetLoaded().GetContext().GetTick() != 12 || tick.GetLoaded().GetPaused() {
		t.Fatal(tick, err)
	}
	emergency, _, err := client.ReadEmergency(ctx, pbIdentity())
	if err != nil || emergency.Context.GetTick() != 12 {
		t.Fatal(emergency, err)
	}
	if _, _, err = client.ReadClockStatus(ctx, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if n := server.calls["rimgovernor/lifecycle_read_tick"].Load() + server.calls["rimgovernor/observations_read_status"].Load(); n != 0 {
		t.Fatal("seeded sections crossed the bridge", n)
	}
	if server.calls["rimgovernor/clock_read_status"].Load() != 1 {
		t.Fatal("live clock status served from the bundle")
	}
	if stats := cache.Stats(); stats.Hits != 2 {
		t.Fatalf("%+v", stats)
	}
	if parent.Len() != 2 {
		t.Fatal(parent.Len())
	}
	// A later step at the same scope fixes its scope natively (a bundle in
	// the scheduler, the tick here) and then reads the seeded emergency
	// row from the parent; the emergency family's invalidation drops it.
	step := func(t *testing.T) {
		cache = NewChildReadCache(parent)
		ctx = WithStepReadCache(context.Background(), cache)
		if _, _, err = client.Tick(ctx); err != nil {
			t.Fatal(err)
		}
		if _, _, err = client.ReadEmergency(ctx, pbIdentity()); err != nil {
			t.Fatal(err)
		}
	}
	step(t)
	if tick, status := server.calls["rimgovernor/lifecycle_read_tick"].Load(), server.calls["rimgovernor/observations_read_status"].Load(); tick != 1 || status != 0 {
		t.Fatal(tick, status)
	}
	parent.InvalidateFamilies(FactEmergency)
	step(t)
	if tick, status := server.calls["rimgovernor/lifecycle_read_tick"].Load(), server.calls["rimgovernor/observations_read_status"].Load(); tick != 2 || status != 1 {
		t.Fatal(tick, status)
	}
	// Without a step cache the bundle seeds nothing.
	if _, _, err = client.ReadBundle(context.Background(), bundleTestRequest()); err != nil {
		t.Fatal(err)
	}
	if _, _, err = client.Tick(context.Background()); err != nil || server.calls["rimgovernor/lifecycle_read_tick"].Load() != 3 {
		t.Fatal(err, "tick served without a native read")
	}
}

// retagContexts sets every ObservationContext inside message, at any depth,
// to context, so a fixture read under another tick joins a bundle's.
func retagContexts(message proto.Message, context *c.ObservationContext) {
	var walk func(m protoreflect.Message)
	walk = func(m protoreflect.Message) {
		m.Range(func(fd protoreflect.FieldDescriptor, v protoreflect.Value) bool {
			if fd.Kind() != protoreflect.MessageKind || fd.IsMap() {
				return true
			}
			if fd.IsList() {
				for i := 0; i < v.List().Len(); i++ {
					walk(v.List().Get(i).Message())
				}
				return true
			}
			if _, ok := v.Message().Interface().(*c.ObservationContext); ok {
				m.Set(fd, protoreflect.ValueOfMessage(proto.Clone(context).ProtoReflect()))
				return true
			}
			walk(v.Message())
			return true
		})
	}
	walk(message.ProtoReflect())
}

// bundleFamilyServer answers the bundle with every census family and the
// dedicated family reads with the same rows, counting each.
type bundleFamilyServer struct {
	*bundleServer
	colony     *o.ColonyFactsReply
	population *o.PopulationReply
	research   *o.ResearchReply
	pawns      *o.ListPawnsReply
}

var bundleFamilyTools = []string{"rimgovernor/observations_read_colony_facts", "rimgovernor/observations_read_population", "rimgovernor/observations_read_research", "rimgovernor/observations_list_pawns"}

func newBundleFamilyServer(t *testing.T) *bundleFamilyServer {
	t.Helper()
	context := authorityTestContext(7)
	s := &bundleFamilyServer{bundleServer: newBundleServer()}
	for _, name := range bundleFamilyTools {
		s.calls[name] = &atomic.Int64{}
	}
	s.colony = colonyFixture(t)
	retagContexts(s.colony, context)
	s.population = populationReply(prisonerPerson("prisoner-1", ""))
	retagContexts(s.population, context)
	s.research = &o.ResearchReply{Outcome: &o.ResearchReply_Observed{Observed: &o.ResearchSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Snapshot: &o.SnapshotRef{Token: proto.String("research-token")}}}}
	pawns := combatPawnsFixture()
	pawns.Pawns[0].Settings = &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(false), Work: []*o.WorkSetting{{DefName: proto.String("Construction"), Priority: proto.Int32(3), Disabled: proto.Bool(false)}}}
	s.pawns = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: pawns}}
	s.snapshot.Emergency.Colonists.Pawns = []*o.PawnState{emergencyRow("pawn-1")}
	s.snapshot.Emergency.Colonists.Completeness = emergencyCounts(1)
	s.snapshot.ColonyFacts = proto.Clone(s.colony.GetObserved()).(*o.ColonyFactsSnapshot)
	s.snapshot.Population = proto.Clone(s.population.GetObserved()).(*o.PopulationSnapshot)
	s.snapshot.Research = proto.Clone(s.research.GetObserved()).(*o.ResearchSnapshot)
	s.snapshot.ColonistPawns = proto.Clone(pawns).(*o.PawnSnapshot)
	return s
}

func (s *bundleFamilyServer) handle(ctx context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
	switch arg.Tool {
	case "rimgovernor/observations_read_colony_facts":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.colony), nil
	case "rimgovernor/observations_read_population":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.population), nil
	case "rimgovernor/observations_read_research":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.research), nil
	case "rimgovernor/observations_list_pawns":
		s.calls[arg.Tool].Add(1)
		return pbResult(s.pawns), nil
	}
	return s.bundleServer.handle(ctx, arg)
}

func bundleFamilyRequest() *o.BundleRequest {
	request := bundleTestRequest()
	request.ColonyFacts, request.Population, request.Research, request.ColonistPawns = proto.Bool(true), proto.Bool(true), proto.Bool(true), proto.Bool(true)
	return request
}

// familyReads issues the routine census's family reads under ctx and
// reports how many crossed the bridge.
func (s *bundleFamilyServer) familyReads(t *testing.T, ctx context.Context, client *Client) int64 {
	t.Helper()
	before := s.familyCalls()
	if _, _, err := client.ReadColonyFacts(ctx, pbIdentity(), true, nil); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadRoutinePopulation(ctx, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadResearch(ctx, pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := client.ReadRoutinePawns(ctx, pbIdentity(), []string{"pawn-1"}); err != nil {
		t.Fatal(err)
	}
	return s.familyCalls() - before
}

func (s *bundleFamilyServer) familyCalls() int64 {
	var n int64
	for _, name := range bundleFamilyTools {
		n += s.calls[name].Load()
	}
	return n
}

// TestBundleReadSeedsTheCensusFamilies: a bundle that carries the census
// families serves the routine census's colony facts, population, research
// and colonist pawn reads from the step cache (issue #180); a family the
// native left out is read natively, exactly as before.
func TestBundleReadSeedsTheCensusFamilies(t *testing.T) {
	server := newBundleFamilyServer(t)
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	parent := NewFactCache()
	ctx := WithStepReadCache(context.Background(), NewChildReadCache(parent))
	reply, _, err := client.ReadBundle(ctx, bundleFamilyRequest())
	if err != nil || reply.GetObserved().GetColonyFacts() == nil || reply.GetObserved().GetColonistPawns() == nil {
		t.Fatal(reply, err)
	}
	if n := server.familyReads(t, ctx, client); n != 0 {
		t.Fatal("seeded families crossed the bridge", n)
	}
	if parent.Len() != 6 {
		t.Fatal(parent.Len())
	}
	// A planning read with definitions is another key and still goes natively.
	if _, _, err = client.ReadColonyFacts(ctx, pbIdentity(), true, []string{"Wall"}); err != nil || server.calls["rimgovernor/observations_read_colony_facts"].Load() != 1 {
		t.Fatal(err, "definition read served from the bundle")
	}
	// Families the native omitted (best effort) leave the request satisfied
	// and the dedicated reads native.
	server.snapshot.ColonyFacts, server.snapshot.Population, server.snapshot.Research, server.snapshot.ColonistPawns = nil, nil, nil, nil
	ctx = WithStepReadCache(context.Background(), NewStepReadCache())
	if _, _, err = client.ReadBundle(ctx, bundleFamilyRequest()); err != nil {
		t.Fatal(err)
	}
	if n := server.familyReads(t, ctx, client); n != 4 {
		t.Fatal("omitted families served without a read", n)
	}
	// An incomplete colonist census seeds no pawn rows: the bracket reads
	// none, and a read of any ids goes natively.
	server = newBundleFamilyServer(t)
	server.snapshot.Emergency.Colonists.Completeness.Page.Complete = proto.Bool(false)
	client = testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	ctx = WithStepReadCache(context.Background(), NewStepReadCache())
	if _, _, err = client.ReadBundle(ctx, bundleFamilyRequest()); err != nil {
		t.Fatal(err)
	}
	if n := server.familyReads(t, ctx, client); n != 1 || server.calls["rimgovernor/observations_list_pawns"].Load() != 1 {
		t.Fatal("pawns seeded from an incomplete census", n)
	}
}

func TestBundleReadValidatesTheCensusFamilies(t *testing.T) {
	server := newBundleFamilyServer(t)
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	if _, _, err := client.ReadBundle(context.Background(), &o.BundleRequest{ColonistPawns: proto.Bool(true)}); !errors.Is(err, ErrContract) || server.calls[bundleMethod].Load() != 0 {
		t.Fatal("colonist pawns without the emergency section", err)
	}
	cases := map[string]struct {
		request func(*o.BundleRequest)
		mutate  func(*o.BundleSnapshot)
	}{
		"colony unrequested":     {request: func(r *o.BundleRequest) { r.ColonyFacts = nil }},
		"population unrequested": {request: func(r *o.BundleRequest) { r.Population = proto.Bool(false) }},
		"research unrequested":   {request: func(r *o.BundleRequest) { r.Research = nil }},
		"pawns unrequested":      {request: func(r *o.BundleRequest) { r.ColonistPawns = nil }},
		"colony tick":            {mutate: func(s *o.BundleSnapshot) { s.ColonyFacts.Context.Tick = proto.Int64(13) }},
		"population identity":    {mutate: func(s *o.BundleSnapshot) { s.Population.Context.Identity.LoadToken = proto.String("other") }},
		"research context":       {mutate: func(s *o.BundleSnapshot) { s.Research.Context = nil }},
		"pawns tick":             {mutate: func(s *o.BundleSnapshot) { s.ColonistPawns.Context.Tick = proto.Int64(13) }},
	}
	for name, tc := range cases {
		server := newBundleFamilyServer(t)
		if tc.mutate != nil {
			tc.mutate(server.snapshot)
		}
		request := bundleFamilyRequest()
		if tc.request != nil {
			tc.request(request)
		}
		client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
		if reply, _, err := client.ReadBundle(context.Background(), request); !errors.Is(err, ErrContract) || reply != nil {
			t.Fatal(name, reply, err)
		}
	}
}

// TestBundleReadAcceptsMaskedFamilies (#360): a bundle answered under the
// review's field masks, the native having dropped the sub-blocks the
// masks leave out, seeds the same census reads as a whole one; the masks
// ride the request the native sees.
func TestBundleReadAcceptsMaskedFamilies(t *testing.T) {
	server := newBundleFamilyServer(t)
	for _, row := range server.snapshot.ColonistPawns.Pawns {
		row.Health.Capacities, row.Health.SurgeryBills = nil, nil
		if row.Equipment != nil {
			row.Equipment.InventoryWeapons, row.Equipment.InventoryItemCount, row.Equipment.CarriedThingId = nil, nil, nil
			for _, list := range [][]*o.GearItem{row.Equipment.Equipped, row.Equipment.Apparel} {
				for _, g := range list {
					g.Stuff, g.Quality, g.HitPoints, g.MaxHitPoints, g.ApparelLayers, g.ArmorSharp, g.ArmorBlunt, g.InsulationCold, g.InsulationHeat = nil, nil, nil, nil, nil, nil, nil, nil, nil
				}
			}
		}
		if row.Social != nil {
			row.Social.Relations = nil
		}
	}
	for _, person := range server.snapshot.Population.Persons {
		person.OwnedBed, person.NutritionPerDay = nil, nil
	}
	server.snapshot.Population.SupportedInteractions = nil
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	ctx := WithStepReadCache(context.Background(), NewStepReadCache())
	request := bundleFamilyRequest()
	request.ColonistPawnFields, request.PopulationFields, request.ResearchFields = routinePawnMask(), &o.PopulationFields{}, &o.ResearchFields{}
	if _, _, err := client.ReadBundle(ctx, request); err != nil {
		t.Fatal(err)
	}
	if seen := server.request; seen.ColonistPawnFields == nil || seen.PopulationFields == nil || seen.ResearchFields == nil {
		t.Fatal("masks did not reach the native", seen)
	}
	if n := server.familyReads(t, ctx, client); n != 0 {
		t.Fatal("masked families crossed the bridge", n)
	}
}

// routinePawnMask is buildingruntime's bundlePawnMask: the routine bundle keeps
// traits and backstory because the work profile reads them (#695).
func routinePawnMask() *o.PawnFields {
	return &o.PawnFields{IncludeTraits: proto.Bool(true), IncludeBackstory: proto.Bool(true)}
}

// TestBundleSlimPawnMaskDoesNotSeedRoutinePawns (#695): a pawn mask that strips
// traits and backstory must not stand in for ReadRoutinePawns, whose work
// profile would read the stripped rows as colonists with no traits and no age.
func TestBundleSlimPawnMaskDoesNotSeedRoutinePawns(t *testing.T) {
	server := newBundleFamilyServer(t)
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	ctx := WithStepReadCache(context.Background(), NewStepReadCache())
	request := bundleFamilyRequest()
	request.ColonistPawnFields = &o.PawnFields{}
	if _, _, err := client.ReadBundle(ctx, request); err != nil {
		t.Fatal(err)
	}
	if n := server.familyReads(t, ctx, client); n == 0 {
		t.Fatal("a traitless pawn bundle served the routine pawn read")
	}
}

// TestBundleMaskServesOnlyItsConsumers (#648): a masked family seeds a
// dedicated read's key only when the mask carries every block that key's
// consumers decode, so a slim copy never passes for detail it omits.
func TestBundleMaskServesOnlyItsConsumers(t *testing.T) {
	for _, tc := range []struct {
		name       string
		mask, need proto.Message
		want       bool
	}{
		{"absent mask is whole", (*o.PawnFields)(nil), &o.PawnFields{IncludeTraits: proto.Bool(true)}, true},
		{"slim serves no need", &o.PawnFields{}, &o.PawnFields{}, true},
		{"slim lacks a needed block", &o.PawnFields{}, &o.PawnFields{IncludeTraits: proto.Bool(true)}, false},
		{"explicit false lacks it", &o.PopulationFields{IncludeOwnedBed: proto.Bool(false)}, &o.PopulationFields{IncludeOwnedBed: proto.Bool(true)}, false},
		{"selective bit serves it", &o.ResearchFields{IncludeCosts: proto.Bool(true)}, &o.ResearchFields{IncludeCosts: proto.Bool(true)}, true},
		{"other bit does not", &o.ResearchFields{IncludeUnlocks: proto.Bool(true)}, &o.ResearchFields{IncludeCosts: proto.Bool(true)}, false},
	} {
		if got := maskServes(tc.mask, tc.need); got != tc.want {
			t.Errorf("%s: maskServes = %v, want %v", tc.name, got, tc.want)
		}
	}
	// The routine bundle's masks (bundleMasks in buildingruntime) serve every
	// seeded key; TestBundleReadAcceptsMaskedFamilies proves the hit.
	for _, pair := range [][2]proto.Message{{routinePawnMask(), seededPawnFields}, {&o.PopulationFields{}, seededPopulationFields}, {&o.ResearchFields{}, seededResearchFields}} {
		if !maskServes(pair[0], pair[1]) {
			t.Fatal("the routine bundle mask no longer serves a seeded key; the routine bundle would read twice", pair[1])
		}
	}
}
