package observation

import (
	"context"
	"errors"
	"os"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// colonySource serves one colony facts reply. Its identity read is the
// planner's own entry observation; ObserveColony must never issue it.
type colonySource struct {
	reply      *o.ColonyFactsReply
	onRead     func()
	reads      int
	identities int
}

func (s *colonySource) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	s.identities++
	return nil, bridge.Result{}, errors.New("identity read inside ObserveColony")
}

func (s *colonySource) ReadColonyFacts(ctx context.Context, id *c.Identity, planning bool, definitions []string) (*o.ColonyFactsReply, bridge.Result, error) {
	s.reads++
	if s.onRead != nil {
		s.onRead()
	}
	return s.reply, bridge.Result{}, ctx.Err()
}

func TestObserveColonyValidatesFactsByTheirContext(t *testing.T) {
	for _, scenario := range []struct {
		name   string
		change func(*colonySource, *Identity, *testkit.ManualClock, context.CancelFunc)
		want   error
	}{
		{"stable", nil, nil},
		// A running clock is no longer a hold (#243): the facts bind to the
		// expected tick and may trail it within the planning tolerance.
		{"expected running", func(_ *colonySource, i *Identity, _ *testkit.ManualClock, _ context.CancelFunc) {
			i.Paused = domain.Known(false)
		}, nil},
		{"expected pause unknown", func(_ *colonySource, i *Identity, _ *testkit.ManualClock, _ context.CancelFunc) {
			i.Paused = domain.Unknown[bool]()
		}, nil},
		// The facts may come from the step's fact cache behind the expected
		// tick by their family's tolerance, never further (#306).
		{"facts behind within family tolerance", func(_ *colonySource, i *Identity, _ *testkit.ManualClock, _ context.CancelFunc) {
			i.Tick = 7 + domain.Tick(bridge.FactColony.TickTolerance())
		}, nil},
		{"facts behind past family tolerance", func(_ *colonySource, i *Identity, _ *testkit.ManualClock, _ context.CancelFunc) {
			i.Tick = 8 + domain.Tick(bridge.FactColony.TickTolerance())
		}, ErrChanged},
		{"facts within tolerance", func(s *colonySource, i *Identity, _ *testkit.ManualClock, _ context.CancelFunc) {
			s.reply.GetObserved().Context.Tick = proto.Int64(int64(i.Tick + domain.PlanningTickTolerance))
			s.reply.GetObserved().Planning.GetObserved().Cells.Context.Tick = proto.Int64(int64(i.Tick + domain.PlanningTickTolerance))
		}, nil},
		{"facts past tolerance", func(s *colonySource, i *Identity, _ *testkit.ManualClock, _ context.CancelFunc) {
			s.reply.GetObserved().Context.Tick = proto.Int64(int64(i.Tick + domain.PlanningTickTolerance + 1))
			s.reply.GetObserved().Planning.GetObserved().Cells.Context.Tick = proto.Int64(int64(i.Tick + domain.PlanningTickTolerance + 1))
		}, ErrChanged},
		{"load changed", func(s *colonySource, _ *Identity, _ *testkit.ManualClock, _ context.CancelFunc) {
			s.reply.GetObserved().Context.Identity.LoadToken = proto.String("new")
		}, bridge.ErrContract},
		{"generation changed", func(_ *colonySource, i *Identity, _ *testkit.ManualClock, _ context.CancelFunc) {
			i.NativeGeneration = domain.Known(domain.NativeGeneration(2))
		}, ErrChanged},
		{"generation missing", func(s *colonySource, _ *Identity, _ *testkit.ManualClock, _ context.CancelFunc) {
			s.reply.GetObserved().Context.NativeGeneration = nil
			s.reply.GetObserved().Planning.GetObserved().Cells.Context.NativeGeneration = nil
		}, ErrChanged},
		{"slow read", func(s *colonySource, _ *Identity, clock *testkit.ManualClock, _ context.CancelFunc) {
			s.onRead = func() { clock.Advance(2 * time.Second) }
		}, ErrStale},
		{"clock rewound", func(s *colonySource, _ *Identity, clock *testkit.ManualClock, _ context.CancelFunc) {
			s.onRead = func() { clock.Advance(-time.Second) }
		}, ErrStale},
		{"expected generation unknown", func(_ *colonySource, i *Identity, _ *testkit.ManualClock, _ context.CancelFunc) {
			i.NativeGeneration = domain.Unknown[domain.NativeGeneration]()
		}, ErrChanged},
		{"cancel during read", func(s *colonySource, _ *Identity, _ *testkit.ManualClock, cancel context.CancelFunc) {
			s.onRead = cancel
		}, context.Canceled},
	} {
		t.Run(scenario.name, func(t *testing.T) {
			data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
			if err != nil {
				t.Fatal(err)
			}
			r := &o.ColonyFactsReply{}
			if err = protojson.Unmarshal(data, r); err != nil {
				t.Fatal(err)
			}
			s := &colonySource{reply: r}
			expected, err := DecodeIdentity(&l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(r.GetObserved().Context).(*c.ObservationContext), Paused: proto.Bool(true)}}})
			if err != nil {
				t.Fatal(err)
			}
			clock := testkit.NewManualClock(time.Now())
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			if scenario.change != nil {
				scenario.change(s, &expected, clock, cancel)
			}
			got, err := ObserveColony(ctx, s, clock, expected, time.Second, true, nil)
			if !errors.Is(err, scenario.want) {
				t.Fatalf("got %v, want %v", err, scenario.want)
			}
			if s.identities != 0 {
				t.Fatal("ObserveColony bracketed the facts with identity reads")
			}
			if err == nil {
				if wood, known := got.Projection.Facts.Wood.Value(); !known || wood != 40 {
					t.Fatal("lost validated facts")
				}
				if s.reads != 1 {
					t.Fatalf("facts read %d times", s.reads)
				}
			} else if got.Projection.Identity.Colony != "" {
				t.Fatal("failed read published facts")
			}
		})
	}
}

