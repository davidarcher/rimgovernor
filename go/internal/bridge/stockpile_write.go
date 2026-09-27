package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// StockpileWriteAttempt scopes one admission of either stockpile write
// kind: a zone cell edit (native EditZoneCells) or a stockpile patch
// (native PatchStockpile on a zone or a storage building).
type StockpileWriteAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Action     domain.Action
}
type StockpileWriteControl struct{ client *Client }

func NewStockpileWriteControl(client *Client) (*StockpileWriteControl, error) {
	if client == nil {
		return nil, contract("stockpile write client missing")
	}
	return &StockpileWriteControl{client}, nil
}

// StockpileWriteTarget is the id and CAS token a stockpile write names.
func StockpileWriteTarget(action domain.Action) (target, before string, ok bool) {
	if e, ok := action.ZoneCellEdit(); ok {
		return e.Zone(), e.BeforeToken(), true
	}
	if p, ok := action.StockpilePatch(); ok {
		return p.Target(), p.BeforeToken(), true
	}
	return "", "", false
}

func stockpileWriteOperation(action domain.Action) (*op.Operation, error) {
	if e, ok := action.ZoneCellEdit(); ok {
		if _, err := domain.NewZoneCellEditAction(action.ID(), e); err != nil {
			return nil, contract("invalid zone cell edit")
		}
		mode := op.CellEdit_CELL_EDIT_ADD
		if e.Mode() == domain.RemoveZoneCells {
			mode = op.CellEdit_CELL_EDIT_REMOVE
		}
		var cells []*c.Cell
		for _, cell := range e.Cells() {
			cells = append(cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
		}
		return &op.Operation{Command: &op.Operation_EditZoneCells{EditZoneCells: &op.EditZoneCells{
			Zone: &op.EntityPrecondition{EntityId: proto.String(e.Zone()), ExpectedSnapshotToken: proto.String(e.BeforeToken())},
			Edit: mode.Enum(), Cells: &op.Cells{Selection: &op.Cells_ExplicitCells{ExplicitCells: &op.CellList{Cells: cells}}},
		}}}, nil
	}
	if p, ok := action.StockpilePatch(); ok {
		if _, err := domain.NewStockpilePatchAction(action.ID(), p); err != nil {
			return nil, contract("invalid stockpile patch")
		}
		return &op.Operation{Command: &op.Operation_PatchStockpile{PatchStockpile: &op.PatchStockpile{
			Zone:     &op.EntityPrecondition{EntityId: proto.String(p.Target()), ExpectedSnapshotToken: proto.String(p.BeforeToken())},
			Settings: StockpileSettings(p.Filter(), p.Priority()),
		}}}, nil
	}
	return nil, contract("not a stockpile write")
}

func (client *Client) PreviewStockpileWrite(ctx context.Context, identity *c.Identity, action domain.Action) (*op.PreviewReply, Result, error) {
	operation, err := stockpileWriteOperation(action)
	if ValidateIdentity(identity) != nil || err != nil {
		return nil, Result{}, contract("invalid stockpile write preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: operation}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown stockpile write preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || v.Projected != nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid stockpile write preview evidence")
	}
	return reply, raw, nil
}

func (writer *StockpileWriteControl) ApplyStockpileWrite(ctx context.Context, pre *a.WritePrecondition, action domain.Action) (*op.ExecuteReply, Result, error) {
	operation, err := stockpileWriteOperation(action)
	if writer == nil || writer.client == nil || pre == nil || err != nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid stockpile write execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: operation}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown stockpile write execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	if reply.GetReceipt() == nil {
		return nil, raw, contract("stockpile write receipt missing")
	}
	return reply, raw, stockpileWriteReceipt(reply.GetReceipt(), StockpileWriteAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), action})
}

