package buildingruntime

import (
	"context"
	"errors"
	"math"

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

// CaravanDepartureNative composes the exact-ID pawn read every pawn-order
// boundary already uses (crew eligibility), the existing ColonyFacts read
// (home colonist count and food runway), a new home-colonist census (Doctor
// coverage among pawns who are NOT departing, which cannot be answered by an
// exact-ID read since the boundary does not otherwise know who stays home),
// and the caravan catalog/preview/lookup/progress reads the round-1 bridge
// layer (bridge/caravan_departure.go) already built against the FormCaravan
// operation. ReadCaravanCatalog has no native handler as of this writing
// (confirmed by exhaustive search of the C# mod).
type CaravanDepartureNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*n.ColonyFactsReply, bridge.Result, error)
	ReadHomeColonists(context.Context, *c.Identity) (*n.ListPawnsReply, bridge.Result, error)
	ReadCaravanCatalog(context.Context, *c.Identity, int32) (bridge.CaravanCatalogRead, bridge.Result, error)
	PreviewCaravanDeparture(context.Context, *c.Identity, string, []string, []bridge.CaravanCargoSelection, int32) (*o.PreviewReply, bridge.Result, error)
	LookupCaravanDeparture(context.Context, bridge.CaravanDepartureAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveCaravanDepartureProgress(context.Context, bridge.CaravanDepartureAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type CaravanDepartureWriter interface {
	ApplyCaravanDeparture(context.Context, *a.WritePrecondition, *a.Owner, string, []string, []bridge.CaravanCargoSelection, int32) (*o.ExecuteReply, bridge.Result, error)
}
type CaravanDepartureCapabilities struct {
	Native CaravanDepartureNative
	Writer CaravanDepartureWriter
	Policy policy.CaravanDeparturePolicy
}
type CaravanDepartureBoundary struct {
	native  CaravanDepartureNative
	writer  CaravanDepartureWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
	policy  policy.CaravanDeparturePolicy
}

func NewCaravanDepartureBoundary(native CaravanDepartureNative, writer CaravanDepartureWriter, leases boundary.LeaseSource, clock executor.Clock, session string, p policy.CaravanDeparturePolicy) (*CaravanDepartureBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid caravan departure boundary dependencies")
	}
	return &CaravanDepartureBoundary{native, writer, leases, clock, session, p}, nil
}

func factFloat64(v *float64) domain.Fact[float64] {
	if v == nil {
		return domain.Unknown[float64]()
	}
	return domain.Known(*v)
}

func factFloat64FromFloat32(v *float32) domain.Fact[float64] {
	if v == nil {
		return domain.Unknown[float64]()
	}
	return domain.Known(float64(*v))
}

func factInt32(v *int32) domain.Fact[int32] {
	if v == nil {
		return domain.Unknown[int32]()
	}
	return domain.Known(*v)
}

// caravanDoctorEnabled mirrors tend.doctorWorkFacts's enabled half: Doctor
// work-type enablement is unknown unless native reported whether the work
// type is disabled.
func caravanDoctorEnabled(work []*n.WorkSetting) (enabled bool, known bool) {
	for _, w := range work {
		if w == nil {
			continue
		}
		if w.GetDefName() == "Doctor" {
			if w.Disabled == nil {
				return false, false
			}
			return !w.GetDisabled(), true
		}
	}
	return false, false
}

// homeDoctorAvailable reports whether any home colonist not in the departing
// crew is a known, non-downed, Doctor-enabled pawn. A single confirmed
// witness proves true regardless of other pawns' unknowns; otherwise any
// remaining unknown pawn keeps the fact Unknown rather than falsely proving
// false.
func homeDoctorAvailable(rows []*n.PawnState, crew map[domain.PawnID]bool) domain.Fact[bool] {
	unknown := false
	for _, row := range rows {
		if row == nil || row.Pawn == nil {
			unknown = true
			continue
		}
		if crew[domain.PawnID(row.Pawn.GetId())] {
			continue
		}
		downed, downedKnown := boundary.FactBool(row.Downed).Value()
		enabled, enabledKnown := caravanDoctorEnabled(row.Settings.GetWork())
		if !downedKnown || !enabledKnown {
			unknown = true
			continue
		}
		if !downed && enabled {
			return domain.Known(true)
		}
	}
	if unknown {
		return domain.Unknown[bool]()
	}
	return domain.Known(false)
}

func caravanCrewFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.CaravanCrewFacts {
	facts := policy.CaravanCrewFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") && !boundary.IssueField(row.Job.Issues, "queued_jobs") {
		facts.PlayerForced, facts.QueuedJobs = boundary.FactBool(row.Job.PlayerForced), boundary.FactUint(row.Job.QueuedJobs)
	}
	return facts
}

