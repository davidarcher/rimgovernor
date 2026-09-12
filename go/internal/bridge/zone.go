package bridge

import (
	"context"
	"crypto/sha256"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type ZoneTarget struct {
	Zone  domain.ZoneCreate
	Token string
}
type ZoneRead struct {
	Context *c.ObservationContext
	Token   string
}
type ZoneAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Owner      *a.Owner
	Generation uint64
	Token      string
	Zone       domain.ZoneCreate
}
type ZoneControl struct{ client *Client }

func NewZoneControl(client *Client) (*ZoneControl, error) {
	if client == nil {
		return nil, contract("zone client missing")
	}
	return &ZoneControl{client}, nil
}
func (client *Client) ReadZoneTarget(ctx context.Context, identity *c.Identity, zone domain.ZoneCreate) (ZoneRead, Result, error) {
	reply, raw, err := client.ReadColonyFacts(ctx, identity, true, []string{zone.Crop()})
	if err != nil {
		return ZoneRead{}, raw, err
	}
	v := reply.GetObserved()
	snapshot := v.GetPlanning().GetObserved().GetZoneMapSnapshot()
	if snapshot == nil {
		return ZoneRead{}, raw, ErrUnavailable
	}
	return ZoneRead{Context: proto.Clone(v.Context).(*c.ObservationContext), Token: snapshot.GetToken()}, raw, nil
}
func ZoneConfiguration(zone domain.ZoneCreate) *op.CreateZone {
	cells := &op.CellList{}
	for _, cell := range zone.Cells() {
		cells.Cells = append(cells.Cells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
	}
	return &op.CreateZone{Type: op.ZoneType_ZONE_TYPE_GROWING.Enum(), Label: proto.String(zone.Label()), Cells: &op.Cells{Selection: &op.Cells_ExplicitCells{ExplicitCells: cells}}, Growing: &op.GrowingSettings{PlantDef: proto.String(zone.Crop()), AllowSow: proto.Bool(true), AllowCut: proto.Bool(true)}}
}
func ZoneConfigurationToken(zone domain.ZoneCreate) string {
	data, _ := (proto.MarshalOptions{Deterministic: true}).Marshal(ZoneConfiguration(zone))
	sum := sha256.Sum256(data)
	return fmt.Sprintf("zone-%x", sum)
}
func zoneOperation(target ZoneTarget) *op.Operation {
	command := ZoneConfiguration(target.Zone)
	command.ExpectedMapSnapshotToken = proto.String(target.Token)
	return &op.Operation{Command: &op.Operation_CreateZone{CreateZone: command}}
}
func validZone(target ZoneTarget) error {
	if _, err := domain.NewZoneCreate(target.Zone.Kind(), target.Zone.Crop(), target.Zone.Cells()); err != nil {
		return err
	}
	return validID(target.Token)
}
func (client *Client) PreviewZone(ctx context.Context, identity *c.Identity, target ZoneTarget) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validZone(target) != nil {
		return nil, Result{}, contract("invalid zone preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: zoneOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || buildingContext(v.Context, identity, 0, false) != nil || v.Projected != nil {
		return nil, raw, contract("invalid zone preview evidence")
	}
	return reply, raw, nil
}
func (writer *ZoneControl) CreateZone(ctx context.Context, pre *a.WritePrecondition, target ZoneTarget) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validID(pre.GetLeaseId()) != nil || validZone(target) != nil {
		return nil, Result{}, contract("invalid zone execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: zoneOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	// The runtime additionally compares full owner/direction against its admission.
	v := reply.GetReceipt()
	if v == nil || v.AuthorizingOwner == nil || v.AuthorizingOwner.GetControllerSessionId() != pre.Attempt.GetControllerSessionId() {
		return nil, raw, contract("zone owner mismatch")
	}
	err = zoneReceipt(v, ZoneAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Owner: v.AuthorizingOwner, Generation: pre.GetExpectedGeneration(), Token: target.Token, Zone: target.Zone})
	return reply, raw, err
}
func validZoneAttempt(w ZoneAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || authorityOwner(w.Owner) != nil || buildingUnknown(w.Owner) != nil || w.Generation == 0 || w.Owner.GetControllerSessionId() != w.Attempt.GetControllerSessionId() {
		return contract("invalid zone attempt")
	}
	return validZone(ZoneTarget{w.Zone, w.Token})
}
func ZoneMatches(v *r.EffectEvidence, zone domain.ZoneCreate, token string) (bool, error) {
	d := v.GetZone()
	if d == nil || buildingUnknown(v) != nil || d.Snapshot == nil || validID(d.GetZoneId()) != nil || d.Snapshot.GetEntityId() != d.GetZoneId() || d.Snapshot.GetBeforeToken() != token || d.Present == nil || d.ListedCellCount == nil || d.GridCellCount == nil || d.PhantomCellCount == nil || d.ChangedCells == nil || d.GetListedCellCount() != int32(len(d.Cells)) || len(d.Cells) > 256 || d.GetGridCellCount() < 0 || d.GetPhantomCellCount() < 0 || d.GetPhantomCellCount() > d.GetListedCellCount() || d.GetChangedCells() < 0 {
		return false, contract("invalid zone readback")
	}
	seen := map[domain.Cell]bool{}
	accepted := true
	for _, row := range d.Cells {
		if row == nil || row.Cell == nil || row.Cell.X == nil || row.Cell.Z == nil || row.Cell.GetX() < 0 || row.Cell.GetZ() < 0 || row.Accepted == nil || row.TakenFromZoneId != nil {
			return false, contract("invalid zone cell")
		}
		cell := domain.Cell{X: row.Cell.GetX(), Z: row.Cell.GetZ()}
		if seen[cell] {
			return false, contract("duplicate zone cell")
		}
		seen[cell] = true
		accepted = accepted && row.GetAccepted()
	}
	matches := d.GetPresent() && accepted && d.GetPhantomCellCount() == 0 && d.GetGridCellCount() == d.GetListedCellCount() && len(d.Cells) == len(zone.Cells()) && d.Snapshot.GetAfterToken() == ZoneConfigurationToken(zone)
	for _, cell := range zone.Cells() {
		matches = matches && seen[cell]
	}
	return matches, nil
}
func zoneEffect(v *r.EffectEvidence, w ZoneAttempt) error {
	_, err := ZoneMatches(v, w.Zone, w.Token)
	return err
}
func zoneReceipt(v *r.Receipt, w ZoneAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || !proto.Equal(v.AuthorizingOwner, w.Owner) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("zone admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return zoneEffect(out.Applied.GetObserved(), w)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("zone uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return zoneEffect(out.Uncertain.LastObserved, w)
		}
		return nil
	default:
		return contract("unsupported zone receipt")
	}
}
func (client *Client) LookupZone(ctx context.Context, w ZoneAttempt) (*r.LookupReply, Result, error) {
	if err := validZoneAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = zoneReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("zone in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("zone lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveZone(ctx context.Context, w ZoneAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validZoneAttempt(w) != nil || zoneReceipt(admitted, w) != nil {
		return nil, Result{}, contract("zone observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("zone progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing zone uncertainty")
		}
	case *r.Progress_Completed:
		matches, check := ZoneMatches(out.Completed.GetEvidence(), w.Zone, w.Token)
		err = check
		if !v.GetCompleteInspection() || !matches {
			return nil, raw, contract("unverified zone completion")
		}
	case *r.Progress_Unsuccessful:
		matches, check := ZoneMatches(out.Unsuccessful.GetEvidence(), w.Zone, w.Token)
		err = check
		if !v.GetCompleteInspection() || matches || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified zone failure")
		}

	default:
		err = contract("unsupported zone progress")
	}
	return reply, raw, err
}
