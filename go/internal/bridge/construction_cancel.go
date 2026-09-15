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

// ConstructionCancelTarget is the exact pending construction order one cell
// currently holds, with its CAS snapshot token. Present false is a complete
// observation that no matching order is there any more -- a built building, a
// player removal, or an order someone else already cancelled -- and is the
// bridge form of Python execute_target's 'observed_absent' result, not an
// error.
type ConstructionCancelTarget struct {
	Context *c.ObservationContext
	ThingID string
	Token   string
	Present bool
}

// ReadConstructionCancelTarget observes the one player blueprint or frame at
// this placement's exact cell whose definition and material match it.
//
// Unlike ReadZoneEditTarget, which asks for an entity by identity, this asks by
// geometry: a pending order's thing ID changes when its blueprint becomes a
// frame, so the cell/def/stuff triple is the stable handle. This is the same
// query shape Python's capture_targets uses (home/list_buildings at the
// placement, radius one, playerOnly), narrowed to the two pending statuses so a
// completed building can never be selected. An ambiguous cell -- more than one
// matching pending order -- is refused rather than guessed at.
func (client *Client) ReadConstructionCancelTarget(ctx context.Context, identity *c.Identity, cancel domain.ConstructionCancel) (ConstructionCancelTarget, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return ConstructionCancelTarget{}, Result{}, err
	}
	if validConstructionCancel(cancel) != nil {
		return ConstructionCancelTarget{}, Result{}, contract("invalid construction cancel target")
	}
	cell := cancel.Cell()
	corner := func() *c.Cell { return &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)} }
	request := &o.ListBuildingsRequest{
		Scope:      &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)},
		Statuses:   []string{"blueprint", "frame"},
		PlayerOnly: proto.Bool(true),
		Region:     &o.Rectangle{Minimum: corner(), Maximum: corner()},
		Page:       &c.PageRequest{Limit: proto.Uint32(16)},
	}
	reply := &o.ListBuildingsReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_buildings", request, reply)
	if err != nil {
		return ConstructionCancelTarget{}, raw, err
	}
	if buildingUnknown(reply) != nil {
		return ConstructionCancelTarget{}, raw, contract("unknown construction cancel target fields")
	}
	switch v := reply.Outcome.(type) {
	case *o.ListBuildingsReply_Failure:
		return ConstructionCancelTarget{}, raw, failure(v.Failure, raw)
	case *o.ListBuildingsReply_Unavailable:
		return ConstructionCancelTarget{}, raw, unavailable(v.Unavailable, raw)
	case *o.ListBuildingsReply_Observed:
		if err = buildingContext(v.Observed.Context, identity, 0, false); err != nil {
			return ConstructionCancelTarget{}, raw, err
		}
		counts := v.Observed.Completeness
		if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" ||
			counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 ||
			counts.GetReturned() != uint64(len(v.Observed.Buildings)) {
			return ConstructionCancelTarget{}, raw, contract("incomplete construction cancel observation")
		}
		out := ConstructionCancelTarget{Context: v.Observed.Context}
		for _, row := range v.Observed.Buildings {
			if row == nil || row.Building == nil || row.Building.Position == nil {
				return ConstructionCancelTarget{}, raw, contract("invalid construction cancel row")
			}
			switch row.GetStatus() {
			case "blueprint", "frame":
			default:
				return ConstructionCancelTarget{}, raw, contract("unexpected construction cancel status")
			}
			if row.Building.Position.GetX() != cell.X || row.Building.Position.GetZ() != cell.Z ||
				row.GetBuildDefName() != cancel.Definition() || row.GetStuff() != cancel.Material() {
				continue
			}
			snapshot := row.Building.GetSnapshot()
			if out.Present || snapshot == nil || validID(row.Building.GetId()) != nil ||
				snapshot.GetEntityId() != row.Building.GetId() || validID(snapshot.GetToken()) != nil {
				return ConstructionCancelTarget{}, raw, contract("ambiguous or unusable construction cancel target")
			}
			out.ThingID, out.Token, out.Present = row.Building.GetId(), snapshot.GetToken(), true
		}
		return out, raw, nil
	default:
		return ConstructionCancelTarget{}, raw, contract("construction cancel target outcome missing")
	}
}

// ConstructionCancelSelection is one cancellation bound to the exact native
// target an inspection resolved it to, the pairing operations.proto's
// CancelConstruction message requires.
type ConstructionCancelSelection struct {
	Cancel  domain.ConstructionCancel
	ThingID string
	Token   string
}
type ConstructionCancelAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Selected   ConstructionCancelSelection
}
type ConstructionCancelControl struct{ client *Client }

func NewConstructionCancelControl(client *Client) (*ConstructionCancelControl, error) {
	if client == nil {
		return nil, contract("construction cancel client missing")
	}
	return &ConstructionCancelControl{client}, nil
}

