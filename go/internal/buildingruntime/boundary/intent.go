package boundary

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
)

// ActionsWriter narrows *bridge.ActionsWriter.
type ActionsWriter interface {
	Apply(context.Context, *c.Identity, []*o.Action) (*o.ApplyReply, bridge.Result, error)
}

// IntentKey is an intent-mode attempt's idempotency key: the action and attempt.
// A stockpile rectangle uses only its immutable action ID so a lost reply
// recovers every created identity from native replay. The action, not the plan, keys it:
// a plan's building actions each start at attempt 1, and the native replay
// window would answer the second with the first's result. Native stamps a
// building intent's key on what it builds, which is how the construction
// census names the owning action (store.ConstructionClaims).
func IntentKey(p executor.Placement) string {
	if batch, ok := p.Action.CombatBatch(); ok {
		return batch.Key()
	}
	if z, ok := p.Action.ZoneCreate(); ok && z.Kind() == domain.StockpileZone {
		return string(p.Action.ID())
	}
	return fmt.Sprintf("%s/%d", p.Action.ID(), p.Attempt)
}

// DispatchIntent sends one intent-mode action through Actions/Apply: the
// one-element case of DispatchIntents.
func (b *Boundary) DispatchIntent(ctx context.Context, p executor.Placement, writer ActionsWriter) (executor.Receipt, error) {
	return DispatchIntent(ctx, b.Leases, p, writer)
}

// DispatchIntent is Boundary.DispatchIntent for a family boundary that keeps
// only a lease source.
func DispatchIntent(ctx context.Context, leases LeaseSource, p executor.Placement, writer ActionsWriter) (executor.Receipt, error) {
	out, err := DispatchIntents(ctx, leases, []executor.Placement{p}, writer)
	return out[0], err
}

// DispatchIntents sends intent-mode actions of one world in one
// Actions/Apply call. Only the identity is checked on the wire;
// native validates each intent against live state. Receipts are in input
// order: applied is accepted; refused and failed are refused, except an
// attempt conflict; a batch failure or a lost reply leaves every receipt
// unknown, which an idempotent intent may resend.
func DispatchIntents(ctx context.Context, leases LeaseSource, placements []executor.Placement, writer ActionsWriter) ([]executor.Receipt, error) {
	out := make([]executor.Receipt, len(placements))
	for i, p := range placements {
		out[i] = executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	}
	if len(placements) == 0 {
		return out, nil
	}
	snapshot := placements[0].Snapshot
	actions := make([]*o.Action, len(placements))
	for i, p := range placements {
		if !World(p.Snapshot, snapshot) {
			return out, executor.ErrAuthority
		}
		action, err := bridge.IntentAction(IntentKey(p), p.Action)
		if err != nil {
			return out, err
		}
		actions[i] = action
	}
	lease, err := leases.Lease(snapshot)
	if err != nil {
		return out, err
	}
	if !ValidID(lease) {
		return out, executor.ErrAuthority
	}
	reply, _, err := writer.Apply(ctx, Identity(snapshot), actions)
	if err != nil {
		return out, err
	}
	results := reply.GetResults()
	if len(results) != len(placements) {
		return out, executor.ErrEvidence
	}
	for i, result := range results {
		switch {
		case result.GetApplied().GetApplied() != nil:
			out[i].Kind = domain.ReceiptAccepted
			if placements[i].Action.Kind() == domain.CombatBatchAction {
				results, err := bridge.CombatOrderResults(result.GetApplied(), actions[i].GetCombatOrders())
				if err != nil {
					return out, err
				}
				for _, r := range results {
					bridge.RecordCombatOrder(ctx, r)
					out[i].Combat = append(out[i].Combat, domain.CombatResult{Index: r.Index, PawnID: r.PawnID, Applied: r.Applied, Refusal: r.Refusal, JobDef: r.JobDef})
				}
			}
			if placements[i].Action.Kind() == domain.ZoneCreateAction {
				effect := result.GetApplied().GetApplied().GetObserved().GetZone()
				zone, _ := placements[i].Action.ZoneCreate()
				if zone.Kind() == domain.StockpileZone {
					for _, row := range effect.GetCreated() {
						created := domain.CreatedZone{ID: row.GetZoneId()}
						for _, c := range row.GetCells() {
							if c == nil || c.X == nil || c.Z == nil {
								return out, executor.ErrEvidence
							}
							created.Cells = append(created.Cells, domain.Cell{X: c.GetX(), Z: c.GetZ()})
						}
						out[i].Stockpiles = append(out[i].Stockpiles, created)
					}
					if _, err := domain.NewCreatedZones(zone, out[i].Stockpiles); err != nil {
						return out, executor.ErrEvidence
					}
				} else {
					out[i].Zone = effect.GetZoneId()
				}
			}
			if kind := placements[i].Action.Kind(); kind == domain.ProductionBillAction || kind == domain.SurgeryAction {
				out[i].Bill = appliedBill(result.GetApplied().GetApplied().GetObserved())
			}
		case result.GetRefused() != nil:
			out[i].Kind = domain.ReceiptRefused
			out[i].Refusal = nativeRefusal(result.GetRefused().GetCode(), result.GetRefused().GetReason(), result.GetRefused().GetRefusalClass())
		case result.GetFailed() != nil && result.GetFailed().GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT:
			out[i].Kind = domain.ReceiptRefused
			out[i].Refusal = nativeRefusal(result.GetFailed().GetCode(), result.GetFailed().GetDetail(), result.GetFailed().GetRefusalClass())
		}
	}
	return out, nil
}

// appliedBill is the native bill id a bill-placing receipt's evidence names:
// BillEffect for an ordinary or mech bill, SurgeryEffect for a medical one.
func appliedBill(evidence *r.EffectEvidence) string {
	if id := evidence.GetBill().GetBill().GetId(); id != "" {
		return id
	}
	return evidence.GetSurgeryBill().GetBill().GetId()
}

// nativeRefusal is a refused receipt's journal record: native's failure
// code, its reason text and the class it assigned. A refusal that names no
// class is unknown.
func nativeRefusal(code c.FailureCode, reason string, class c.RefusalClass) *domain.NativeRefusal {
	refusal := &domain.NativeRefusal{Code: code.String(), Reason: reason, Class: domain.RefusalUnknown}
	switch class {
	case c.RefusalClass_REFUSAL_CLASS_PERMANENT:
		refusal.Class = domain.RefusalPermanent
	case c.RefusalClass_REFUSAL_CLASS_TRANSIENT:
		refusal.Class = domain.RefusalTransient
	}
	return refusal
}
