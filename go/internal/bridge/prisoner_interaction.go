package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// PrisonerInteractionMode names Population-*'s direct-write prisoner custody
// order: one exclusive interaction (Recruit, MaintainOnly, ReduceResistance,
// Release, or Enslave/Convert while Ideology is active). The native
// contract is NativePrisonerInteractionOperations.cs
// (integrations/rimgovernor-native/src/Bridge/Protocol), wired onto
// Operation_SetPrisonerInteraction in NativeOperationTools.cs's
// Execute/Preview dispatch. It ports the legacy JSON home/population tool's
// (PopulationTools.Population) interaction-write eligibility checks behind
// the typed boundary: an accepted order is a direct settings write (no
// native job), so acceptance is the effect, not a promise of one.
type PrisonerInteractionMode int32

const (
	PrisonerInteractionModeUnspecified PrisonerInteractionMode = iota
	PrisonerInteractionRecruit
	PrisonerInteractionMaintain
	PrisonerInteractionReduceResistance
	PrisonerInteractionRelease
	PrisonerInteractionEnslave
	PrisonerInteractionConvert
)

var prisonerInteractionModeWire = map[PrisonerInteractionMode]o.PrisonerInteraction{
	PrisonerInteractionRecruit:          o.PrisonerInteraction_PRISONER_INTERACTION_ATTEMPT_RECRUIT,
	PrisonerInteractionMaintain:         o.PrisonerInteraction_PRISONER_INTERACTION_MAINTAIN_ONLY,
	PrisonerInteractionReduceResistance: o.PrisonerInteraction_PRISONER_INTERACTION_REDUCE_RESISTANCE,
	PrisonerInteractionRelease:          o.PrisonerInteraction_PRISONER_INTERACTION_RELEASE,
	PrisonerInteractionEnslave:          o.PrisonerInteraction_PRISONER_INTERACTION_ENSLAVE,
	PrisonerInteractionConvert:          o.PrisonerInteraction_PRISONER_INTERACTION_CONVERT,
}

func prisonerInteractionWire(mode PrisonerInteractionMode) o.PrisonerInteraction {
	return prisonerInteractionModeWire[mode] // absent maps to UNSPECIFIED
}

type PrisonerInteractionAttempt struct {
	Identity        *c.Identity
	Attempt         *c.AttemptKey
	Generation      uint64
	Pawn, PawnToken string
	Interaction     PrisonerInteractionMode
}

func prisonerInteractionOperation(pawn, pawnToken string, interaction PrisonerInteractionMode) *o.Operation {
	wire := prisonerInteractionWire(interaction)
	return &o.Operation{Command: &o.Operation_SetPrisonerInteraction{SetPrisonerInteraction: &o.SetPrisonerInteraction{
		Pawn: gearEntity(pawn, pawnToken), Interaction: &wire,
	}}}
}

func prisonerInteractionCommand(pawn, pawnToken string, interaction PrisonerInteractionMode) error {
	if validID(pawn) != nil || validID(pawnToken) != nil {
		return contract("invalid prisoner interaction command")
	}
	if _, ok := prisonerInteractionModeWire[interaction]; !ok {
		return contract("invalid prisoner interaction mode")
	}
	return nil
}

// PreviewPrisonerInteraction checks an exact already-selected interaction
// write; acceptance is not authority.
func (client *Client) PreviewPrisonerInteraction(ctx context.Context, identity *c.Identity, pawn, pawnToken string, interaction PrisonerInteractionMode) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := prisonerInteractionCommand(pawn, pawnToken, interaction); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: prisonerInteractionOperation(pawn, pawnToken, interaction)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.PreviewReply_Failure:
		err = failure(v.Failure, raw)
	case *o.PreviewReply_Evaluated:
		value := v.Evaluated
		if value == nil {
			return reply, raw, contract("prisoner interaction preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		effect := value.Projected.GetPrisoner()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || effect == nil {
			err = contract("prisoner interaction preview facts missing")
			break
		}
		if effect.Pawn.GetEntityId() != pawn {
			err = contract("prisoner interaction preview pawn mismatch")
		}
	default:
		err = contract("prisoner interaction preview outcome missing")
	}
	return reply, raw, err
}