func validConstructionCancel(cancel domain.ConstructionCancel) error {
	canonical, err := domain.ReconstructConstructionCancel(cancel)
	if err != nil || canonical != cancel {
		return contract("invalid construction cancel value")
	}
	return nil
}
func validConstructionCancelSelection(s ConstructionCancelSelection) error {
	if err := validConstructionCancel(s.Cancel); err != nil {
		return err
	}
	if validID(s.ThingID) != nil || validID(s.Token) != nil {
		return contract("invalid construction cancel selection")
	}
	return nil
}
func constructionCancelOperation(s ConstructionCancelSelection) *op.Operation {
	cell := s.Cancel.Cell()
	command := &op.CancelConstruction{
		Target:          &op.EntityPrecondition{EntityId: proto.String(s.ThingID), ExpectedSnapshotToken: proto.String(s.Token)},
		ExpectedDefName: proto.String(s.Cancel.Definition()),
		Cell:            &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)},
		ExpectedStuff:   proto.String(s.Cancel.Material()),
	}
	return &op.Operation{Command: &op.Operation_CancelConstruction{CancelConstruction: command}}
}

func (client *Client) PreviewConstructionCancel(ctx context.Context, identity *c.Identity, s ConstructionCancelSelection) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validConstructionCancelSelection(s) != nil {
		return nil, Result{}, contract("invalid construction cancel preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: constructionCancelOperation(s)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown construction cancel preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || buildingContext(v.Context, identity, 0, false) != nil || v.Projected != nil {
		return nil, raw, contract("invalid construction cancel preview evidence")
	}
	return reply, raw, nil
}

func (writer *ConstructionCancelControl) ApplyConstructionCancel(ctx context.Context, pre *a.WritePrecondition, s ConstructionCancelSelection) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validConstructionCancelSelection(s) != nil {
		return nil, Result{}, contract("invalid construction cancel execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: constructionCancelOperation(s)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown construction cancel execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("construction cancel owner mismatch")
	}
	err = constructionCancelReceipt(v, ConstructionCancelAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Selected: s})
	return reply, raw, err
}

func validConstructionCancelAttempt(w ConstructionCancelAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid construction cancel attempt")
	}
	return validConstructionCancelSelection(w.Selected)
}

// ConstructionCancelMatches decodes one cancellation effect readback. Removal
// is proved only by the exact requested order being gone: the effect must name
// the thing this attempt targeted, agree with its definition, material and
// cell, and report either that it is absent or that its stage is cancelled.
// A still-present order at any other stage -- including one rebuilt by a
// replacement placement since -- is not removal, matching Python's insistence
// on receipt.removed and its "never cancels a replacement" rule.
func ConstructionCancelMatches(v *r.EffectEvidence, s ConstructionCancelSelection) (bool, error) {
	d := v.GetConstruction()
	cell := s.Cancel.Cell()
	if d == nil || buildingUnknown(v) != nil || d.Present == nil || d.Stage == nil || d.Cell == nil ||
		d.DefName == nil || d.Stuff == nil || validID(d.GetOriginThingId()) != nil {
		return false, contract("invalid construction cancel readback")
	}
	if d.GetOriginThingId() != s.ThingID && d.GetCurrentThingId() != s.ThingID {
		return false, contract("construction cancel identity mismatch")
	}
	if d.GetDefName() != s.Cancel.Definition() || d.GetStuff() != s.Cancel.Material() || d.Cell.GetX() != cell.X || d.Cell.GetZ() != cell.Z {
		return false, contract("construction cancel target mismatch")
	}
	if d.GetFailed() {
		return false, nil
	}
	return !d.GetPresent() || d.GetStage() == r.ConstructionStage_CONSTRUCTION_STAGE_CANCELLED, nil
}
func constructionCancelEffect(v *r.EffectEvidence, w ConstructionCancelAttempt) error {
	_, err := ConstructionCancelMatches(v, w.Selected)
	return err
}
func constructionCancelReceipt(v *r.Receipt, w ConstructionCancelAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("construction cancel admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return constructionCancelEffect(out.Applied.GetObserved(), w)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("construction cancel uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return constructionCancelEffect(out.Uncertain.LastObserved, w)
		}
		return nil
	default:
		return contract("unsupported construction cancel receipt")
	}
}

func (client *Client) LookupConstructionCancel(ctx context.Context, w ConstructionCancelAttempt) (*r.LookupReply, Result, error) {
	if err := validConstructionCancelAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown construction cancel lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = constructionCancelReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("construction cancel in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("construction cancel lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveConstructionCancel(ctx context.Context, w ConstructionCancelAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validConstructionCancelAttempt(w) != nil || constructionCancelReceipt(admitted, w) != nil {
		return nil, Result{}, contract("construction cancel observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown construction cancel progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("construction cancel progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing construction cancel uncertainty")
		}
	case *r.Progress_Completed:
		matches, check := ConstructionCancelMatches(out.Completed.GetEvidence(), w.Selected)
		err = check
		if !v.GetCompleteInspection() || !matches {
			return nil, raw, contract("unverified construction cancel completion")
		}
	case *r.Progress_Unsuccessful:
		matches, check := ConstructionCancelMatches(out.Unsuccessful.GetEvidence(), w.Selected)
		err = check
		if !v.GetCompleteInspection() || matches || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified construction cancel failure")
		}
	default:
		err = contract("unsupported construction cancel progress")
	}
	return reply, raw, err
}
