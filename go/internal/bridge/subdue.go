package bridge

import (
	"context"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// Subdue is a guarded pawn order sharing the attack capability's authority.
func (control *AttackControl) Subdue(ctx context.Context, pre *a.WritePrecondition, command *o.PawnTargetOrder) (*o.ExecuteReply, Result, error) {
	if control == nil || control.client == nil || command.GetKind() != o.PawnOrderKind_PAWN_ORDER_KIND_SUBDUE {
		return nil, Result{}, contract("subdue capability or kind missing")
	}
	return (&PawnOrderControl{client: control.client}).OrderPawn(ctx, pre, command)
}
