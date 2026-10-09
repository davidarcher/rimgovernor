package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"testing"
	"time"
)

type combatTestWriter interface {
	CombatOrders(context.Context, *c.Identity, string, *op.CombatOrders) ([]bridge.CombatOrderResult, error)
}
type combatTestBoundary struct {
	r      *Rounder
	native RoundsDefenseSource
}

func (b combatTestBoundary) InspectIntent(ctx context.Context, target executor.Target) (executor.IntentInspection, error) {
	now := b.r.clock.Now()
	combat, err := b.native.ReadCombat(ctx, boundary.Identity(target.Snapshot))
	if err != nil {
		return executor.IntentInspection{}, err
	}
	return executor.IntentInspection{Current: target.Snapshot, Tick: domain.Tick(combat.Context.GetTick()), StartedAt: now, ObservedAt: now}, nil
}
func (b combatTestBoundary) WriteIntents(ctx context.Context, ps []executor.Placement) ([]executor.Receipt, error) {
	var out []executor.Receipt
	for _, p := range ps {
		wire, err := bridge.IntentAction(boundary.IntentKey(p), p.Action)
		if err != nil {
			return nil, err
		}
		writer := b.native.(combatTestWriter)
		rows, err := writer.CombatOrders(ctx, boundary.Identity(p.Snapshot), wire.GetKey(), wire.GetCombatOrders())
		if err != nil {
			return nil, err
		}
		result := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptAccepted}
		for _, v := range rows {
			result.Combat = append(result.Combat, domain.CombatResult{Index: v.Index, PawnID: v.PawnID, Applied: v.Applied, Refusal: v.Refusal, JobDef: v.JobDef})
		}
		out = append(out, result)
	}
	return out, nil
}
func newCombatTestPlanner(t *testing.T, r *Rounder, native RoundsDefenseSource) (*RoundsDefensePlanner, error) {
	t.Helper()
	hands, err := executor.New(r.player.journal, combatTestBoundary{r, native}, r.clock, executor.Limits{MaxAge: time.Minute, RunTimeout: time.Minute, JournalTimeout: time.Minute}, r.player.journal)
	if err != nil {
		return nil, err
	}
	if err = hands.UpdateAuthority(executor.Authority{Snapshot: r.player.session.State().Snapshot, Enabled: true}); err != nil {
		return nil, err
	}
	return NewRoundsDefensePlanner(r, native, hands)
}
