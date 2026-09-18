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
	// Either mode acknowledges only the downed colonists the window decision
	// names below; combat mode also its hostiles. Medical suppression never.
	p := intent.Command.Start.Policy
	if p == nil || len(p.AcknowledgedInjuredColonistIds)+len(p.SurgicalRecoveryIds)+len(p.MedicalRestIds) != 0 || p.GetInjuryStopCooldownMs() != 0 {
		return store.ClockAttempt{}, executor.ErrHeld
	}
	switch p.GetMode() {
	case k.WatchMode_WATCH_MODE_COLONY:
		if len(p.AcknowledgedHostileIds) != 0 {
			return store.ClockAttempt{}, executor.ErrHeld
		}
	case k.WatchMode_WATCH_MODE_COMBAT:
		if len(p.AcknowledgedHostileIds) == 0 {
			return store.ClockAttempt{}, executor.ErrHeld
		}
	default:
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
		decision := policy.EvaluateClockWindow(request.Facts, policy.ClockWindowLimits{Now: q.clock.Now(), MaxAge: request.MaxAge, MaxTicks: value.Command.Start.MaxTicks, CombatMaxTicks: request.CombatMaxTicks})
		w := value.Window
		if !decision.Admitted || value.Snapshot != decision.Snapshot || w.Snapshot != decision.Snapshot || w.Tick != decision.Tick || w.ReviewRevision != decision.ReviewRevision || w.CapturedCursor != decision.CapturedCursor || w.MaxTicks != decision.MaxTicks {
			return executor.ErrHeld
		}
		return clockWindowPolicyMatches(value.Command.Start.Policy, decision)
	}
	// The admitting step's own status stands in for the pre-dispatch
	// clock_read_status while it is within MaxAge; the check above still
	// re-evaluates the same facts against the wall clock.
	observed := func() *k.Status {
		if request.Status == nil || request.StatusAt.IsZero() || q.clock.Now().Sub(request.StatusAt) > request.MaxAge {
			return nil
		}
		return proto.Clone(request.Status).(*k.Status)
	}
	return q.command(ctx, intent, check, observed)
}

// clockWindowPolicyMatches holds unless the start policy watches in the mode
// the window decision admitted and acknowledges exactly its hostiles and its
// known downed colonists.
func clockWindowPolicyMatches(p *k.WatchPolicy, decision policy.ClockWindowDecision) error {
	var mode k.WatchMode
	switch decision.Mode {
	case policy.ClockWindowColony:
		mode = k.WatchMode_WATCH_MODE_COLONY
	case policy.ClockWindowCombat:
		mode = k.WatchMode_WATCH_MODE_COMBAT
	default:
		return executor.ErrHeld
	}
	if p.GetMode() != mode || len(p.AcknowledgedHostileIds) != len(decision.Hostiles) {
		return executor.ErrHeld
	}
	for i, id := range decision.Hostiles {
		if p.AcknowledgedHostileIds[i] != string(id) {
			return executor.ErrHeld
		}
	}
	if len(p.AcknowledgedDownedColonistIds) != len(decision.Downed) {
		return executor.ErrHeld
	}
	for i, id := range decision.Downed {
		if p.AcknowledgedDownedColonistIds[i] != string(id) {
			return executor.ErrHeld
		}
	}
	return nil
}
