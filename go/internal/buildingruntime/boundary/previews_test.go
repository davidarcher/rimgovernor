package boundary

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"google.golang.org/protobuf/proto"
)

// The preview read runs beside the bounds read, not after it (#593): the
// bounds read blocks until the preview read has started.
func TestBoundaryInspectionReadsPreviewBesideBounds(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	previewStarted := make(chan struct{})
	f.PreviewHook = func() { close(previewStarted) }
	f.BoundsHook = func() { <-previewStarted }
	out, err := b.Inspect(context.Background(), executor.Target{Action: f.Placement.Action, Snapshot: f.Placement.Snapshot})
	if err != nil || !out.ExternalHoldsComplete || f.Previews != 1 {
		t.Fatal(err, f.Previews)
	}
}

// A memoized preview serves the action's first inspection and no other:
// the inspection after preparation reads natively.
func TestBoundaryInspectionTakesAMemoizedPreviewOnce(t *testing.T) {
	t.Parallel()
	b, f := NewFixture(t)
	memo := NewPreviewMemo([]bridge.BuildingPreview{f.Preview})
	ctx := WithPreviewMemo(context.Background(), memo)
	target := executor.Target{Action: f.Placement.Action, Snapshot: f.Placement.Snapshot}
	out, err := b.Inspect(ctx, target)
	if err != nil || !out.ExternalHoldsComplete || out.Tick != 11 || f.Previews != 0 || memo.Served() != 1 {
		t.Fatal(err, f.Previews, memo.Served())
	}
	if out, err = b.Inspect(ctx, target); err != nil || !out.ExternalHoldsComplete || f.Previews != 1 || memo.Served() != 1 {
		t.Fatal(err, f.Previews, memo.Served())
	}
}

// A memoized preview bound to another authority snapshot, or one the
// inspection's bounds read has outrun past the freshness bound, is not
// used; the inspection reads live.
func TestBoundaryInspectionLeavesAStaleMemoizedPreview(t *testing.T) {
	t.Parallel()
	t.Run("snapshot", func(t *testing.T) {
		b, f := NewFixture(t)
		other := f.Preview
		other.Preview.Snapshot.Revision++
		memo := NewPreviewMemo([]bridge.BuildingPreview{other})
		out, err := b.Inspect(WithPreviewMemo(context.Background(), memo), executor.Target{Action: f.Placement.Action, Snapshot: f.Placement.Snapshot})
		if err != nil || !out.ExternalHoldsComplete || f.Previews != 1 || memo.Served() != 0 {
			t.Fatal(err, f.Previews, memo.Served())
		}
	})
	t.Run("outrun", func(t *testing.T) {
		b, f := NewFixture(t)
		old := f.Preview
		memo := NewPreviewMemo([]bridge.BuildingPreview{old})
		later := int64(old.Preview.Tick) + int64(domain.PlanningTickTolerance) + 1
		f.Bounds.Context.Tick = proto.Int64(later)
		f.Emergency.Context.Tick = proto.Int64(later)
		f.Preview.Preview.Tick, f.Preview.Stock.Tick = domain.Tick(later), domain.Tick(later)
		out, err := b.Inspect(WithPreviewMemo(context.Background(), memo), executor.Target{Action: f.Placement.Action, Snapshot: f.Placement.Snapshot})
		if err != nil || !out.ExternalHoldsComplete || out.Tick != domain.Tick(later) || f.Previews != 1 || memo.Served() != 1 {
			t.Fatal(err, out.Tick, f.Previews, memo.Served())
		}
	})
	t.Run("within tolerance", func(t *testing.T) {
		b, f := NewFixture(t)
		old := f.Preview
		memo := NewPreviewMemo([]bridge.BuildingPreview{old})
		later := int64(old.Preview.Tick) + int64(domain.PlanningTickTolerance)
		f.Bounds.Context.Tick = proto.Int64(later)
		f.Emergency.Context.Tick = proto.Int64(later)
		out, err := b.Inspect(WithPreviewMemo(context.Background(), memo), executor.Target{Action: f.Placement.Action, Snapshot: f.Placement.Snapshot})
		if err != nil || !out.ExternalHoldsComplete || out.Tick != old.Preview.Tick || f.Previews != 0 {
			t.Fatal(err, out.Tick, f.Previews)
		}
	})
}
