package observation

import (
	"context"
	"os"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
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
// read, each stamped with its own reply's tick and the method behind it;
// a section the source did not offer (pawns under an unknown colonist
// census, rooms with rooms off) is left out, and the store's as-of
// bookkeeping reports the spread between the sections' ticks.
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
	// The research reply describes a tick within tolerance but ahead of the bundle's.
	read := bridge.ResearchRead{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), CurrentProject: "Electricity", Projects: map[string]policy.ResearchProjectFacts{"Electricity": {}}}
	read.Context.Tick = proto.Int64(tick + 7)
	source := &researchSource{projectSource: &projectSource{colonySource: &colonySource{reply: base}}, read: read}
	out, err := ObserveRoutine(context.Background(), source, testkit.NewManualClock(time.Now()), expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	sections := out.Sections
	if sections.Colony.AsOf != tick || sections.Colony.Source != "rimgovernor/observations_read_colony_facts" || !sections.Colony.Complete {
		t.Fatalf("colony = %+v", sections.Colony)
	}
	if sections.PlanningCells.AsOf != tick || !reflect.DeepEqual(sections.PlanningCells.Value.Cells, out.Projection.Cells) || sections.PlanningCells.Value.Region != out.Projection.Region {
		t.Fatalf("planning cells = %+v", sections.PlanningCells)
	}
	if sections.Emergency.AsOf != tick || sections.Emergency.Source != "rimgovernor/observations_read_status" || sections.Emergency.Complete {
		t.Fatalf("emergency = %+v (an unknown colonist census is not complete)", sections.Emergency)
	}
	if sections.Population.AsOf != tick || sections.Population.Source != "rimgovernor/observations_read_population" || sections.Population.Complete {
		t.Fatalf("population = %+v (unknown custody is not complete)", sections.Population)
	}
	if sections.Research.AsOf != tick+7 || sections.Research.Source != "rimgovernor/observations_read_research" || sections.Research.Value.Current != "Electricity" {
		t.Fatalf("research = %+v", sections.Research)
	}
	if sections.Pawns.Source != "" || sections.Rooms.Source != "" {
		t.Fatalf("pawns=%+v rooms=%+v filed without a read", sections.Pawns, sections.Rooms)
	}
	asOf := sections.AsOf()
	want := map[facts.Section]int64{facts.Colony: tick, facts.PlanningCells: tick, facts.Emergency: tick, facts.Population: tick, facts.Research: tick + 7}
	if !reflect.DeepEqual(asOf, want) {
		t.Fatalf("as_of = %v", asOf)
	}
	if min, spread := facts.Spread(asOf); min != tick || spread != 7 {
		t.Fatalf("min=%d spread=%d", min, spread)
	}

	store := facts.NewStore()
	scope := facts.Scope{Load: "load", Generation: 1}
	sections.File(store, scope)
	if store.Len() != 5 {
		t.Fatalf("filed %d sections", store.Len())
	}
	held, ok := facts.Get[PlanningCells](store, facts.PlanningCells)
	if !ok || held.AsOf != tick || len(held.Value.Cells) != len(out.Projection.Cells) {
		t.Fatalf("stored planning cells = %+v ok=%v", held, ok)
	}
	if _, ok := facts.Get[RoutinePawns](store, facts.Pawns); ok {
		t.Fatal("pawns filed without a read")
	}
	if !store.Fresh(facts.Research, tick+7) || store.Fresh(facts.Research, tick) {
		t.Fatal("research freshness follows its own as-of, not the bundle's")
	}
	sections.File(nil, scope)
}
