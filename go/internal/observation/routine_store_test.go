package observation

import (
	"context"
	"os"
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

// countingSource counts the research and population reads behind the
// research fake, so a served section is one the source never saw.
type countingSource struct {
	*researchSource
	research, population int
}

func (s *countingSource) ReadResearch(ctx context.Context, id *c.Identity) (bridge.ResearchRead, bridge.Result, error) {
	s.research++
	return s.researchSource.ReadResearch(ctx, id)
}

func (s *countingSource) ReadRoutinePopulation(ctx context.Context, id *c.Identity) (bridge.PrisonerCensus, bridge.Result, error) {
	s.population++
	return s.researchSource.ReadRoutinePopulation(ctx, id)
}

// TestRoutineReadingServesFreshSections (#360): a section the store holds
// fresh under its cadence is served without a native read and keeps its
// own as-of tick; one past its cadence, or past a policy's max age, is
// read again; a served section is not re-filed.
func TestRoutineReadingServesFreshSections(t *testing.T) {
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
	scope := facts.Scope{Load: "load", Generation: 1}
	held := policy.ResearchFacts{Current: "HeldProject", Projects: []policy.ResearchProjectID{"HeldProject"}}
	population := bridge.PrisonerCensus{Prisoners: domain.Known([]policy.PrisonerFacts{{Pawn: "prisoner-1"}}), Custody: domain.Known([]policy.CustodyFacts{})}

	for _, phase := range []struct {
		name            string
		researchAsOf    int64
		populationAsOf  int64
		maxAge          map[facts.Section]int64
		researchServed  bool
		populationServd bool
	}{
		{name: "fresh", researchAsOf: tick - 100, populationAsOf: tick - 100, researchServed: true, populationServd: true},
		{name: "research past its cadence", researchAsOf: tick - bridge.FactTickToleranceResearch - 1, populationAsOf: tick - bridge.FactTickToleranceColony, populationServd: true},
		{name: "population past the colony cadence", researchAsOf: tick, populationAsOf: tick - bridge.FactTickToleranceColony - 1, researchServed: true},
		{name: "policy max age", researchAsOf: tick - 100, populationAsOf: tick - 100, maxAge: map[facts.Section]int64{facts.Population: 50}, researchServed: true},
		{name: "ahead of the step", researchAsOf: tick + 1, populationAsOf: tick + 1},
	} {
		t.Run(phase.name, func(t *testing.T) {
			store := facts.NewStore()
			facts.Put(store, scope, facts.Research, facts.Held[policy.ResearchFacts]{Value: held, AsOf: phase.researchAsOf, Complete: true, Source: "rimgovernor/observations_read_research"})
			facts.Put(store, scope, facts.Population, facts.Held[bridge.PrisonerCensus]{Value: population, AsOf: phase.populationAsOf, Complete: true, Source: "rimgovernor/observations_read_population"})
			source := &countingSource{researchSource: &researchSource{projectSource: &projectSource{colonySource: &colonySource{reply: base}}, read: read}}
			ctx := WithRoutineStore(context.Background(), RoutineStore{Store: store, MaxAge: phase.maxAge})
			out, err := ObserveRoutine(ctx, source, testkit.NewManualClock(time.Now()), expected, time.Second)
			if err != nil {
				t.Fatal(err)
			}
			research, _ := out.Projection.Facts.Research.Value()
			if phase.researchServed {
				if source.research != 0 || research.Current != "HeldProject" || out.Sections.Research.AsOf != phase.researchAsOf || !out.Sections.Served[facts.Research] {
					t.Fatalf("research reads=%d current=%s as_of=%d served=%v", source.research, research.Current, out.Sections.Research.AsOf, out.Sections.Served)
				}
			} else if source.research != 1 || research.Current != "Electricity" || out.Sections.Research.AsOf != tick || out.Sections.Served[facts.Research] {
				t.Fatalf("research reads=%d current=%s as_of=%d served=%v", source.research, research.Current, out.Sections.Research.AsOf, out.Sections.Served)
			}
			prisoners, _ := out.Projection.Facts.Prisoners.Value()
			if phase.populationServd {
				if source.population != 0 || len(prisoners) != 1 || out.Sections.Population.AsOf != phase.populationAsOf || !out.Sections.Served[facts.Population] {
					t.Fatalf("population reads=%d prisoners=%d as_of=%d served=%v", source.population, len(prisoners), out.Sections.Population.AsOf, out.Sections.Served)
				}
			} else if source.population != 1 || len(prisoners) != 0 || out.Sections.Population.AsOf != tick || out.Sections.Served[facts.Population] {
				t.Fatalf("population reads=%d prisoners=%d as_of=%d served=%v", source.population, len(prisoners), out.Sections.Population.AsOf, out.Sections.Served)
			}
			// Filing leaves a served section's provenance alone and refiles a read one.
			before, _ := facts.Get[policy.ResearchFacts](store, facts.Research)
			out.Sections.File(store, scope)
			after, _ := facts.Get[policy.ResearchFacts](store, facts.Research)
			if phase.researchServed && (after.AsOf != before.AsOf || after.Value.Current != before.Value.Current) {
				t.Fatalf("served research refiled: %+v -> %+v", before, after)
			}
			if !phase.researchServed && (after.AsOf != tick || after.Value.Current != "Electricity") {
				t.Fatalf("read research not filed: %+v", after)
			}
		})
	}
}