func TestCachedColonyBoundaryToleratesTheFamilyLag(t *testing.T) {
	expected := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 10000, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	at := func(tick domain.Tick) Identity {
		i := expected
		i.Tick = tick
		return i
	}
	for _, scenario := range []struct {
		name   string
		actual Identity
		family bridge.FactFamily
		want   bool
	}{
		{"same tick", at(10000), bridge.FactPawns, true},
		{"ahead within planning tolerance", at(10000 + domain.PlanningTickTolerance), bridge.FactPawns, true},
		{"ahead past planning tolerance", at(10001 + domain.PlanningTickTolerance), bridge.FactResearch, false},
		{"pawns behind within tolerance", at(10000 - domain.Tick(bridge.FactPawns.TickTolerance())), bridge.FactPawns, true},
		{"pawns behind past tolerance", at(9999 - domain.Tick(bridge.FactPawns.TickTolerance())), bridge.FactPawns, false},
		{"research behind within tolerance", at(10000 - domain.Tick(bridge.FactResearch.TickTolerance())), bridge.FactResearch, true},
		{"emergency behind by the pawn tolerance", at(10000 - domain.Tick(bridge.FactPawns.TickTolerance())), bridge.FactEmergency, true},
		{"rooms behind past tolerance", at(9999 - domain.Tick(bridge.FactRooms.TickTolerance())), bridge.FactRooms, false},
	} {
		t.Run(scenario.name, func(t *testing.T) {
			if got := cachedColonyBoundary(scenario.actual, expected, scenario.family); got != scenario.want {
				t.Fatalf("got %v, want %v", got, scenario.want)
			}
			if !scenario.want || scenario.actual.Tick >= expected.Tick {
				return
			}
			if sameColonyBoundary(scenario.actual, expected) {
				t.Fatal("the live boundary accepted a row behind the expected tick")
			}
			changed := scenario.actual
			changed.NativeGeneration = domain.Known(domain.NativeGeneration(2))
			if cachedColonyBoundary(changed, expected, scenario.family) {
				t.Fatal("a cached row of another generation passed the boundary")
			}
		})
	}
}

// A live shrine census read ahead of the step's cache-served identity is the
// same world under a running window (#712): every live shrine pass failed
// "native observation context changed" and the next heater waited a window.
func TestAheadColonyBoundaryToleratesACachedAnchor(t *testing.T) {
	expected := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 10000, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	at := func(tick domain.Tick) Identity {
		i := expected
		i.Tick = tick
		return i
	}
	lag := domain.Tick(bridge.FactColony.TickTolerance())
	for _, scenario := range []struct {
		name   string
		actual Identity
		want   bool
	}{
		{"same tick", at(10000), true},
		{"ahead past planning tolerance within family lag", at(10001 + domain.PlanningTickTolerance), true},
		{"ahead by the family lag", at(10000 + lag), true},
		{"ahead past the family lag", at(10001 + lag), false},
		{"behind", at(9999), false},
	} {
		t.Run(scenario.name, func(t *testing.T) {
			if got := aheadColonyBoundary(scenario.actual, expected, bridge.FactColony); got != scenario.want {
				t.Fatalf("got %v, want %v", got, scenario.want)
			}
		})
	}
}