// caravanCargoGroups resolves each already-selected cargo definition to the
// native catalog's group id for it, the exact translation FormCaravan's
// group-id-keyed CargoSelection requires (see bridge/caravan_departure.go's
// CaravanCargoSelection doc). A definition absent from groups, or a
// requested count exceeding what the catalog offers, is treated as stale
// evidence rather than silently dropped or truncated.
func caravanCargoGroups(cargo []domain.CargoItem, groups []*n.CargoGroup) ([]bridge.CaravanCargoSelection, error) {
	byDef := make(map[string]*n.CargoGroup, len(groups))
	for _, g := range groups {
		if g == nil || !boundary.ValidID(g.GetGroupId()) || !boundary.ValidID(g.GetDefName()) {
			return nil, executor.ErrEvidence
		}
		if _, dup := byDef[g.GetDefName()]; dup {
			return nil, executor.ErrEvidence
		}
		byDef[g.GetDefName()] = g
	}
	out := make([]bridge.CaravanCargoSelection, len(cargo))
	for i, item := range cargo {
		group, ok := byDef[item.Definition]
		if !ok || item.Count == 0 || item.Count > math.MaxInt32 || group.Count == nil || int64(item.Count) > group.GetCount() {
			return nil, executor.ErrEvidence
		}
		out[i] = bridge.CaravanCargoSelection{GroupID: group.GetGroupId(), Count: int32(item.Count)}
	}
	return out, nil
}