func prisonerInteractionAttempt(v PrisonerInteractionAttempt) (PrisonerInteractionAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return PrisonerInteractionAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return PrisonerInteractionAttempt{}, err
	}
	if v.Generation == 0 {
		return PrisonerInteractionAttempt{}, contract("prisoner interaction admission owner or generation mismatch")
	}
	if err := prisonerInteractionCommand(v.Pawn, v.PawnToken, v.Interaction); err != nil {
		return PrisonerInteractionAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

func prisonerInteractionEvidence(effect *r.PrisonerEffect, expected PrisonerInteractionAttempt) (*r.PrisonerEffect, error) {
	if effect == nil || effect.Pawn.GetEntityId() != expected.Pawn {
		return nil, contract("prisoner interaction pawn mismatch")
	}
	return effect, nil
}

func prisonerInteractionReceipt(v *r.Receipt, expected PrisonerInteractionAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("prisoner interaction admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("prisoner interaction applied missing")
		}
		_, err := prisonerInteractionEvidence(outcome.Applied.GetObserved().GetPrisoner(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("prisoner interaction uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := prisonerInteractionEvidence(outcome.Uncertain.LastObserved.GetPrisoner(), expected)
			return err
		}
		return nil
	default:
		return contract("unsupported prisoner interaction receipt")
	}
}

type PrisonerInteractionWriter struct{ client *Client }

func NewPrisonerInteractionWriter(client *Client) (*PrisonerInteractionWriter, error) {
	if client == nil {
		return nil, contract("prisoner interaction client missing")
	}
	return &PrisonerInteractionWriter{client}, nil
}

// ApplyPrisonerInteraction dispatches one already-admitted interaction write.
func (writer *PrisonerInteractionWriter) ApplyPrisonerInteraction(ctx context.Context, pre *a.WritePrecondition, pawn, pawnToken string, interaction PrisonerInteractionMode) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid prisoner interaction execution")
	}
	if err := prisonerInteractionCommand(pawn, pawnToken, interaction); err != nil {
		return nil, Result{}, err
	}
	expected, err := prisonerInteractionAttempt(PrisonerInteractionAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Pawn: pawn, PawnToken: pawnToken, Interaction: interaction})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: prisonerInteractionOperation(pawn, pawnToken, interaction)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = prisonerInteractionReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("prisoner interaction execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupPrisonerInteraction(ctx context.Context, w PrisonerInteractionAttempt) (*r.LookupReply, Result, error) {
	expected, err := prisonerInteractionAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = prisonerInteractionReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("prisoner interaction in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("prisoner interaction unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("prisoner interaction lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObservePrisonerInteractionProgress(ctx context.Context, w PrisonerInteractionAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := prisonerInteractionAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = prisonerInteractionReceipt(admitted, expected); err != nil {
			return nil, Result{}, err
		}
		admitted = proto.Clone(admitted).(*r.Receipt)
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = prisonerInteractionProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("prisoner interaction progress outcome missing")
	}
	return reply, raw, err
}

func prisonerInteractionProgress(v *r.Progress, expected PrisonerInteractionAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("prisoner interaction progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("prisoner interaction progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("prisoner interaction unknown progress missing")
		}
		return nil
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("prisoner interaction completed missing")
		}
		_, err := prisonerInteractionEvidence(outcome.Completed.Evidence.GetPrisoner(), expected)
		return err
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("prisoner interaction unsuccessful reason missing")
		}
		_, err := prisonerInteractionEvidence(outcome.Unsuccessful.Evidence.GetPrisoner(), expected)
		return err
	default:
		return contract("prisoner interaction progress state missing")
	}
}
