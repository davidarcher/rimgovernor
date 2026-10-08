package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type handoffSession struct {
	*playerFakeSession
	focus   func(context.Context, domain.GenerationSnapshot, domain.GenerationSnapshot) (domain.NativeGeneration, error)
	confirm func(context.Context, domain.GenerationSnapshot, domain.GenerationSnapshot, domain.NativeGeneration) error
}

func (s *handoffSession) ConfirmMapFocus(ctx context.Context, before, target domain.GenerationSnapshot, generation domain.NativeGeneration) error {
	return s.confirm(ctx, before, target, generation)
}

func (s *handoffSession) FocusMap(ctx context.Context, before, target domain.GenerationSnapshot) (domain.NativeGeneration, error) {
	return s.focus(ctx, before, target)
}

func TestExpeditionHandoffHomeArrivalAfterNativeRemovedSiteMap(t *testing.T) {
	p, db, s, worlds := playerFixture(t)
	worlds.world.Map = 77
	if _, err := p.Resume(context.Background(), store.ControlRequest{RequestID: "site-auto", Kind: store.ResumeControl, World: worlds.world}); err != nil {
		t.Fatal(err)
	}
	before := s.State()
	plan := handoffPlan(t, before.Snapshot, 1, "quest-site-q-site-return-site-0")
	ctx := context.Background()
	if err := db.CreatePlan(ctx, plan.Spec); err != nil {
		t.Fatal(err)
	}
	if err := db.SeedPlanMethod(ctx, plan.Spec.ID(), plan.Method); err != nil {
		t.Fatal(err)
	}
	proof := before.Snapshot
	proof.Plan = plan.Spec.ID()
	if _, err := db.Prepare(ctx, plan.Spec.ID(), "depart", proof, 1); err != nil {
		t.Fatal(err)
	}
	progress, err := db.Dispatch(ctx, plan.Spec.ID(), "depart", proof, 1)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = db.RecordReceipt(ctx, plan.Spec.ID(), "depart", progress.View().Attempt, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	// Native has removed map77 and changed the viewed identity to home0.
	// The event poll disabled local writes but retained the prior journal intent.
	if err = s.Disable(); err != nil {
		t.Fatal(err)
	}
	worlds.world.Map = 0
	confirmed := false
	p.session = &handoffSession{playerFakeSession: s, focus: func(context.Context, domain.GenerationSnapshot, domain.GenerationSnapshot) (domain.NativeGeneration, error) {
		t.Fatal("removed source map was focused/validated")
		return 0, ErrControl
	}, confirm: func(ctx context.Context, old, target domain.GenerationSnapshot, generation domain.NativeGeneration) error {
		if old.Map != 77 || target.Map != 0 || generation != old.Native+1 {
			t.Fatal("wrong arrival scope")
		}
		confirmed = true
		return nil
	}}
	context := &c.ObservationContext{Identity: controlIdentity(domain.GenerationSnapshot{Colony: before.Snapshot.Colony, Load: before.Snapshot.Load, Map: 0}), Tick: proto.Int64(2), NativeGeneration: proto.Uint64(uint64(before.Snapshot.Native + 1))}
	loaded := &o.BundleSnapshot{Context: context, WorldProgression: &o.WorldProgressionSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Maps: []*o.WorldMap{{Id: proto.Int32(0), Tile: proto.Int32(1), Home: proto.Bool(true), Pawns: []*o.PawnState{{Pawn: &o.EntityRef{Id: proto.String("a")}}, {Pawn: &o.EntityRef{Id: proto.String("b")}}}}}}}
	call, epoch, done, err := p.enter(ctx, "incoming-handoff", false)
	if err != nil {
		t.Fatal(err)
	}
	handed, err := p.expeditionIncomingHandoff(call, epoch, loaded)
	done()
	if err != nil || !handed || !confirmed || !s.State().Enabled || s.State().Snapshot.Map != 0 {
		t.Fatalf("home handoff: %v %v %+v", handed, err, s.State())
	}
}

func TestExpeditionHandoffDrainsBeforeFocusAndPreservesManual(t *testing.T) {
	for _, manual := range []bool{false, true} {
		t.Run(map[bool]string{false: "success", true: "manual_during_focus"}[manual], func(t *testing.T) {
			p, _, s, worlds := playerFixture(t)
			request := store.ControlRequest{RequestID: "initial-auto", Kind: store.ResumeControl, World: worlds.world}
			if _, err := p.Resume(context.Background(), request); err != nil {
				t.Fatal(err)
			}
			before := s.State()
			target := before.Snapshot
			target.Map = 77
			entered, release := make(chan struct{}), make(chan struct{})
			p.session = &handoffSession{playerFakeSession: s, focus: func(ctx context.Context, old, next domain.GenerationSnapshot) (domain.NativeGeneration, error) {
				if s.State().Enabled {
					t.Error("focus preceded local writer invalidation")
				}
				if old != before.Snapshot || next != target {
					t.Error("focus widened scope")
				}
				close(entered)
				select {
				case <-release:
				case <-ctx.Done():
					return 0, ctx.Err()
				}
				worlds.mu.Lock()
				worlds.world = playerWorld(next)
				worlds.mu.Unlock()
				return old.Native + 1, nil
			}}
			finished := make(chan error, 1)
			go func() {
				call, epoch, done, err := p.enter(context.Background(), "handoff", false)
				if err == nil {
					_, err = p.handoffHeld(call, epoch, before, target)
					done()
				}
				finished <- err
			}()
			<-entered
			if manual {
				paused := make(chan error, 1)
				go func() {
					_, err := p.Pause(context.Background(), store.ControlRequest{RequestID: "human-manual", Kind: store.PauseControl, World: request.World})
					paused <- err
				}()
				if err := <-finished; !errors.Is(err, context.Canceled) {
					t.Fatalf("handoff survived Manual: %v", err)
				}
				if err := <-paused; err != nil {
					t.Fatal(err)
				}
				if s.acquires.Load() != 1 || s.State().Enabled {
					t.Fatal("Manual regranted authority")
				}
			} else {
				close(release)
				if err := <-finished; err != nil {
					t.Fatal(err)
				}
				if got := s.State(); !got.Enabled || got.Snapshot.Map != 77 || s.acquires.Load() != 2 {
					t.Fatal(got)
				}
			}
		})
	}
}