func (b *CaravanDepartureBoundary) InspectCaravanDeparture(ctx context.Context, target executor.Target) (executor.CaravanDepartureInspection, error) {
	out := executor.CaravanDepartureInspection{StartedAt: b.clock.Now(), Policy: b.policy}
	departure, ok := target.Action.CaravanDeparture()
	if !ok {
		return out, executor.ErrEvidence
	}
	crew := departure.Crew()
	pawnIDs := make([]string, len(crew))
	for i, pawn := range crew {
		pawnIDs[i] = string(pawn)
	}
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(target.Snapshot), pawnIDs)
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
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != uint64(len(crew)) || counts.GetReturned() != uint64(len(crew)) || len(observed.Pawns) != len(crew) {
		return out, executor.ErrHeld
	}
	crewSet := make(map[domain.PawnID]bool, len(crew))
	for _, pawn := range crew {
		crewSet[pawn] = true
	}
	byID := make(map[string]*n.PawnState, len(observed.Pawns))
	for _, row := range observed.Pawns {
		if row == nil || row.Pawn == nil || !crewSet[domain.PawnID(row.Pawn.GetId())] || byID[row.Pawn.GetId()] != nil {
			return out, executor.ErrEvidence
		}
		byID[row.Pawn.GetId()] = row
	}
	facts := policy.CaravanDepartureFacts{Snapshot: current, PawnTick: domain.Tick(observed.Context.GetTick())}
	facts.Crew = make([]policy.CaravanCrewFacts, len(crew))
	for i, pawn := range crew {
		row := byID[string(pawn)]
		token, err := boundary.PawnToken(row, observed.Context)
		if err != nil {
			return out, err
		}
		facts.Crew[i] = caravanCrewFacts(pawn, row, token)
	}

	colony, _, err := b.native.ReadColonyFacts(ctx, boundary.Identity(current), false, nil)
	if err != nil {
		return out, err
	}
	colonyObserved := colony.GetObserved()
	if colonyObserved == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(colonyObserved.Context, current); err != nil {
		return out, err
	}
	if colonyObserved.Context.GetTick() < observed.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	total, totalKnown := boundary.FactUint(colonyObserved.ColonistCount).Value()
	if totalKnown && int(total) >= len(crew) {
		facts.RemainingHomeColonists = domain.Known(total - uint32(len(crew)))
	}
	facts.HomeFoodRunwayDays = factFloat64(colonyObserved.FoodRunwayDays)

	roster, _, err := b.native.ReadHomeColonists(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	rosterObserved := roster.GetObserved()
	if rosterObserved == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(rosterObserved.Context, current); err != nil {
		return out, err
	}
	if rosterObserved.Context.GetTick() < colonyObserved.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	facts.HomeDoctorAvailable = homeDoctorAvailable(rosterObserved.Pawns, crewSet)

	catalog, _, err := b.native.ReadCaravanCatalog(ctx, boundary.Identity(current), departure.DestinationTile())
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(catalog.Context, current); err != nil {
		return out, err
	}
	if catalog.Context.GetTick() < rosterObserved.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	facts.CatalogToken = catalog.Token
	for _, route := range catalog.Routes {
		if route.GetDestination() == departure.DestinationTile() {
			facts.RouteReachable = boundary.FactBool(route.Reachable)
			facts.RouteTemperatureC = factFloat64(route.TemperatureC)
			facts.RouteHostile = boundary.FactBool(route.Hostile)
			facts.RouteFactionID = route.GetFactionId()
			facts.RouteGoodwill = factInt32(route.Goodwill)
			break
		}
	}
	cargo, err := caravanCargoGroups(departure.Cargo(), catalog.CargoGroups)
	if err != nil {
		return out, err
	}
	preview, _, err := b.native.PreviewCaravanDeparture(ctx, boundary.Identity(current), catalog.Token, pawnIDs, cargo, departure.DestinationTile())
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
	if evaluated.Context.GetTick() < catalog.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	facts.NativeCanTry = boundary.FactBool(evaluated.Accepted)
	facts.PreviewTick = domain.Tick(evaluated.Context.GetTick())
	// RoutePreparation.FoodRotDays is the dialog-level pre-departure figure
	// (Dialog_FormCaravan.DaysWorthOfFood.tillRot for the exact selected
	// pack), the same value Python's route.get('foodRotDays') reads --
	// distinct from CaravanState.food_rot_days, which only exists once a
	// caravan has already departed and is unrelated to this preview.
	if routePrep := evaluated.GetCaravan().GetRoute(); routePrep != nil {
		facts.RouteFoodRotDays = factFloat64FromFloat32(routePrep.FoodRotDays)
	}

	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *CaravanDepartureBoundary) attempt(dispatch executor.CaravanDepartureDispatch, cargo []bridge.CaravanCargoSelection) (bridge.CaravanDepartureAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	departure, ok := p.Action.CaravanDeparture()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Tick > p.Tick || !boundary.ValidID(admission.CatalogToken) {
		return bridge.CaravanDepartureAttempt{}, executor.ErrEvidence
	}
	crew := departure.Crew()
	if len(admission.Crew) != len(crew) {
		return bridge.CaravanDepartureAttempt{}, executor.ErrEvidence
	}
	byPawn := make(map[domain.PawnID]bool, len(admission.Crew))
	for _, member := range admission.Crew {
		if !boundary.ValidID(member.SnapshotToken) {
			return bridge.CaravanDepartureAttempt{}, executor.ErrEvidence
		}
		byPawn[member.Pawn] = true
	}
	pawnIDs := make([]string, len(crew))
	for i, pawn := range crew {
		if !byPawn[pawn] {
			return bridge.CaravanDepartureAttempt{}, executor.ErrEvidence
		}
		pawnIDs[i] = string(pawn)
	}
	return bridge.CaravanDepartureAttempt{
		Identity:        boundary.Identity(p.Snapshot),
		Attempt:         &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))},
		Owner:           &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))},
		Generation:      uint64(p.Snapshot.Native),
		CatalogToken:    admission.CatalogToken,
		PawnIDs:         pawnIDs,
		Cargo:           cargo,
		DestinationTile: departure.DestinationTile(),
	}, nil
}

func (b *CaravanDepartureBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.CaravanDepartureDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *CaravanDepartureBoundary) DepartCaravan(ctx context.Context, dispatch executor.CaravanDepartureDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	departure, ok := p.Action.CaravanDeparture()
	if !ok {
		return out, executor.ErrEvidence
	}
	catalog, _, err := b.native.ReadCaravanCatalog(ctx, boundary.Identity(p.Snapshot), departure.DestinationTile())
	if err != nil {
		return out, err
	}
	if catalog.Token != dispatch.Admission.CatalogToken {
		return out, executor.ErrEvidence
	}
	cargo, err := caravanCargoGroups(departure.Cargo(), catalog.CargoGroups)
	if err != nil {
		return out, err
	}
	attempt, err := b.attempt(dispatch, cargo)
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
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.Generation), LeaseId: proto.String(lease)}
	reply, _, err := b.writer.ApplyCaravanDeparture(ctx, pre, attempt.Owner, attempt.CatalogToken, attempt.PawnIDs, attempt.Cargo, attempt.DestinationTile)
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	receipt := reply.GetReceipt()
	if err = b.checkReceipt(receipt, dispatch); err != nil {
		return out, err
	}
	switch receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		out.Kind = domain.ReceiptAccepted
	case *r.Receipt_Uncertain:
	default:
		return out, executor.ErrEvidence
	}
	return out, ctx.Err()
}

