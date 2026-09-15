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

// ZoneEditTarget is a fresh read of one existing native zone's exact CAS
// snapshot token, the same scoped-refresh shape BuildingTemperatureTarget
// uses for a single settable-entity patch.
type ZoneEditTarget struct {
	Context *c.ObservationContext
	ZoneID  string
	Token   string
}

// ReadZoneEditTarget observes one exact zone's current CAS snapshot token by
// its native identity, mirroring ReadConstructionBuildings' exact-identity
// list-and-verify shape.
func (client *Client) ReadZoneEditTarget(ctx context.Context, identity *c.Identity, zoneID string) (ZoneEditTarget, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return ZoneEditTarget{}, Result{}, err
	}
	if validID(zoneID) != nil {
		return ZoneEditTarget{}, Result{}, contract("invalid zone edit target identity")
	}
	request := &o.ListZonesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Ids: []string{zoneID}, Page: &c.PageRequest{Limit: proto.Uint32(1)}}
	reply := &o.ListZonesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_zones", request, reply)
	if err != nil {
		return ZoneEditTarget{}, raw, err
	}
	if buildingUnknown(reply) != nil {
		return ZoneEditTarget{}, raw, contract("unknown zone edit target fields")
	}
	switch v := reply.Outcome.(type) {
	case *o.ListZonesReply_Failure:
		return ZoneEditTarget{}, raw, failure(v.Failure, raw)
	case *o.ListZonesReply_Unavailable:
		return ZoneEditTarget{}, raw, unavailable(v.Unavailable, raw)
	case *o.ListZonesReply_Observed:
		if err = buildingContext(v.Observed.Context, identity, 0, false); err != nil {
			return ZoneEditTarget{}, raw, err
		}
		if len(v.Observed.Zones) != 1 {
			return ZoneEditTarget{}, raw, contract("zone edit target missing or ambiguous")
		}
		zone := v.Observed.Zones[0]
		snapshot := zone.GetSnapshot()
		if zone.GetId() != zoneID || snapshot == nil || snapshot.GetEntityId() != zoneID || validID(snapshot.GetToken()) != nil {
			return ZoneEditTarget{}, raw, contract("invalid zone edit target readback")
		}
		return ZoneEditTarget{Context: v.Observed.Context, ZoneID: zoneID, Token: snapshot.GetToken()}, raw, nil
	default:
		return ZoneEditTarget{}, raw, contract("zone edit target outcome missing")
	}
}

type ZoneEditAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Edit       domain.ZoneEdit
}
type ZoneEditControl struct{ client *Client }

func NewZoneEditControl(client *Client) (*ZoneEditControl, error) {
	if client == nil {
		return nil, contract("zone edit client missing")
	}
	return &ZoneEditControl{client}, nil
}
func validZoneEdit(edit domain.ZoneEdit) error {
	_, err := domain.ReconstructZoneEdit(edit)
	return err
}
func zoneEditCellList(cells []domain.Cell) *op.Cells {
	list := &op.CellList{}
	for _, cell := range cells {
		list.Cells = append(list.Cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	return &op.Cells{Selection: &op.Cells_ExplicitCells{ExplicitCells: list}}
}
func zoneEditOperation(edit domain.ZoneEdit) *op.Operation {
	precondition := &op.EntityPrecondition{EntityId: proto.String(edit.ZoneID()), ExpectedSnapshotToken: proto.String(edit.BeforeToken())}
	switch edit.Op() {
	case domain.ZoneEditAdd:
		return &op.Operation{Command: &op.Operation_EditZoneCells{EditZoneCells: &op.EditZoneCells{Zone: precondition, Edit: op.CellEdit_CELL_EDIT_ADD.Enum(), Cells: zoneEditCellList(edit.Cells())}}}
	case domain.ZoneEditRemove:
		return &op.Operation{Command: &op.Operation_EditZoneCells{EditZoneCells: &op.EditZoneCells{Zone: precondition, Edit: op.CellEdit_CELL_EDIT_REMOVE.Enum(), Cells: zoneEditCellList(edit.Cells())}}}
	default:
		return &op.Operation{Command: &op.Operation_DeleteZone{DeleteZone: &op.DeleteZone{Zone: precondition}}}
	}
}
func (client *Client) PreviewZoneEdit(ctx context.Context, identity *c.Identity, edit domain.ZoneEdit) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validZoneEdit(edit) != nil {
		return nil, Result{}, contract("invalid zone edit preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: zoneEditOperation(edit)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone edit preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || buildingContext(v.Context, identity, 0, false) != nil || v.Projected != nil {
		return nil, raw, contract("invalid zone edit preview evidence")
	}
	return reply, raw, nil
}
func (writer *ZoneEditControl) ApplyZoneEdit(ctx context.Context, pre *a.WritePrecondition, edit domain.ZoneEdit) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validZoneEdit(edit) != nil {
		return nil, Result{}, contract("invalid zone edit execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: zoneEditOperation(edit)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone edit execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("zone edit owner mismatch")
	}
	err = zoneEditReceipt(v, ZoneEditAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Edit: edit})
	return reply, raw, err
}
func validZoneEditAttempt(w ZoneEditAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid zone edit attempt")
	}
	return validZoneEdit(w.Edit)
}

