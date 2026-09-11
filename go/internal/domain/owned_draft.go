package domain

import "errors"

type PawnID string
type DraftClaimID string
type ControllerSessionID string
type OwnedDraft struct{ pawn PawnID }

func NewOwnedDraft(pawn PawnID) (OwnedDraft, error) {
	if !validID(string(pawn)) {
		return OwnedDraft{}, errors.New("invalid draft pawn")
	}
	return OwnedDraft{pawn}, nil
}
func (d OwnedDraft) Pawn() PawnID { return d.pawn }
func NewOwnedDraftAction(id ActionID, draft OwnedDraft) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewOwnedDraft(draft.pawn); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: OwnedDraftAction, draft: draft}, nil
}
func (a Action) OwnedDraft() (OwnedDraft, bool) { return a.draft, a.kind == OwnedDraftAction }

type DraftClaim struct {
	Action  ActionID
	Attempt AttemptID
	Pawn    PawnID
	Claim   DraftClaimID
	Session ControllerSessionID
	Origin  GenerationSnapshot
}
type DraftCleanupStage string

const (
	DraftAwaitingClaim     DraftCleanupStage = "awaiting_claim"
	DraftNotAcquired       DraftCleanupStage = "not_acquired"
	DraftCleanupRequired   DraftCleanupStage = "required"
	DraftCleanupDispatched DraftCleanupStage = "dispatched"
	DraftCleanupUncertain  DraftCleanupStage = "uncertain"
	DraftReleased          DraftCleanupStage = "released"
	DraftSuperseded        DraftCleanupStage = "superseded"
)

type DraftReleaseRequest struct {
	Claim             DraftClaim
	PawnSnapshotToken string
	Observed          GenerationSnapshot
	Tick              Tick
}
type DraftRelease struct {
	Request  DraftReleaseRequest
	Sequence uint64
}
type DraftCleanup struct {
	Stage   DraftCleanupStage
	Claim   Fact[DraftClaim]
	Release Fact[DraftRelease]
}
type DraftCleanupObservation struct {
	Claim    DraftClaim
	Observed GenerationSnapshot
	Tick     Tick
	Outcome  DraftCleanupOutcome
}
type DraftCleanupOutcome string

const (
	DraftReleaseUncertain  DraftCleanupOutcome = "uncertain"
	DraftReleaseConfirmed  DraftCleanupOutcome = "released"
	DraftReleaseSuperseded DraftCleanupOutcome = "superseded"
)

func (p Progress) draftCleanupOutstanding() bool {
	v, ok := p.view.DraftCleanup.Value()
	return ok && v.Stage != DraftNotAcquired && v.Stage != DraftReleased && v.Stage != DraftSuperseded
}
func (p Progress) bindDraftClaim(value Fact[DraftClaim]) (Progress, error) {
	cleanup, ok := p.view.DraftCleanup.Value()
	if p.action.kind != OwnedDraftAction || !ok {
		return p, errors.New("draft dispatch required")
	}
	claim, known := value.Value()
	if !known {
		return p, nil
	}
	if claim.Action != p.view.Action || claim.Attempt != p.view.Attempt || claim.Attempt == 0 || claim.Pawn != p.action.draft.pawn || claim.Origin != p.view.Snapshot || claim.Origin.Native == 0 || claim.Origin.Direction == 0 || !validID(string(claim.Claim)) || !validID(string(claim.Session)) {
		return p, errors.New("claim does not identify original draft attempt")
	}
	if prior, known := cleanup.Claim.Value(); known {
		if prior != claim {
			return p, errors.New("draft claim is immutable")
		}
		return p, nil
	}
	if cleanup.Stage != DraftAwaitingClaim {
		return p, errors.New("claim cannot replace resolved absence")
	}
	cleanup.Claim = Known(claim)
	cleanup.Stage = DraftCleanupRequired
	p.view.DraftCleanup = Known(cleanup)
	return p, nil
}
func (p Progress) RecordDraftReceipt(attempt AttemptID, receipt Receipt, claim Fact[DraftClaim]) (Progress, error) {
	next, err := p.bindDraftClaim(claim)
	if err != nil {
		return p, err
	}
	if receipt == ReceiptRefused {
		if c, _ := next.view.DraftCleanup.Value(); c.Claim.known {
			return p, errors.New("refusal contradicts acquired claim")
		}
	}
	next, err = next.recordReceipt(attempt, receipt)
	if err != nil {
		return p, err
	}
	if receipt == ReceiptRefused {
		next.view.DraftCleanup = Known(DraftCleanup{Stage: DraftNotAcquired})
	}
	return next, nil
}

// ObserveDraft can bind a later lookup claim after unsuccessful ordinary progress;
// doing so does not reactivate the action or erase its prior outcome.
func (p Progress) ObserveDraft(observation Observation, current GenerationSnapshot, claim Fact[DraftClaim]) (Progress, error) {
	if observation.Causality != AfterDispatch {
		return p, errors.New("draft observation requires causal inspection")
	}
	next, err := p.bindDraftClaim(claim)
	if err != nil {
		return p, err
	}
	terminal := !p.view.Unresolved
	if terminal {
		cleanup, _ := p.view.DraftCleanup.Value()
		if cleanup.Stage != DraftAwaitingClaim || (observation.Effect != EffectUnknown && observation.Effect != EffectAbsent) {
			return p, errors.New("terminal draft only permits outstanding claim lookup")
		}
		next.view.Unresolved = true
	}
	next, err = next.observe(observation, current)
	if err != nil {
		return p, err
	}
	cleanup, _ := next.view.DraftCleanup.Value()
	if observation.Effect == EffectCompleted && !cleanup.Claim.known {
		return p, errors.New("draft completion requires exact owned claim")
	}
	if observation.Effect == EffectAbsent && !cleanup.Claim.known {
		next.view.DraftCleanup = Known(DraftCleanup{Stage: DraftNotAcquired})
	}
	if terminal {
		cleanup := next.view.DraftCleanup
		next = p
		next.view.DraftCleanup = cleanup
		next.view.Tick = observation.Tick
		return next, nil
	}
	return next, nil
}

