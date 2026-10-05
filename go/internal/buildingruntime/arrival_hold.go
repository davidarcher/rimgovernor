package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// ArrivalPawns reads live pawn positions and jobs for the arrival hold.
type ArrivalPawns interface {
	ReadCombatPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
}

// arrivalMove is one completed move an undispatched action depends on.
type arrivalMove struct {
	plan   domain.PlanID
	action domain.Action
}

// arrivalHolds names the undispatched actions that depend on a completed
// move whose pawn does not yet stand on its destination. A move's receipt
// only says the order was given; the shrine plan's breach or opening waits
// here, in the worker, until every mover has arrived. A drafted pawn idle
// away from its cell has lost its order, so its move is sent again (the
// intent is idempotent). An unreadable position holds.
func (w *Worker) arrivalHolds(ctx context.Context, current domain.GenerationSnapshot, plans []store.PlanState) map[domain.ActionID]bool {
	held := map[domain.ActionID]bool{}
	gated := map[domain.ActionID][]arrivalMove{}
	pawns := map[string]bool{}
	var ids []string
	for _, plan := range plans {
		if plan.Retired {
			continue
		}
		byID := map[domain.ActionID]domain.Progress{}
		for _, p := range plan.Progress {
			byID[p.View().Action] = p
		}
		for _, dependency := range plan.Spec.Dependencies() {
			waiting, ok := byID[dependency.Action]
			required, known := byID[dependency.Requires]
			if !ok || !known {
				continue
			}
			v := waiting.View()
			if v.Unresolved || v.Stage != domain.Pending && v.Stage != domain.Prepared {
				continue
			}
			m, isMove := required.Action().Movement()
			if !isMove || required.View().Stage != domain.Completed {
				continue
			}
			gated[v.Action] = append(gated[v.Action], arrivalMove{plan.Spec.ID(), required.Action()})
			if !pawns[string(m.Pawn())] {
				pawns[string(m.Pawn())] = true
				ids = append(ids, string(m.Pawn()))
			}
		}
	}
	if len(gated) == 0 {
		return held
	}
	rows, tick := w.arrivalRows(ctx, current, ids)
	resent := map[domain.ActionID]bool{}
	for waiting, moves := range gated {
		for _, move := range moves {
			m, _ := move.action.Movement()
			row, ok := rows[string(m.Pawn())]
			if !ok {
				held[waiting] = true
				continue
			}
			if at, known := breakCell(row).Value(); known && at == m.Destination() {
				continue
			}
			held[waiting] = true
			if row.GetDrafted() && row.GetJob().GetDefName() != "Goto" && !resent[move.action.ID()] && w.config.Moves != nil {
				resent[move.action.ID()] = true
				w.resendMove(ctx, current, move, tick)
			}
		}
	}
	return held
}

func (w *Worker) arrivalRows(ctx context.Context, current domain.GenerationSnapshot, ids []string) (map[string]*n.PawnState, int64) {
	rows := map[string]*n.PawnState{}
	if w.config.Pawns == nil {
		return rows, 0
	}
	reply, _, err := w.config.Pawns.ReadCombatPawns(ctx, boundary.Identity(current), ids)
	if err != nil {
		return rows, 0
	}
	observed := reply.GetObserved()
	if observed == nil {
		return rows, 0
	}
	if _, err = boundary.Context(observed.Context, current); err != nil {
		return rows, 0
	}
	for _, row := range observed.Pawns {
		rows[row.GetPawn().GetId()] = row
	}
	return rows, observed.Context.GetTick()
}

func (w *Worker) resendMove(ctx context.Context, current domain.GenerationSnapshot, move arrivalMove, tick int64) {
	key := fmt.Sprintf("%s/%s/%d", move.plan, move.action.ID(), tick)
	// A failed resend is retried at the next arrival read.
	if action, err := bridge.IntentAction(key, move.action); err == nil {
		_, _, _ = w.config.Moves.Apply(ctx, boundary.Identity(current), []*o.Action{action})
	}
}