// StockpileWriteEvidence validates a stockpile write's effect evidence
// names its target and before token: zone evidence for a cell edit or a
// zone patch, settings evidence for a storage building patch.
func StockpileWriteEvidence(v *r.EffectEvidence, action domain.Action) error {
	target, before, ok := StockpileWriteTarget(action)
	if !ok || v == nil || buildingUnknown(v) != nil {
		return contract("invalid stockpile write readback")
	}
	if p, isPatch := action.StockpilePatch(); isPatch && p.TargetKind() == domain.StorageBuildingTarget {
		s := v.GetSettings()
		if s == nil || s.Snapshot == nil || s.Snapshot.GetEntityId() != target || s.Snapshot.GetBeforeToken() != before {
			return contract("invalid storage building readback")
		}
		return nil
	}
	z := v.GetZone()
	if z == nil || z.Snapshot == nil || z.GetZoneId() != target || z.Snapshot.GetEntityId() != target || z.Snapshot.GetBeforeToken() != before || z.Present == nil {
		return contract("invalid stockpile zone readback")
	}
	return nil
}

func validStockpileWriteAttempt(w StockpileWriteAttempt) error {
	if _, err := stockpileWriteOperation(w.Action); err != nil || ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid stockpile write attempt")
	}
	return nil
}

func stockpileWriteReceipt(v *r.Receipt, w StockpileWriteAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("stockpile write admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return StockpileWriteEvidence(out.Applied.GetObserved(), w.Action)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("stockpile write uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return StockpileWriteEvidence(out.Uncertain.LastObserved, w.Action)
		}
		return nil
	default:
		return contract("unsupported stockpile write receipt")
	}
}

func (client *Client) LookupStockpileWrite(ctx context.Context, w StockpileWriteAttempt) (*r.LookupReply, Result, error) {
	if err := validStockpileWriteAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown stockpile write lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = stockpileWriteReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("stockpile write in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("stockpile write lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveStockpileWrite(ctx context.Context, w StockpileWriteAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validStockpileWriteAttempt(w) != nil || stockpileWriteReceipt(admitted, w) != nil {
		return nil, Result{}, contract("stockpile write observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown stockpile write progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("stockpile write progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing stockpile write uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete stockpile write completion")
		}
		err = StockpileWriteEvidence(out.Completed.GetEvidence(), w.Action)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified stockpile write failure")
		}
		err = StockpileWriteEvidence(out.Unsuccessful.GetEvidence(), w.Action)
	default:
		err = contract("unsupported stockpile write progress")
	}
	return reply, raw, err
}

// StorageBuildingTarget is one player storage building's (shelf's) storage
// CAS token, the settings snapshot its building listing row carries.
type StorageBuildingTarget struct {
	Context *c.ObservationContext
	Thing   string
	Present bool
	Token   string
}

// ReadStorageBuildingTarget observes one exact storage building's storage
// token via the building listing; an absent building reads back as not
// present rather than as an error.
func (client *Client) ReadStorageBuildingTarget(ctx context.Context, identity *c.Identity, thing string) (StorageBuildingTarget, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return StorageBuildingTarget{}, Result{}, err
	}
	if validID(thing) != nil {
		return StorageBuildingTarget{}, Result{}, contract("invalid storage building identity")
	}
	request := &o.ListBuildingsRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Ids: []string{thing}, Statuses: []string{"built"}}
	reply := &o.ListBuildingsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_buildings", request, reply)
	if err != nil {
		return StorageBuildingTarget{}, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return StorageBuildingTarget{}, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ListBuildingsReply_Failure:
		return StorageBuildingTarget{}, raw, failure(v.Failure, raw)
	case *o.ListBuildingsReply_Unavailable:
		return StorageBuildingTarget{}, raw, unavailable(v.Unavailable, raw)
	case *o.ListBuildingsReply_Observed:
		if err = ValidateConstructionBuildings(v.Observed, identity, request.Ids); err != nil {
			return StorageBuildingTarget{}, raw, err
		}
	default:
		return StorageBuildingTarget{}, raw, contract("building read outcome missing")
	}
	v := reply.GetObserved()
	out := StorageBuildingTarget{Context: v.Context, Thing: thing}
	if len(v.Buildings) == 0 {
		return out, raw, nil
	}
	row := v.Buildings[0]
	settings := row.GetSettings()
	if len(v.Buildings) != 1 || row.GetBuilding().GetId() != thing || settings == nil || settings.Snapshot == nil || settings.Snapshot.GetEntityId() != thing || validID(settings.Snapshot.GetToken()) != nil {
		return StorageBuildingTarget{}, raw, contract("storage building target missing settings")
	}
	out.Present, out.Token = true, settings.Snapshot.GetToken()
	return out, raw, nil
}
