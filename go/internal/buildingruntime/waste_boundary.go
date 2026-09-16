package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// WasteNative reuses the generic bridge.ReadPawns (no combat/work/care
// details are needed, unlike tend) and the new bridge.ReadWasteTarget, which
// is the only way to refresh a waste item's CAS token (the generic per-tick
// waste census has no exact-ID lookup RPC, mirroring filth). Preview/lookup/
// progress reuse the dedicated ManageWaste bridge calls (bridge/waste.go),
// which already validate the receipt/evidence shape against the exact
// attempt, unlike the generic PawnOrder boundary Clean uses.
type WasteNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadWasteTarget(context.Context, *c.Identity, string, domain.Cell) (bridge.WasteTarget, bridge.Result, error)
	PreviewWaste(context.Context, *c.Identity, string, string, string, string, []string, []string) (*o.PreviewReply, bridge.Result, error)
	LookupWaste(context.Context, bridge.WasteAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveWasteProgress(context.Context, bridge.WasteAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type WasteWriter interface {
	ApplyWaste(context.Context, *a.WritePrecondition, string, string, string, string, []string, []string) (*o.ExecuteReply, bridge.Result, error)
}
type WasteCapabilities struct {
	Native WasteNative
	Writer WasteWriter
}
type WasteBoundary struct {
	native  WasteNative
	writer  WasteWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewWasteBoundary(native WasteNative, writer WasteWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*WasteBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid waste boundary dependencies")
	}
	return &WasteBoundary{native, writer, leases, clock, session}, nil
}

func wastePawnFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.WastePawnFacts {
	facts := policy.WastePawnFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "def_name") {
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	return facts
}

func (b *WasteBoundary) InspectWaste(ctx context.Context, target executor.Target) (executor.WasteInspection, error) {
	out := executor.WasteInspection{StartedAt: b.clock.Now()}
	waste, ok := target.Action.Waste()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(target.Snapshot), []string{string(waste.Pawn())})
	if err != nil {
		return out, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return out, executor.ErrHeld
	}
	current, err := boundary.Context(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != 1 || counts.GetReturned() != 1 || len(observed.Pawns) != 1 {
		return out, executor.ErrHeld
	}
	row := observed.Pawns[0]
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(waste.Pawn()) {
		return out, executor.ErrEvidence
	}
	pawnToken, err := boundary.PawnToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	item, _, err := b.native.ReadWasteTarget(ctx, boundary.Identity(current), waste.Target(), waste.Cell())
	if err != nil {
		return out, err
	}
	if item.Context == nil || item.Item != waste.Target() {
		return out, executor.ErrEvidence
	}
	if _, err = boundary.Context(item.Context, current); err != nil {
		return out, err
	}
	if item.Context.GetTick() < observed.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	itemToken := item.Token
	preview, _, err := b.native.PreviewWaste(ctx, boundary.Identity(current), string(waste.Pawn()), pawnToken, waste.Target(), itemToken, nil, nil)
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(evaluated.Context, current); err != nil {
		return out, err
	}
	if evaluated.Context.GetTick() < observed.Context.GetTick() || evaluated.Accepted == nil || evaluated.GetProjected().GetJob() == nil {
		return out, executor.ErrEvidence
	}
	facts := policy.WasteDispatchFacts{Snapshot: current, PawnTick: domain.Tick(observed.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: domain.Known(evaluated.GetAccepted())}
	facts.Pawn = wastePawnFacts(waste.Pawn(), row, pawnToken)
	facts.Item = policy.WasteItemDispatchFacts{Item: waste.Target(), SnapshotToken: itemToken, Exists: domain.Known(true)}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *WasteBoundary) attempt(dispatch executor.WasteDispatch) (bridge.WasteAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	waste, ok := p.Action.Waste()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != waste.Pawn() || admission.Target != waste.Target() || admission.Cell != waste.Cell() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) || !boundary.ValidID(admission.TargetSnapshotToken) {
		return bridge.WasteAttempt{}, executor.ErrEvidence
	}
	return bridge.WasteAttempt{
		Identity:   boundary.Identity(p.Snapshot),
		Attempt:    &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))},
		Generation: uint64(p.Snapshot.Native),
		Pawn:       string(waste.Pawn()), PawnToken: admission.PawnSnapshotToken,
		Target: waste.Target(), TargetToken: admission.TargetSnapshotToken,
	}, nil
}

func (b *WasteBoundary) ManageWaste(ctx context.Context, dispatch executor.WasteDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	lease, err := b.leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !boundary.ValidID(lease) {
		return out, executor.ErrAuthority
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.Generation)}
	reply, _, err := b.writer.ApplyWaste(ctx, pre, attempt.Pawn, attempt.PawnToken, attempt.Target, attempt.TargetToken, attempt.UnwantedIDs, attempt.BuryIDs)
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	switch reply.GetReceipt().Outcome.(type) {
	case *r.Receipt_Applied:
		out.Kind = domain.ReceiptAccepted
	case *r.Receipt_Uncertain:
	default:
		return out, executor.ErrEvidence
	}
	return out, ctx.Err()
}

var _ executor.WasteBoundary = (*WasteBoundary)(nil)