// observeAttempt rebuilds the bridge attempt used by Lookup/ObserveProgress.
// Those calls are keyed by AttemptKey alone; the command fields they also
// require (catalog token, pawn ids, destination) are validated only for
// shape there, not cross-checked against the receipt's evidence (see
// bridge/caravan_departure.go's caravanDepartureEvidence, which checks only
// destination and pawn membership). Cargo is therefore omitted rather than
// re-resolved against a live catalog: Observe must not require native
// surface DepartCaravan itself does not depend on succeeding first.
func (b *CaravanDepartureBoundary) observeAttempt(dispatch executor.CaravanDepartureDispatch) (bridge.CaravanDepartureAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	departure, ok := p.Action.CaravanDeparture()
	if !ok || p.Attempt == 0 || !boundary.ValidID(admission.CatalogToken) {
		return bridge.CaravanDepartureAttempt{}, executor.ErrEvidence
	}
	crew := departure.Crew()
	pawnIDs := make([]string, len(crew))
	for i, pawn := range crew {
		pawnIDs[i] = string(pawn)
	}
	return bridge.CaravanDepartureAttempt{
		Identity:        boundary.Identity(p.Snapshot),
		Attempt:         &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))},
		Owner:           &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))},
		Generation:      uint64(p.Snapshot.Native),
		CatalogToken:    admission.CatalogToken,
		PawnIDs:         pawnIDs,
		DestinationTile: departure.DestinationTile(),
	}, nil
}

func (b *CaravanDepartureBoundary) ObserveCaravanDeparture(ctx context.Context, dispatch executor.CaravanDepartureDispatch, current domain.GenerationSnapshot) (executor.CaravanDepartureEvidence, error) {
	out := executor.CaravanDepartureEvidence{StartedAt: b.clock.Now()}
	attempt, err := b.observeAttempt(dispatch)
	if err != nil {
		return out, err
	}
	reply, _, err := b.native.LookupCaravanDeparture(ctx, attempt)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
	departure, _ := p.Action.CaravanDeparture()
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Unknown:
		if _, err = boundary.Context(v.Unknown.GetContext(), current); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: domain.Tick(v.Unknown.GetContext().GetTick()), Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		out.Crew, out.ObservedAt = departure.Crew(), b.clock.Now()
		return out, ctx.Err()
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, attempt.Attempt) {
			return out, executor.ErrEvidence
		}
		if err = boundary.Admission(&r.Receipt{Attempt: v.InFlight.Attempt, AdmittedContext: v.InFlight.AdmittedContext, AuthorizingOwner: attempt.Owner}, p, b.session); err != nil {
			return out, err
		}
	case *r.LookupReply_Receipt:
		if err = b.checkReceipt(v.Receipt, dispatch); err != nil {
			return out, err
		}
	default:
		return out, executor.ErrEvidence
	}
	progress, _, err := b.native.ObserveCaravanDepartureProgress(ctx, attempt, nil)
	if err != nil {
		return out, err
	}
	prog := progress.GetProgress()
	if prog == nil {
		return out, executor.ErrHeld
	}
	if !proto.Equal(prog.Attempt, attempt.Attempt) {
		return out, executor.ErrEvidence
	}
	tickCtx, err := boundary.Context(prog.Context, current)
	if err != nil {
		return out, err
	}
	tick := domain.Tick(prog.Context.GetTick())
	out.ObservedAt = b.clock.Now()
	out.Crew = departure.Crew()
	switch outcome := prog.Effect.(type) {
	case *r.Progress_Unknown:
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		return out, ctx.Err()
	case *r.Progress_Pending:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		effect := outcome.Pending.GetEvidence().GetCaravan()
		out.CaravanID = effect.GetCaravanId()
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectPending, Causality: domain.AfterDispatch}
		return out, ctx.Err()
	case *r.Progress_Completed:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		effect := outcome.Completed.GetEvidence().GetCaravan()
		out.CaravanID = effect.GetCaravanId()
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}
		out.Complete = true
		return out, ctx.Err()
	case *r.Progress_Absent:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectAbsent, Causality: domain.AfterDispatch}
		out.Complete = true
		return out, ctx.Err()
	case *r.Progress_Unsuccessful:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		effect := outcome.Unsuccessful.GetEvidence().GetCaravan()
		out.CaravanID = effect.GetCaravanId()
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure, Causality: domain.AfterDispatch}
		out.Complete = true
		return out, ctx.Err()
	default:
		return out, executor.ErrEvidence
	}
}

var _ executor.CaravanDepartureBoundary = (*CaravanDepartureBoundary)(nil)
