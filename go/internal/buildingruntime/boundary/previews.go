package boundary

import (
	"context"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// PreviewMemo carries the building previews a Worker step read in one
// batch ahead of its dispatches (#593): the step's building candidates
// cost one placement_preview hop instead of one each. An inspection takes
// its action's preview once and drops it, so the inspection after durable
// preparation, and every later one, reads natively; a preview bound to
// another authority snapshot, or one the inspection's own bounds read has
// outrun past the planning tolerance and the live drift, is left unused
// and the inspection reads live. A batch preview is a paused-world fact
// like any the step cache serves: the live second inspection is what
// guards the dispatch against what the step's earlier writes changed.
type PreviewMemo struct {
	mu       sync.Mutex
	previews map[domain.ActionID]bridge.BuildingPreview
	served   int
}

// NewPreviewMemo files previews by their action.
func NewPreviewMemo(previews []bridge.BuildingPreview) *PreviewMemo {
	memo := &PreviewMemo{previews: make(map[domain.ActionID]bridge.BuildingPreview, len(previews))}
	for _, preview := range previews {
		memo.previews[preview.Preview.Action.ID()] = preview
	}
	return memo
}

type previewMemoKey struct{}

// WithPreviewMemo attaches memo to ctx for the inspections run under it.
func WithPreviewMemo(ctx context.Context, memo *PreviewMemo) context.Context {
	if memo == nil {
		return ctx
	}
	return context.WithValue(ctx, previewMemoKey{}, memo)
}

// PreviewMemoFrom is the memo on ctx, or nil.
func PreviewMemoFrom(ctx context.Context) *PreviewMemo {
	memo, _ := ctx.Value(previewMemoKey{}).(*PreviewMemo)
	return memo
}

// Take hands out and drops the preview of action bound to snapshot.
func (m *PreviewMemo) Take(action domain.ActionID, snapshot domain.GenerationSnapshot) (bridge.BuildingPreview, bool) {
	if m == nil {
		return bridge.BuildingPreview{}, false
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	preview, ok := m.previews[action]
	if !ok {
		return bridge.BuildingPreview{}, false
	}
	delete(m.previews, action)
	if !preview.Preview.Snapshot.Matches(snapshot) || !preview.Stock.Snapshot.Matches(snapshot) {
		return bridge.BuildingPreview{}, false
	}
	m.served++
	return preview, true
}

// Served is the number of previews inspections took from the memo.
func (m *PreviewMemo) Served() int {
	if m == nil {
		return 0
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.served
}
