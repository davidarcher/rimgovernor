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
		{"tick advanced", func(_ *colonySource, i *Identity, _ *testkit.ManualClock, _ context.CancelFunc) {
			i.Tick = 8
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
