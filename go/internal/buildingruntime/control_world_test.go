package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"testing"
)

func TestControlCloseRetiresOnlyPositivelyReplacedWorld(t *testing.T) {
	t.Parallel()
	for _, field := range []string{"colony", "map", "load"} {
		t.Run(field, func(t *testing.T) {
			drained := false
			control, n, sink, dir := controlFixture(t, func(context.Context) error { drained = true; return nil })
			original, err := control.Acquire(context.Background(), controlScope())
			if err != nil {
				t.Fatal(err)
			}
			actual := playerWorld(original)
			switch field {
			case "colony":
				actual.Colony = "replacement"
			case "map":
				actual.Map++
			case "load":
				actual.Load = "replacement"
			}
			reads := 0
			control.config.Worlds = playerWorldFunc(func(context.Context) (store.World, error) {
				reads++
				if !drained {
					t.Fatal("identity read before writer drain")
				}
				return actual, nil
			})
			n.onRead = func(context.Context) error { t.Fatal("authority read against replaced target"); return nil }
			if err := control.Close(context.Background()); err != nil {
				t.Fatal(err)
			}
			if reads != 1 || n.acquires.Load() != 1 || n.revokes.Load() != 0 || n.renews.Load() != 0 || sink.enabled() {
				t.Fatal("replacement gained authority")
			}
			if control.snapshot != original {
				t.Fatal("replacement overwrote original cleanup scope")
			}
			other, err := AcquireProfile(context.Background(), dir)
			if err != nil {
				t.Fatal("profile not released", err)
			}
			other.Close()
		})
	}
}
func TestControlCloseWorldFailureRetainsOwnershipForRetry(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"unavailable", "invalid", "cancelled"} {
		t.Run(kind, func(t *testing.T) {
			control, n, sink, dir := controlFixture(t, nil)
			original, err := control.Acquire(context.Background(), controlScope())
			if err != nil {
				t.Fatal(err)
			}
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			actual := playerWorld(original)
			actual.Load = "new-load"
			control.config.Worlds = playerWorldFunc(func(context.Context) (store.World, error) {
				switch kind {
				case "unavailable":
					return store.World{}, errors.New("identity unavailable")
				case "invalid":
					return store.World{}, nil
				case "cancelled":
					cancel()
				}
				return actual, nil
			})
			n.onRead = func(context.Context) error { t.Fatal("authority read after unproven identity"); return nil }
			if err := control.Close(ctx); err == nil {
				t.Fatal("uncertain shutdown succeeded")
			}
			if control.closed || sink.enabled() || n.revokes.Load() != 0 {
				t.Fatal("failed close released permission or ownership")
			}
			if other, err := AcquireProfile(context.Background(), dir); err == nil {
				other.Close()
				t.Fatal("failed close released profile")
			}
			control.config.Worlds = playerWorldFunc(func(context.Context) (store.World, error) { return actual, nil })
			if err := control.Close(context.Background()); err != nil {
				t.Fatal(err)
			}
			other, err := AcquireProfile(context.Background(), dir)
			if err != nil {
				t.Fatal(err)
			}
			other.Close()
		})
	}
}
func TestControlCloseSameWorldStillRevokes(t *testing.T) {
	t.Parallel()
	control, n, _, _ := controlFixture(t, nil)
	original, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	reads := 0
	control.config.Worlds = playerWorldFunc(func(context.Context) (store.World, error) { reads++; return playerWorld(original), nil })
	if err := control.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if reads != 1 || n.revokes.Load() != 1 || n.acquires.Load() != 1 {
		t.Fatal(reads, n.revokes.Load())
	}
}
func TestControlCloseDoesNotReadWorldBeforeSuccessfulDrain(t *testing.T) {
	t.Parallel()
	blocked := true
	control, n, _, _ := controlFixture(t, func(context.Context) error {
		if blocked {
			return errors.New("writer active")
		}
		return nil
	})
	original, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	calls := 0
	control.config.Worlds = playerWorldFunc(func(context.Context) (store.World, error) { calls++; return playerWorld(original), nil })
	if err := control.Close(context.Background()); err == nil || calls != 0 || n.revokes.Load() != 0 {
		t.Fatal("drain bypassed")
	}
	blocked = false
	if err := control.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
}
