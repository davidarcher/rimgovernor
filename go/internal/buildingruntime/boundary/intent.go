package boundary

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// ActionsWriter narrows *bridge.ActionsWriter.
type ActionsWriter interface {
	Apply(context.Context, *c.Identity, []*o.Action) (*o.ApplyReply, bridge.Result, error)
}

// IntentKey is an intent-mode attempt's idempotency key: the plan (which
// names the goal) and the attempt.
func IntentKey(p executor.Placement) string {
	return fmt.Sprintf("%s/%d", p.Snapshot.Plan, p.Attempt)
}

// DispatchIntent sends one intent-mode action through Actions/Apply. Only
// the identity is checked on the wire; native validates the intent against
// live state. Applied is accepted; refused and failed are refused, except an
// attempt conflict; a batch failure or a lost reply is unknown, which an
// idempotent intent may resend.
func (b *Boundary) DispatchIntent(ctx context.Context, p executor.Placement, writer ActionsWriter) (executor.Receipt, error) {
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	lease, err := b.Leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !ValidID(lease) {
		return out, executor.ErrAuthority
	}
	action, err := bridge.IntentAction(IntentKey(p), p.Action)
	if err != nil {
		return out, err
	}
	reply, _, err := writer.Apply(ctx, Identity(p.Snapshot), []*o.Action{action})
	if err != nil {
		return out, err
	}
	result := reply.GetResults()[0]
	switch {
	case result.GetApplied().GetApplied() != nil:
		out.Kind = domain.ReceiptAccepted
	case result.GetRefused() != nil:
		out.Kind = domain.ReceiptRefused
	case result.GetFailed() != nil && result.GetFailed().GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT:
		out.Kind = domain.ReceiptRefused
	}
	return out, nil
}