// BeginDraftCleanup records the exact request before dispatch. A caller supplies
// freshly inspected CAS evidence even when paused ticks and tokens are unchanged.
func (p Progress) BeginDraftCleanup(request DraftReleaseRequest) (Progress, error) {
	cleanup, ok := p.view.DraftCleanup.Value()
	claim, known := cleanup.Claim.Value()
	if p.action.kind != OwnedDraftAction || !ok || !known || (cleanup.Stage != DraftCleanupRequired && cleanup.Stage != DraftCleanupUncertain) || request.Claim != claim {
		return p, errors.New("owned draft cleanup required")
	}
	if request.Observed.Validate() != nil || !request.Observed.sameWorld(claim.Origin) || request.Observed.Native < claim.Origin.Native || request.Tick < p.view.Tick || !validID(request.PawnSnapshotToken) {
		return p, errors.New("invalid cleanup observation")
	}
	sequence := uint64(1)
	if previous, known := cleanup.Release.Value(); known {
		if previous.Sequence == ^uint64(0) {
			return p, errors.New("cleanup sequence exhausted")
		}
		if request.Tick < previous.Request.Tick || request.Observed.Native < previous.Request.Observed.Native {
			return p, errors.New("cleanup observation regressed")
		}
		sequence = previous.Sequence + 1
	}
	cleanup.Release = Known(DraftRelease{Request: request, Sequence: sequence})
	cleanup.Stage = DraftCleanupDispatched
	p.view.DraftCleanup = Known(cleanup)
	return p, nil
}

// Outcomes are validated native evidence for the exact journaled release, never
// an inference from a missing owner or failed transport.
func (p Progress) RecordDraftCleanup(release DraftRelease, outcome DraftCleanupOutcome) (Progress, error) {
	cleanup, ok := p.view.DraftCleanup.Value()
	pending, known := cleanup.Release.Value()
	if !ok || !known || cleanup.Stage != DraftCleanupDispatched || pending != release || release.Sequence == 0 {
		return p, errors.New("cleanup outcome does not match dispatched request")
	}
	switch outcome {
	case DraftReleaseUncertain:
		cleanup.Stage = DraftCleanupUncertain
	case DraftReleaseConfirmed:
		cleanup.Stage = DraftReleased
	default:
		return p, errors.New("invalid cleanup outcome")
	}
	p.view.DraftCleanup = Known(cleanup)
	return p, nil
}

// ObserveDraftCleanup consumes positive replacement evidence without pretending
// that a release was dispatched. The boundary must establish actual replacement.
func (p Progress) ObserveDraftCleanup(observation DraftCleanupObservation) (Progress, error) {
	cleanup, ok := p.view.DraftCleanup.Value()
	claim, known := cleanup.Claim.Value()
	if !ok || !known || !p.draftCleanupOutstanding() || claim != observation.Claim || observation.Outcome != DraftReleaseSuperseded || observation.Observed.Validate() != nil || observation.Tick < 0 {
		return p, errors.New("invalid draft supersession evidence")
	}
	if observation.Observed.sameWorld(claim.Origin) {
		if observation.Tick < p.view.Tick || observation.Observed.Native < claim.Origin.Native {
			return p, errors.New("supersession observation regressed")
		}
		if release, known := cleanup.Release.Value(); known && (observation.Tick < release.Request.Tick || observation.Observed.Native < release.Request.Observed.Native) {
			return p, errors.New("supersession predates cleanup")
		}
	}
	cleanup.Stage = DraftSuperseded
	p.view.DraftCleanup = Known(cleanup)
	return p, nil
}

type DraftScopeSupersession struct {
	Action   ActionID
	Attempt  AttemptID
	Origin   GenerationSnapshot
	Observed GenerationSnapshot
	Tick     Tick
}

// ObserveDraftScopeSupersession retires cleanup only after positive observation
// of a replacement world. It says nothing about whether the original draft was
// acquired or released, and cannot authorize a call against the replacement.
func (p Progress) ObserveDraftScopeSupersession(observation DraftScopeSupersession) (Progress, error) {
	if p.action.kind != OwnedDraftAction || !p.draftCleanupOutstanding() || observation.Action != p.view.Action || observation.Attempt == 0 || observation.Attempt != p.view.Attempt || observation.Origin != p.view.Snapshot || observation.Observed.Validate() != nil || observation.Tick < 0 || observation.Observed.sameWorld(observation.Origin) {
		return p, errors.New("invalid draft scope supersession evidence")
	}
	cleanup, _ := p.view.DraftCleanup.Value()
	cleanup.Stage = DraftSuperseded
	p.view.DraftCleanup = Known(cleanup)
	return p, nil
}
