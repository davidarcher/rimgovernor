package haul

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

func init() { domain.RegisterIntentKind(domain.HaulAction) }

type anyFrameNative struct{ anyFrame bool }

func (f *anyFrameNative) ReadPawns(ctx context.Context, _ *c.Identity, _ []string) (*n.ListPawnsReply, bridge.Result, error) {
	f.anyFrame = bridge.AnyFrame(ctx)
	return &n.ListPawnsReply{}, bridge.Result{}, nil
}

type noWrites struct{}

func (noWrites) Apply(context.Context, *c.Identity, []*o.Action) (*o.ApplyReply, bridge.Result, error) {
	return nil, bridge.Result{}, errors.New("unused")
}

type noLeases struct{}

func (noLeases) Lease(domain.GenerationSnapshot) (string, error) { return "", nil }

type wallClock struct{}

func (wallClock) Now() time.Time { return time.Now() }

// The haul inspect reads the pawn only for its Context, so it skips the
// read-after-write frame wait.
func TestInspectHaulReadsAnyFrame(t *testing.T) {
	native := &anyFrameNative{}
	b, err := NewHaulBoundary(native, noWrites{}, noLeases{}, wallClock{})
	if err != nil {
		t.Fatal(err)
	}
	haul, _ := domain.NewHaul("hauler", "thing", "MealSimple", domain.Cell{X: 1, Z: 1})
	action, _ := domain.NewHaulAction("haul-1", haul)
	if _, err := b.InspectHaul(context.Background(), executor.Target{Action: action}); !errors.Is(err, executor.ErrHeld) {
		t.Fatalf("inspect: %v", err)
	}
	if !native.anyFrame {
		t.Fatal("haul pawn read waited for a post-write frame")
	}
}
