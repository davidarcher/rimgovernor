package melee

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
)

type subdueNative interface {
	PreviewPawnOrder(context.Context, *c.Identity, *o.PawnTargetOrder) (*o.PreviewReply, bridge.Result, error)
	LookupPawnOrderAttempt(context.Context, bridge.PawnOrderAttempt) (*r.LookupReply, bridge.Result, error)
	ObservePawnOrderProgress(context.Context, bridge.PawnOrderAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type subdueWriter interface {
	Subdue(context.Context, *a.WritePrecondition, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}

func subdueCommand(command *o.AttackTarget) *o.PawnTargetOrder {
	no := false
	return &o.PawnTargetOrder{Pawn: command.Pawn, Target: command.Target, Kind: o.PawnOrderKind_PAWN_ORDER_KIND_SUBDUE.Enum(), RequireSafeStorage: &no}
}
func subdueAttempt(a bridge.AttackAttempt) bridge.PawnOrderAttempt {
	return bridge.PawnOrderAttempt{Identity: a.Identity, Attempt: a.Attempt, NativeGeneration: a.NativeGeneration, PawnID: a.PawnID, TargetID: a.TargetID, Kind: o.PawnOrderKind_PAWN_ORDER_KIND_SUBDUE}
}
func (b *MeleeBoundary) preview(ctx context.Context, id *c.Identity, command *o.AttackTarget, subdue bool) (*o.PreviewReply, bridge.Result, error) {
	if !subdue {
		return b.native.PreviewAttack(ctx, id, command)
	}
	n, ok := b.native.(subdueNative)
	if !ok {
		return nil, bridge.Result{}, executor.ErrHeld
	}
	return n.PreviewPawnOrder(ctx, id, subdueCommand(command))
}
func (b *MeleeBoundary) execute(ctx context.Context, pre *a.WritePrecondition, command *o.AttackTarget, subdue bool) (*o.ExecuteReply, bridge.Result, error) {
	if !subdue {
		return b.writer.AttackTarget(ctx, pre, command)
	}
	w, ok := b.writer.(subdueWriter)
	if !ok {
		return nil, bridge.Result{}, executor.ErrHeld
	}
	return w.Subdue(ctx, pre, subdueCommand(command))
}
func (b *MeleeBoundary) lookup(ctx context.Context, attempt bridge.AttackAttempt, subdue bool) (*r.LookupReply, bridge.Result, error) {
	if !subdue {
		return b.native.LookupAttackAttempt(ctx, attempt)
	}
	n, ok := b.native.(subdueNative)
	if !ok {
		return nil, bridge.Result{}, executor.ErrHeld
	}
	return n.LookupPawnOrderAttempt(ctx, subdueAttempt(attempt))
}
func (b *MeleeBoundary) observe(ctx context.Context, attempt bridge.AttackAttempt, receipt *r.Receipt, subdue bool) (*r.ProgressReply, bridge.Result, error) {
	if !subdue {
		return b.native.ObserveAttackProgress(ctx, attempt, receipt)
	}
	n, ok := b.native.(subdueNative)
	if !ok {
		return nil, bridge.Result{}, executor.ErrHeld
	}
	return n.ObservePawnOrderProgress(ctx, subdueAttempt(attempt), receipt)
}
