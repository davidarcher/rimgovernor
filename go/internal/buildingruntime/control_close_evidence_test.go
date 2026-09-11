package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/runtimeowner"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
)

func requireProfileHeld(t *testing.T, dir string) {
	t.Helper()
	other, err := runtimeowner.Acquire(context.Background(), dir)
	if err == nil {
		other.Close()
		t.Fatal("uncertain shutdown released profile ownership")
	}
}

func TestCloseRetainsProfileUntilAuthorityReadRecovers(t *testing.T) {
	control, native, sink, dir := controlFixture(t, nil)
	if _, err := control.Acquire(context.Background(), controlScope()); err != nil {
		t.Fatal(err)
	}
	unavailable := errors.New("authority observation unavailable")
	native.onRead = func(context.Context) error { return unavailable }
	t.Cleanup(func() { native.onRead = nil })
	for range 2 {
		if err := control.Close(context.Background()); !errors.Is(err, unavailable) {
			t.Fatalf("close erased unresolved authority: %v", err)
		}
		if sink.enabled() || native.revokes.Load() != 0 {
			t.Fatal("unavailable authority permitted a native write")
		}
		requireProfileHeld(t, dir)
	}
	native.onRead = nil
	if err := control.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if native.revokes.Load() != 1 || native.acquires.Load() != 1 {
		t.Fatal("close retried acquisition or failed to revoke the observed owner")
	}
	other, err := runtimeowner.Acquire(context.Background(), dir)
	if err != nil {
		t.Fatal("confirmed shutdown retained the profile", err)
	}
	other.Close()
}

type lostCloseReplyNative struct {
	*controlNative
	lost error
}

func (n lostCloseReplyNative) Revoke(ctx context.Context, request *a.Revoke) (*a.ControlReply, bridge.Result, error) {
	if _, _, err := n.controlNative.Revoke(ctx, request); err != nil {
		return nil, bridge.Result{}, err
	}
	return nil, bridge.Result{}, n.lost
}

func TestCloseObservesLostRevokeReplyBeforeReleasingProfile(t *testing.T) {
	control, native, sink, dir := controlFixture(t, nil)
	if _, err := control.Acquire(context.Background(), controlScope()); err != nil {
		t.Fatal(err)
	}
	lost := errors.New("revoke reply lost after native application")
	control.native = lostCloseReplyNative{native, lost}
	if err := control.Close(context.Background()); !errors.Is(err, lost) {
		t.Fatalf("close accepted a lost reply: %v", err)
	}
	if sink.enabled() {
		t.Fatal("shutdown retained live permission")
	}
	requireProfileHeld(t, dir)
	if err := control.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if native.revokes.Load() != 1 || native.acquires.Load() != 1 {
		t.Fatal("shutdown replayed a write instead of observing inactive authority")
	}
	other, err := runtimeowner.Acquire(context.Background(), dir)
	if err != nil {
		t.Fatal("observed inactive authority did not release profile", err)
	}
	other.Close()
}
