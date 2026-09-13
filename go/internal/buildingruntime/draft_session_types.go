package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/draft"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
)

// CleanupDraft shares the executor writer and remains usable after ordinary Stop.
// Session/worker ownership must join every caller before closing shared handles.
func (s *Session) CleanupDraft(ctx context.Context, plan domain.PlanID, action domain.ActionID) (executor.Result, error) {
	return s.executor.CleanupDraft(ctx, plan, action)
}

var _ WorldSource = (*draft.DraftBoundary)(nil)
