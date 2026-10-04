package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

type stepScopeTickFake struct {
	ticks   int
	context *c.ObservationContext
	err     error
}

func (f *stepScopeTickFake) Tick(ctx context.Context) (*l.TickReply, bridge.Result, error) {
	f.ticks++
	if f.err != nil {
		return nil, bridge.Result{}, f.err
	}
	return &l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: f.context, Paused: proto.Bool(false)}}}, bridge.Result{}, ctx.Err()
}

// The scope read is lifecycle_read_tick, which the step's bundle seeds (#200);
// the source must offer it, so there is no identity fallback (#1670).
func TestStepScopeReadsTheTick(t *testing.T) {
	t.Parallel()
	context_ := &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(1)}, Tick: proto.Int64(12), NativeGeneration: proto.Uint64(3)}
	ticking := &stepScopeTickFake{context: context_}
	got, err := stepScope(context.Background(), ticking)
	if err != nil || ticking.ticks != 1 || got.Tick != 12 || got.Colony != "colony" || got.Paused != domain.Known(false) {
		t.Fatal(got, err, ticking.ticks)
	}
	if generation, known := got.NativeGeneration.Value(); !known || generation != 3 {
		t.Fatal(got.NativeGeneration)
	}
	want := errors.New("tick unavailable")
	if _, err = stepScope(context.Background(), &stepScopeTickFake{err: want}); !errors.Is(err, want) {
		t.Fatal(err)
	}
}
