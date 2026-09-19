package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"time"
)

// Lease is a construction-time relay; native calls never occur under sink.mu.
func (s *sessionSink) Lease(snapshot domain.GenerationSnapshot) (string, error) {
	s.mu.Lock()
	control := s.control
	s.mu.Unlock()
	if control == nil {
		return "", ErrControl
	}
	return control.Lease(snapshot)
}

type draftSweep struct {
	journal  *store.Store
	executor *executor.Executor
	gate     chan struct{}
	timeout  time.Duration
	next     int
}

func draftOutstanding(p domain.Progress) bool {
	if p.Action().Kind() != domain.OwnedDraftAction {
		return false
	}
	c, known := p.View().DraftCleanup.Value()
	return known && c.Stage != domain.DraftNotAcquired && c.Stage != domain.DraftReleased && c.Stage != domain.DraftSuperseded
}

// run visits every obligation fairly. One evidence-only claim binding may be
// followed by one release; uncertain writes always return to the caller.
// With retainHeld the drafts a plan still holds (workerPlanHoldsDraft) are
// left alone: a resume in the same world keeps the suspended plan's owned
// drafts, so a combat hold plan survives the pause that interrupted it
// (#228, #318); the worker releases them once the plan settles.
func (s *draftSweep) run(ctx context.Context, retainHeld bool) error {
	ctx, cancel := context.WithTimeout(ctx, s.timeout)
	defer cancel()
	select {
	case s.gate <- struct{}{}:
		defer func() { <-s.gate }()
	case <-ctx.Done():
		return ctx.Err()
	}
	plans, err := s.journal.LoadPlans(ctx, 256)
	if err != nil {
		return err
	}
	var pending []domain.Progress
	for _, plan := range plans {
		for _, p := range plan.Progress {
			if draftOutstanding(p) && !(retainHeld && workerPlanHoldsDraft(plan, p.View())) {
				pending = append(pending, p)
			}
		}
	}
	if len(pending) == 0 {
		return nil
	}
	start := s.next % len(pending)
	var failures []error
	for offset := range len(pending) {
		index := (start + offset) % len(pending)
		s.next = (index + 1) % len(pending)
		progress := pending[index]
		for step := 0; step < 2; step++ {
			before := progress.View()
			result, callErr := s.executor.CleanupDraft(ctx, before.Plan, before.Action)
			if callErr != nil {
				failures = append(failures, fmt.Errorf("draft cleanup %s: %w", before.Action, callErr))
				break
			}
			progress = result.Progress
			if !draftOutstanding(progress) {
				break
			}
			if result.NativeCalled || progress.View().DraftCleanup == before.DraftCleanup || step == 1 {
				failures = append(failures, fmt.Errorf("draft cleanup %s: %w", before.Action, executor.ErrHeld))
				break
			}
		}
		if ctx.Err() != nil {
			failures = append(failures, ctx.Err())
			break
		}
	}
	return errors.Join(failures...)
}
