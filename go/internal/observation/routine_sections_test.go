package observation

import (
	"context"
	"errors"
	"os"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// TestRoutineReadingSections: a reading files one section per census it
// read, each stamped with the frame tick and the method behind it;
// a section the source did not offer (pawns under an unknown colonist
// census, rooms with rooms off) is left out, and the store's as-of
// bookkeeping reports no spread: one frame has one tick (#884).
func TestRoutineReadingSections(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	base := &o.ColonyFactsReply{}
	if err := protojson.Unmarshal(data, base); err != nil {
		t.Fatal(err)
	}
	expected, err := DecodeIdentity(&l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Paused: proto.Bool(true)}}})
	if err != nil {
		t.Fatal(err)
	}
	tick := base.GetObserved().Context.GetTick()
	read := bridge.ResearchRead{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), CurrentProject: "Electricity", Projects: map[string]policy.ResearchProjectFacts{"Electricity": {}}}
	source := researchSource(base, read)
	source.frame.Population = &bridge.PrisonerCensus{Context: base.GetObserved().Context, Prisoners: domain.Known([]policy.PrisonerFacts{})}
	out, err := observeRoutineUnowned(context.Background(), source, testkit.NewManualClock(time.Now()), expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	sections := out.Sections
	if sections.Colony.AsOf != tick || sections.Colony.Source != "rimgovernor/observations_read_colony_facts" || !sections.Colony.Complete {
		t.Fatalf("colony = %+v", sections.Colony)
	}
	// Without a window source the planning cells section stays unfiled.
	if sections.PlanningCells.Source != "" {
		t.Fatalf("planning cells = %+v", sections.PlanningCells)
	}
	if sections.Emergency.AsOf != tick || sections.Emergency.Source != "rimgovernor/observations_read_status" || sections.Emergency.Complete {
		t.Fatalf("emergency = %+v (an unknown colonist census is not complete)", sections.Emergency)
	}
	if sections.Population.AsOf != tick || sections.Population.Source != "rimgovernor/observations_read_population" || sections.Population.Complete {
		t.Fatalf("population = %+v (unknown custody is not complete)", sections.Population)
	}
	if sections.Research.AsOf != tick || sections.Research.Source != "rimgovernor/observations_read_research" || sections.Research.Value.Current != "Electricity" {
		t.Fatalf("research = %+v", sections.Research)
	}
	if sections.Pawns.Source != "" || sections.Rooms.Source != "" {
		t.Fatalf("pawns=%+v rooms=%+v filed without a read", sections.Pawns, sections.Rooms)
	}
	store := facts.NewStore()
	scope := facts.Scope{Load: "load", Generation: 1}
	sections.File(store, scope)
	if store.Len() != 4 {
		t.Fatalf("filed %d sections", store.Len())
	}
	if _, ok := facts.Get[PlanningCells](store, facts.PlanningCells); ok {
		t.Fatal("planning cells filed without a window source")
	}
	if _, ok := facts.Get[RoutinePawns](store, facts.Pawns); ok {
		t.Fatal("pawns filed without a read")
	}
	if research, ok := facts.Get[policy.ResearchFacts](store, facts.Research); !ok || research.AsOf != tick {
		t.Fatal("research keeps the frame tick")
	}
	sections.File(nil, scope)
}

type windowSource struct {
	asks   []policy.Rectangle
	window facts.Held[PlanningCells]
	err    error
}

func (s *windowSource) PlanningWindow(_ context.Context, _ *c.Identity, region policy.Rectangle) (facts.Held[PlanningCells], error) {
	s.asks = append(s.asks, region)
	return s.window, s.err
}

type extentSource struct {
	windowSource
	extent policy.Rectangle
}

func (s *extentSource) PlanExtent(context.Context) (policy.Rectangle, error) { return s.extent, nil }

// TestRoutineReadingFillsPlanningWindowFromSource: a colony reply that
// observed planning facts without listing the cells (a native that serves
// the window through observations_get_cells, #356) takes its window from
// the source the context carries, asked for the centre +/- 22 rect clipped
// to the map, and files the section with the source's own tick and
// method; without a source the window stays empty.
func TestRoutineReadingFillsPlanningWindowFromSource(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	base := &o.ColonyFactsReply{}
	if err := protojson.Unmarshal(data, base); err != nil {
		t.Fatal(err)
	}
	expected, err := DecodeIdentity(&l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Paused: proto.Bool(true)}}})
	if err != nil {
		t.Fatal(err)
	}
	tick := base.GetObserved().Context.GetTick()
	cells := []policy.SiteCell{{Cell: domain.Cell{X: 1, Z: 2}, Walkable: domain.Known(true)}}
	source := &windowSource{window: facts.Held[PlanningCells]{Value: PlanningCells{Region: policy.Rectangle{X: 0, Z: 0, Width: 23, Height: 23}, Cells: cells}, AsOf: tick - 5, Complete: true, Source: "rimgovernor/observations_get_cells"}}
	ctx := WithPlanningWindow(context.Background(), source)
	out, err := observeRoutineUnowned(ctx, &projectSource{colonySource: &colonySource{reply: base}}, testkit.NewManualClock(time.Now()), expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if len(source.asks) != 1 || source.asks[0] != (policy.Rectangle{X: 0, Z: 0, Width: 23, Height: 23}) {
		t.Fatalf("asks = %v", source.asks)
	}
	if !reflect.DeepEqual(out.Projection.Cells, cells) || out.Projection.Region != source.window.Value.Region {
		t.Fatalf("projection window = %+v", out.Projection.Cells)
	}
	if got := out.Sections.PlanningCells; got.AsOf != tick-5 || got.Source != "rimgovernor/observations_get_cells" || !reflect.DeepEqual(got.Value.Cells, cells) {
		t.Fatalf("planning cells section = %+v", got)
	}
	// A source that knows the layout plan's extent widens the window to
	// take it in (#1282).
	planned := &extentSource{windowSource: windowSource{window: source.window}, extent: policy.Rectangle{X: 40, Z: 3, Width: 5, Height: 5}}
	if _, err := observeRoutineUnowned(WithPlanningWindow(context.Background(), planned), &projectSource{colonySource: &colonySource{reply: base}}, testkit.NewManualClock(time.Now()), expected, time.Second); err != nil {
		t.Fatal(err)
	}
	if len(planned.asks) != 1 || planned.asks[0] != (policy.Rectangle{X: 0, Z: 0, Width: 45, Height: 23}) {
		t.Fatalf("asks with a plan extent = %v", planned.asks)
	}
	// A source that fails the read fails the observation.
	source.err = bridge.ErrUnavailable
	if _, err := observeRoutineUnowned(ctx, &projectSource{colonySource: &colonySource{reply: base}}, testkit.NewManualClock(time.Now()), expected, time.Second); !errors.Is(err, bridge.ErrUnavailable) {
		t.Fatal(err)
	}
	// Without a source the window is empty and the section unfiled.
	out, err = observeRoutineUnowned(context.Background(), &projectSource{colonySource: &colonySource{reply: base}}, testkit.NewManualClock(time.Now()), expected, time.Second)
	if err != nil || out.Projection.Cells != nil || out.Sections.PlanningCells.Source != "" {
		t.Fatalf("%+v %v", out.Sections.PlanningCells, err)
	}
}
