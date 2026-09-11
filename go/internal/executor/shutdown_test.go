package executor

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestStopJoinsUnknownReceiptBeforeOwnershipRelease(t *testing.T) {
	f := newFixture(t)
	entered, release := make(chan struct{}), make(chan struct{})
	f.env.onPlace = func(ctx context.Context, _ Placement) (Receipt, error) {
		close(entered)
		<-release
		return Receipt{}, ctx.Err()
	}
	done := make(chan error, 1)
	go func() { _, err := f.run(); done <- err }()
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("dispatch did not start")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Millisecond)
	defer cancel()
	if err := f.executor.Stop(ctx); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("stop returned before active write: %v", err)
	}
	if _, err := f.run(); !errors.Is(err, ErrStopped) {
		t.Fatalf("new work after stop: %v", err)
	}
	if err := f.executor.UpdateAuthority(f.authority); !errors.Is(err, ErrStopped) {
		t.Fatalf("stopped executor reenabled: %v", err)
	}
	close(release)
	if err := f.executor.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatalf("active run outcome: %v", err)
	}
	view := f.progress(t)
	receipt, known := view.Receipt.Value()
	if !view.Unresolved || !known || receipt != domain.ReceiptUnknown {
		t.Fatal("stop returned without durable unknown receipt")
	}
	if err := f.executor.Stop(context.Background()); err != nil {
		t.Fatal("stop not idempotent", err)
	}
}
