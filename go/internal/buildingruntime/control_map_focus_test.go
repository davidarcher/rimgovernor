package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"testing"
)

type focusControlNative struct {
	*controlNative
	focus func(context.Context, *a.FocusMap) (*a.ControlReply, bridge.Result, error)
}

func (n *focusControlNative) FocusMap(ctx context.Context, r *a.FocusMap) (*a.ControlReply, bridge.Result, error) {
	return n.focus(ctx, r)
}

func TestMapFocusJoinsWritersBeforeNativeCall(t *testing.T) {
	control, native, _, _ := controlFixture(t, nil)
	before, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	if err = control.Disable(); err != nil {
		t.Fatal(err)
	}
	entered, release, focused := make(chan struct{}), make(chan struct{}), make(chan struct{})
	control.config.StopWrites = func(ctx context.Context) error {
		close(entered)
		select {
		case <-release:
			return nil
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	control.native = &focusControlNative{controlNative: native, focus: func(ctx context.Context, r *a.FocusMap) (*a.ControlReply, bridge.Result, error) {
		select {
		case <-release:
		default:
			t.Error("native focus before writer join")
		}
		close(focused)
		return &a.ControlReply{Outcome: &a.ControlReply_FocusedMap{FocusedMap: &a.FocusedMap{Context: &c.ObservationContext{Identity: r.Target, NativeGeneration: proto.Uint64(r.GetExpectedGeneration() + 1)}}}}, bridge.Result{}, nil
	}}
	session := &Session{control: control}
	target := before
	target.Map = 77
	finished := make(chan error, 1)
	go func() { _, err := session.FocusMap(context.Background(), before, target); finished <- err }()
	<-entered
	select {
	case <-focused:
		t.Fatal("focus escaped drain")
	default:
	}
	close(release)
	if err := <-finished; err != nil {
		t.Fatal(err)
	}
	control.config.StopWrites = func(context.Context) error { return nil }
}

func TestHandoffGenerationRejectsNativeManualBeforeAcquire(t *testing.T) {
	control, native, _, _ := controlFixture(t, nil)
	ctx := context.WithValue(context.Background(), handoffGenerationKey{}, domain.NativeGeneration(1))
	native.mu.Lock()
	native.generation = 2
	native.active = false
	native.mu.Unlock()
	if _, err := control.Acquire(ctx, controlScope()); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
	if native.acquires.Load() != 0 {
		t.Fatal("native Manual was regranted")
	}
}
