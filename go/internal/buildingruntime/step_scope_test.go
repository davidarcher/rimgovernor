package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

type stepScopeFake struct {
	identities, ticks int
	context           *c.ObservationContext
}

func (f *stepScopeFake) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	f.identities++
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: f.context, Paused: proto.Bool(true)}}}, bridge.Result{}, ctx.Err()
}

type stepScopeTickFake struct{ stepScopeFake }

func (f *stepScopeTickFake) Tick(ctx context.Context) (*l.TickReply, bridge.Result, error) {
	f.ticks++
	return &l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: f.context, Paused: proto.Bool(false)}}}, bridge.Result{}, ctx.Err()
}

// The scope read prefers lifecycle_read_tick, which the step's bundle seeds,
// and falls back to the identity read for a source without it (#200).
func TestStepScopePrefersTickRead(t *testing.T) {
	t.Parallel()
	context_ := &c.ObservationContext{Identity: &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(1)}, Tick: proto.Int64(12), NativeGeneration: proto.Uint64(3)}
	plain := &stepScopeFake{context: context_}
	got, err := stepScope(context.Background(), plain)
	if err != nil || plain.identities != 1 || got.Tick != 12 || got.Colony != "colony" || got.Map != 1 || got.Paused != domain.Known(true) {
		t.Fatal(got, err, plain.identities)
	}
	ticking := &stepScopeTickFake{stepScopeFake{context: context_}}
	got, err = stepScope(context.Background(), ticking)
	if err != nil || ticking.identities != 0 || ticking.ticks != 1 || got.Tick != 12 || got.Colony != "colony" || got.Paused != domain.Known(false) {
		t.Fatal(got, err, ticking.identities, ticking.ticks)
	}
	if generation, known := got.NativeGeneration.Value(); !known || generation != 3 {
		t.Fatal(got.NativeGeneration)
	}
}