// ZoneEditMatches decodes one zone-edit effect readback: for delete it
// requires the zone now absent; for add/remove it requires every requested
// delta cell to appear exactly once, accepted, matching ZoneCreate's
// ZoneMatches decoding of the same ZoneEffect message.
func ZoneEditMatches(v *r.EffectEvidence, edit domain.ZoneEdit) (bool, error) {
	d := v.GetZone()
	if d == nil || buildingUnknown(v) != nil || d.Snapshot == nil || validID(d.GetZoneId()) != nil || d.GetZoneId() != edit.ZoneID() || d.Snapshot.GetEntityId() != edit.ZoneID() || d.Snapshot.GetBeforeToken() != edit.BeforeToken() || d.Present == nil {
		return false, contract("invalid zone edit readback")
	}
	if edit.Op() == domain.ZoneEditDelete {
		if len(d.Cells) != 0 {
			return false, contract("invalid zone delete cell readback")
		}
		return !d.GetPresent(), nil
	}
	if !d.GetPresent() || validID(d.Snapshot.GetAfterToken()) != nil || d.ListedCellCount == nil || d.GridCellCount == nil || d.PhantomCellCount == nil || d.ChangedCells == nil || d.GetPhantomCellCount() < 0 || d.GetPhantomCellCount() > d.GetListedCellCount() {
		return false, contract("invalid zone edit cell readback")
	}
	requested := edit.Cells()
	seen := map[domain.Cell]bool{}
	accepted := true
	for _, row := range d.Cells {
		if row == nil || row.Cell == nil || row.Cell.X == nil || row.Cell.Z == nil || row.Cell.GetX() < 0 || row.Cell.GetZ() < 0 || row.Accepted == nil {
			return false, contract("invalid zone edit cell")
		}
		cell := domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}
		if seen[cell] {
			return false, contract("duplicate zone edit cell")
		}
		seen[cell] = true
		accepted = accepted && row.GetAccepted()
	}
	matches := accepted && len(d.Cells) == len(requested) && d.GetChangedCells() == int32(len(requested))
	for _, cell := range requested {
		matches = matches && seen[cell]
	}
	return matches, nil
}
func zoneEditEffect(v *r.EffectEvidence, w ZoneEditAttempt) error {
	_, err := ZoneEditMatches(v, w.Edit)
	return err
}
func zoneEditReceipt(v *r.Receipt, w ZoneEditAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("zone edit admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return zoneEditEffect(out.Applied.GetObserved(), w)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("zone edit uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return zoneEditEffect(out.Uncertain.LastObserved, w)
		}
		return nil
	default:
		return contract("unsupported zone edit receipt")
	}
}
func (client *Client) LookupZoneEdit(ctx context.Context, w ZoneEditAttempt) (*r.LookupReply, Result, error) {
	if err := validZoneEditAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone edit lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = zoneEditReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("zone edit in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("zone edit lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveZoneEdit(ctx context.Context, w ZoneEditAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validZoneEditAttempt(w) != nil || zoneEditReceipt(admitted, w) != nil {
		return nil, Result{}, contract("zone edit observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone edit progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("zone edit progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing zone edit uncertainty")
		}
	case *r.Progress_Completed:
		matches, check := ZoneEditMatches(out.Completed.GetEvidence(), w.Edit)
		err = check
		if !v.GetCompleteInspection() || !matches {
			return nil, raw, contract("unverified zone edit completion")
		}
	case *r.Progress_Unsuccessful:
		matches, check := ZoneEditMatches(out.Unsuccessful.GetEvidence(), w.Edit)
		err = check
		if !v.GetCompleteInspection() || matches || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified zone edit failure")
		}
	default:
		err = contract("unsupported zone edit progress")
	}
	return reply, raw, err
}
