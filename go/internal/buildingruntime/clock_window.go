package buildingruntime

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// CommandWindow carries the same captured facts through queueing and dispatch.
// Revalidation never refreshes their timestamps or grants replacement authority.
func (q *ClockCoordinator) CommandWindow(ctx context.Context, request ClockWindowRequest) (store.ClockAttempt, error) {
	intent := request.Intent
	if intent.Window == nil || intent.Command.Start == nil || intent.Command.Renew != nil || intent.Command.Speed != nil {
		return store.ClockAttempt{}, executor.ErrHeld
	}
	window, start := *intent.Window, *intent.Command.Start
	intent.Window, intent.Command.Start = &window, &start
	if start.Policy != nil {
		start.Policy = proto.Clone(start.Policy).(*k.WatchPolicy)
	}
	check := func(value store.ClockIntent) error {
		if value.Window == nil || value.Command.Start == nil {
			return executor.ErrHeld
		}
		decision := policy.EvaluateClockWindow(request.Facts, policy.ClockWindowLimits{Now: q.clock.Now(), MaxAge: request.MaxAge, MaxTicks: value.Command.Start.MaxTicks})
		w := value.Window
		if !decision.Admitted || value.Snapshot != decision.Snapshot || w.Snapshot != decision.Snapshot || w.Tick != decision.Tick || w.ReviewRevision != decision.ReviewRevision || w.CapturedCursor != decision.CapturedCursor || w.MaxTicks != decision.MaxTicks {
			return executor.ErrHeld
		}
		return nil
	}
	return q.command(ctx, intent, check)
}
