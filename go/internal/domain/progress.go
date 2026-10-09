package domain

import "errors"

type Stage string

const (
	Pending             Stage = "pending"
	Prepared            Stage = "prepared"
	Dispatched          Stage = "dispatched"
	AwaitingObservation Stage = "awaiting_observation"
	Completed           Stage = "completed"
	Unsuccessful        Stage = "unsuccessful"
	Cancelled           Stage = "cancelled"
)

type Receipt string

const (
	ReceiptAccepted Receipt = "accepted"
	// ReceiptRefused is trusted pre-admission no-effect proof, never a transport refusal.
	ReceiptRefused Receipt = "refused"
	// ReceiptUnsent is the transport's proof that the write never left the
	// controller (it failed before the native call was issued), so nothing
	// was admitted: no-effect, and the same authority may retry.
	ReceiptUnsent  Receipt = "unsent"
	ReceiptUnknown Receipt = "unknown"
)

// ErrWriteUnsent marks a native write failure that happened before the call
// was issued. The transport wraps it; executors record ReceiptUnsent instead
// of an uncertain receipt that could only reconcile through a native ledger
// entry that never existed (issue #70).
var ErrWriteUnsent = errors.New("native write never sent")

type Effect string

const (
	EffectUnknown   Effect = "unknown"
	EffectPending   Effect = "pending"
	EffectCompleted Effect = "completed"
	// EffectAbsent requires a complete inspection proving no order or effect remains.
	// Partial/truncated reads must report EffectUnknown.
	EffectAbsent       Effect = "absent"
	EffectUnsuccessful Effect = "unsuccessful"
)

type ObservationCausality string

// AfterDispatch asserts a native inspection correlated to this exact attempt,
// causally after dispatch. Equal game ticks alone cannot establish this ordering.
const AfterDispatch ObservationCausality = "after_dispatch"

type UnsuccessfulReason string

const (
	NativeFailure      UnsuccessfulReason = "native_failure"
	NativeCancelled    UnsuccessfulReason = "cancelled"
	NativeInterrupted  UnsuccessfulReason = "interrupted"
	NativeExpired      UnsuccessfulReason = "expired"
	TargetDead         UnsuccessfulReason = "target_dead"
	OutcomeNotAchieved UnsuccessfulReason = "outcome_not_achieved"
)

// IntentRefused names an intent settled Unsuccessful by a refused receipt
// (a stale source, a target changed since the census): no observation
// carries it, and the owning routine replans from live state. Read it
// through ProgressView.UnsuccessfulCause.
const IntentRefused UnsuccessfulReason = "refused"

// UnsuccessfulCause is the typed reason an Unsuccessful action ended: the
// observed reason, or IntentRefused for an intent native refused. Empty
// for any other stage.
func (v ProgressView) UnsuccessfulCause() UnsuccessfulReason {
	if v.Stage != Unsuccessful {
		return ""
	}
	if reason, known := v.UnsuccessfulReason.Value(); known {
		return reason
	}
	if receipt, known := v.Receipt.Value(); known && receipt == ReceiptRefused {
		return IntentRefused
	}
	return ""
}

func (r UnsuccessfulReason) valid() bool {
	switch r {
	case NativeFailure, NativeCancelled, NativeInterrupted, NativeExpired, TargetDead, OutcomeNotAchieved:
		return true
	}
	return false
}

// HeldReason explains why a not-yet-dispatched action is currently stuck,
// mirroring (not importing, to avoid a domain->policy cycle) every
// policy.Reason value across both the emergency subset and the ordinary
// (non-emergency) admission-refusal reasons every action family's Admit-style
// check can return -- geometry conflicts, insufficient stock, dependency and
// spending blocks, per-family worker/resource unavailability, and the rest.
type HeldReason string

const (
	HeldUnsafeThreat    HeldReason = "unsafe_threat"
	HeldCriticalMedical HeldReason = "critical_medical"
	HeldStaleFacts      HeldReason = "stale_facts"
	HeldUnknownFacts    HeldReason = "unknown_facts"

	HeldNotReady           HeldReason = "not_ready"
	HeldAlreadyReserved    HeldReason = "already_reserved"
	HeldUnsafePlacement    HeldReason = "unsafe_placement"
	HeldMaterialRequired   HeldReason = "explicit_material_required"
	HeldDependencyBlocked  HeldReason = "dependency_incomplete"
	HeldGeometryBlocked    HeldReason = "geometry_conflict"
	HeldSpendingBlocked    HeldReason = "spending_policy"
	HeldInsufficientStock  HeldReason = "insufficient_stock"
	HeldInvalidHeld        HeldReason = "held_reservation_unverifiable"
	HeldArithmeticOverflow HeldReason = "arithmetic_overflow"

	HeldNativeIneligible           HeldReason = "native_ineligible"
	HeldPlayerOrder                HeldReason = "player_order"
	HeldUnsuitableEquipment        HeldReason = "unsuitable_equipment"
	HeldUnsupportedThreat          HeldReason = "unsupported_threat"
	HeldWallRemovalGeometryChanged HeldReason = "wall_removal_geometry_changed"
	HeldWallRemovalTargetChanged   HeldReason = "wall_removal_target_changed"
	HeldExcavationUnsupported      HeldReason = "excavation_unsupported"
	HeldExcavationGeometryChanged  HeldReason = "excavation_geometry_changed"
	HeldUnsafeRoute                HeldReason = "unsafe_route"
	HeldRoofSupportRisk            HeldReason = "roof_support_risk"
	HeldStorageMissing             HeldReason = "missing_storage"
	HeldUrgentCompetingWork        HeldReason = "urgent_competing_work"
	HeldUnsafeItem                 HeldReason = "unsafe_item"
)

// orderedHeldReasons lists every reason in the fixed, deterministic order
// HoldEvidence.Reasons() decodes them in; bit position within this slice is
// the single source of truth for the packed encoding below.
var orderedHeldReasons = []HeldReason{
	HeldUnsafeThreat, HeldCriticalMedical, HeldStaleFacts, HeldUnknownFacts,
	HeldNotReady, HeldAlreadyReserved, HeldUnsafePlacement, HeldMaterialRequired,
	HeldDependencyBlocked, HeldGeometryBlocked, HeldSpendingBlocked, HeldInsufficientStock,
	HeldInvalidHeld, HeldArithmeticOverflow,
	HeldNativeIneligible, HeldPlayerOrder,
	HeldUnsuitableEquipment,
	HeldUnsupportedThreat, HeldWallRemovalGeometryChanged, HeldWallRemovalTargetChanged,
	HeldExcavationUnsupported, HeldExcavationGeometryChanged,
	HeldUnsafeRoute, HeldRoofSupportRisk, HeldStorageMissing, HeldUrgentCompetingWork, HeldUnsafeItem,
}

func (r HeldReason) valid() bool {
	return r.bit() != 0
}

// heldReasonBits packs every hold reason into a comparable value so
// ProgressView (compared by == elsewhere) stays comparable; a slice field
// could not. The reason count must stay under the 64-bit cap;
// bit reports 0 (invalid) once orderedHeldReasons would exceed that cap.
type heldReasonBits uint64

func (r HeldReason) bit() heldReasonBits {
	for i, candidate := range orderedHeldReasons {
		if candidate == r {
			return 1 << i
		}
	}
	return 0
}

// HoldEvidence ties held-action reasons to the exact plan/revision and tick
// they were observed under. A projection must re-verify this tie before
// surfacing the reasons, so a stale reason can never survive a subsequent
// re-admission, resolution, or plan/action progression.
type HoldEvidence struct {
	reasons  heldReasonBits
	Plan     PlanID
	Revision PlanRevision
	Tick     Tick
	// Since is the tick this exact reason set was first recorded on this
	// plan revision; a repeated identical hold keeps it, so planners can
	// tell a long-stalled hold from a fresh one.
	Since Tick
}

// Reasons decodes the packed bits back into their canonical, deduplicated,
// deterministically ordered form.
func (e HoldEvidence) Reasons() []HeldReason {
	var result []HeldReason
	for _, r := range orderedHeldReasons {
		if e.reasons&r.bit() != 0 {
			result = append(result, r)
		}
	}
	return result
}

// ConstructionIdentity comes from a complete attempt-correlated native
// completion inspection. It proves identity, not authority for later upkeep.
type ConstructionIdentity struct{ Origin, Current string }

func (v ConstructionIdentity) Validate() error {
	if !validID(v.Origin) || !validID(v.Current) {
		return errors.New("invalid completed construction identity")
	}
	return nil
}

type Observation struct {
	Action             ActionID
	Attempt            AttemptID
	Snapshot           GenerationSnapshot
	Tick               Tick
	Effect             Effect
	Causality          ObservationCausality `json:",omitempty"`
	UnsuccessfulReason UnsuccessfulReason   `json:",omitempty"`
}
type ProgressView struct {
	// Zone is the native zone an applied zone_create receipt identifies (the
	// zone's unique load id, ZoneEffect.zone_id). It is the identity later
	// native censuses name the zone by, which is how a completed stockpile
	// method owns its zone for MaintainHomeCoverage (#315).
	Zone       Fact[string]
	Stockpiles Fact[CreatedZones]
	Combat     Fact[CombatResults]
	// Bill is the native bill an applied production_bill or surgery receipt
	// identifies (BillEffect.bill, SurgeryEffect.bill): the id a later
	// remove_production_bill names.
	Bill               Fact[string]
	Action             ActionID
	Attempt            AttemptID
	Plan               PlanID
	Revision           PlanRevision
	Stage              Stage
	Snapshot           GenerationSnapshot
	Tick               Tick
	Unresolved         bool
	Receipt            Fact[Receipt]
	Effect             Fact[Effect]
	UnsuccessfulReason Fact[UnsuccessfulReason]
	HeldReason         Fact[HoldEvidence]
}

// Progress transitions return a new value; failed transitions preserve the original.
// One orchestrator owns each action and durably records Prepared and Dispatched
// before invoking a native mutation. This package does not perform persistence.
type Progress struct {
	view   ProgressView
	action Action
}

func NewProgress(plan PlanSpec, action ActionID) (Progress, error) {
	for _, a := range plan.actions {
		if a.id == action {
			return Progress{view: ProgressView{Action: action, Plan: plan.id, Revision: plan.revision, Stage: Pending}, action: a}, nil
		}
	}
	return Progress{}, errors.New("action is not in plan")
}
func (p Progress) View() ProgressView { return p.view }
func (p Progress) Action() Action     { return p.action }

// Prepare binds a not-yet-dispatched action to the authority snapshot it will
// dispatch under. Dispatch is recorded durably before any native write, so a
// Prepared action never has a write outstanding: when its authority moves (a
// native generation advance after a cancelled dispatch) it is prepared again
// under the current snapshot rather than left behind with one it can never
// dispatch against.
func (p Progress) Prepare(snapshot GenerationSnapshot, tick Tick) (Progress, error) {
	if p.view.Stage != Pending && p.view.Stage != Prepared || p.view.Unresolved {
		return p, errors.New("action is not ready")
	}
	if err := snapshot.Validate(); err != nil {
		return p, err
	}
	if snapshot.Plan != p.view.Plan || snapshot.Revision != p.view.Revision || tick < p.view.Tick {
		return p, errors.New("stale plan or tick")
	}
	p.view.Stage, p.view.Snapshot, p.view.Tick = Prepared, snapshot, tick
	p.view.HeldReason = Unknown[HoldEvidence]()
	return p, nil
}

// Hold records why a Pending/Prepared, non-dispatched action is currently
// stuck, without changing Stage. The evidence is pinned to this exact plan
// revision and tick; any later successful transition (Prepare, dispatch,
// receipt, observation, cancellation) clears it, so a hold can never outlive
// the exact admission attempt it was computed against.
func (p Progress) Hold(reasons []HeldReason, tick Tick) (Progress, error) {
	if p.view.Stage != Pending && p.view.Stage != Prepared {
		return p, errors.New("hold requires a not-yet-dispatched action")
	}
	if p.view.Unresolved {
		return p, errors.New("hold requires no outstanding dispatch")
	}
	if tick < p.view.Tick {
		return p, errors.New("stale hold tick")
	}
	if len(reasons) == 0 || len(reasons) > len(orderedHeldReasons) {
		return p, errors.New("invalid hold reasons")
	}
	var bits heldReasonBits
	for _, r := range reasons {
		if !r.valid() {
			return p, errors.New("invalid hold reason")
		}
		bit := r.bit()
		if bits&bit != 0 {
			return p, errors.New("duplicate hold reason")
		}
		bits |= bit
	}
	since := tick
	if previous, known := p.view.HeldReason.Value(); known && previous.Plan == p.view.Plan && previous.Revision == p.view.Revision && previous.reasons == bits && previous.Since <= tick {
		since = previous.Since
	}
	p.view.HeldReason = Known(HoldEvidence{reasons: bits, Plan: p.view.Plan, Revision: p.view.Revision, Tick: tick, Since: since})
	return p, nil
}

// FreshHeldReason re-verifies HeldReason evidence against the view carrying
// it, rather than trusting the stored Fact alone. Any DTO projection must go
// through this instead of reading HeldReason directly.
func (v ProgressView) FreshHeldReason() ([]HeldReason, bool) {
	evidence, ok := v.FreshHold()
	if !ok {
		return nil, false
	}
	return evidence.Reasons(), true
}

// FreshHold is FreshHeldReason's evidence form, for callers that also need
// how long the current reason set has persisted (Since).
func (v ProgressView) FreshHold() (HoldEvidence, bool) {
	if v.Stage != Pending && v.Stage != Prepared || v.Unresolved {
		return HoldEvidence{}, false
	}
	evidence, known := v.HeldReason.Value()
	if !known || evidence.Plan != v.Plan || evidence.Revision != v.Revision || evidence.Tick < v.Tick {
		return HoldEvidence{}, false
	}
	return evidence, true
}
func (p Progress) MarkDispatched(current GenerationSnapshot, tick Tick) (Progress, error) {
	if p.view.Stage != Prepared || !p.view.Snapshot.Matches(current) || tick < p.view.Tick {
		return p, errors.New("dispatch requires current prepared authority")
	}
	if p.view.Attempt == ^AttemptID(0) {
		return p, errors.New("dispatch attempt identity exhausted")
	}
	p.view.Attempt++
	p.view.Stage, p.view.Unresolved, p.view.Tick = Dispatched, true, tick
	p.view.Receipt, p.view.Effect = Unknown[Receipt](), Unknown[Effect]()
	p.view.UnsuccessfulReason = Unknown[UnsuccessfulReason]()
	p.view.HeldReason = Unknown[HoldEvidence]()
	p.view.Zone = Unknown[string]()
	p.view.Bill = Unknown[string]()
	return p, nil
}

// IntentMode reports an action kind whose actions are idempotent intents
// (#856): native validates each against live state when it applies, so the
// receipt is terminal (applied completes, refused fails, unknown is sent
// again), with no observation phase, and the routine owning the
// kind reads its next phase from live facts rather than per-attempt
// progress. A kind opts in by registering an Actions/Apply builder in
// bridge/actions.go, whose init calls RegisterIntentKind.
func (k ActionKind) IntentMode() bool { return intentKinds[k] }

// intentKinds is written only from package init, before any goroutine reads it.
var intentKinds = map[ActionKind]bool{}

func RegisterIntentKind(k ActionKind) { intentKinds[k] = true }

func (p Progress) RecordReceipt(attempt AttemptID, receipt Receipt) (Progress, error) {
	return p.recordReceipt(attempt, receipt)
}

// RecordZoneReceipt records an applied zone_create's receipt together with
// the native zone identity its evidence named, which zone claims read.
func (p Progress) RecordZoneReceipt(attempt AttemptID, receipt Receipt, zone string) (Progress, error) {
	if p.action.kind != ZoneCreateAction || p.action.zone.Kind() == StockpileZone || receipt != ReceiptAccepted || !validID(zone) {
		return p, errors.New("zone identity requires an applied zone create")
	}
	next, err := p.recordReceipt(attempt, receipt)
	if err != nil {
		return p, err
	}
	next.view.Zone = Known(zone)
	return next, nil
}

// RecordBillReceipt records an applied bill-placing receipt together with the
// native bill id its evidence named.
func (p Progress) RecordBillReceipt(attempt AttemptID, receipt Receipt, bill string) (Progress, error) {
	if p.action.kind != ProductionBillAction && p.action.kind != SurgeryAction || receipt != ReceiptAccepted || !validID(bill) {
		return p, errors.New("bill identity requires an applied bill placement")
	}
	next, err := p.recordReceipt(attempt, receipt)
	if err != nil {
		return p, err
	}
	next.view.Bill = Known(bill)
	return next, nil
}
func (p Progress) recordReceipt(attempt AttemptID, receipt Receipt) (Progress, error) {
	if attempt == 0 || attempt != p.view.Attempt {
		return p, errors.New("receipt belongs to a different dispatch attempt")
	}
	if p.view.Stage != Dispatched && !(p.view.Stage == Cancelled && p.view.Unresolved) {
		return p, errors.New("receipt requires dispatched action")
	}
	switch receipt {
	case ReceiptAccepted, ReceiptRefused, ReceiptUnsent, ReceiptUnknown:
	default:
		return p, errors.New("invalid receipt")
	}
	if _, known := p.view.Receipt.Value(); known {
		return p, errors.New("receipt already recorded")
	}
	p.view.Receipt = Known(receipt)
	p.view.HeldReason = Unknown[HoldEvidence]()
	if p.action.kind.IntentMode() && receipt != ReceiptUnsent {
		// The receipt settles an intent's attempt: applied is done, refused
		// is over (the owning routine replans from live state), and an
		// unknown outcome is sent again, which an idempotent intent allows.
		p.view.Unresolved = false
		stage := Pending
		switch receipt {
		case ReceiptAccepted:
			p.view.Effect, stage = Known(EffectCompleted), Completed
		case ReceiptRefused:
			p.view.Effect, stage = Known(EffectAbsent), Unsuccessful
		}
		if p.view.Stage != Cancelled {
			p.view.Stage = stage
		}
		return p, nil
	}
	if p.action.kind == CommsTradeRequestAction && receipt == ReceiptRefused {
		p.view.Unresolved = false
		p.view.Effect = Known(EffectAbsent)
		if p.view.Stage != Cancelled {
			p.view.Stage = Unsuccessful
		}
		return p, nil
	}
	if receipt == ReceiptRefused || receipt == ReceiptUnsent {
		p.view.Unresolved = false
		p.view.Effect = Known(EffectAbsent)
		if p.view.Stage != Cancelled {
			p.view.Stage = Pending
		}
		return p, nil
	}
	if p.view.Stage != Cancelled {
		p.view.Stage = AwaitingObservation
	}
	return p, nil
}

// Withdraw opens the attempt that withdraws a cancelled, still-pending
// dispatch natively (#291: a harvest designation nobody took). The action
// stays Cancelled and unresolved under a fresh attempt id, whose receipt and
// observation settle it the ordinary way; a lost receipt resolves as absent
// like any other unadmitted attempt.
func (p Progress) Withdraw(current GenerationSnapshot, tick Tick) (Progress, error) {
	if p.view.Stage != Cancelled || !p.view.Unresolved {
		return p, errors.New("withdrawal requires a cancelled unresolved dispatch")
	}
	if effect, known := p.view.Effect.Value(); !known || effect != EffectPending {
		return p, errors.New("withdrawal requires a pending effect")
	}
	if err := current.Validate(); err != nil {
		return p, err
	}
	if !p.view.Snapshot.sameWorld(current) || tick < p.view.Tick {
		return p, errors.New("stale withdrawal authority or tick")
	}
	if p.view.Attempt == ^AttemptID(0) {
		return p, errors.New("dispatch attempt identity exhausted")
	}
	p.view.Attempt++
	p.view.Snapshot, p.view.Tick = current, tick
	p.view.Receipt, p.view.Effect = Unknown[Receipt](), Unknown[Effect]()
	p.view.UnsuccessfulReason = Unknown[UnsuccessfulReason]()
	return p, nil
}

// A completed intent-mode order is a standing order: its owned draft stays
// held until the planner cancels it (the settled effect is kept, so the plan
// still retires).
func (p Progress) Cancel() (Progress, error) {
	if p.view.Stage == "" || p.view.Stage == Completed && !p.action.kind.IntentMode() || p.view.Stage == Unsuccessful {
		return p, errors.New("cannot cancel this action")
	}
	p.view.Stage = Cancelled
	p.view.HeldReason = Unknown[HoldEvidence]()
	return p, nil
}

// Observe accepts read evidence under current authority, even after direction
// changes, but never attributes effects across colony/map/load changes.
// Terminal evidence must come from a complete native attempt-correlated inspection;
// the executor validates that boundary evidence regardless of tick distance.
func (p Progress) Observe(observation Observation, current GenerationSnapshot) (Progress, error) {
	if p.action.kind.IntentMode() {
		return p, errors.New("an intent's receipt is terminal; there is nothing to observe")
	}
	return p.observe(observation, current)
}
func (p Progress) observe(observation Observation, current GenerationSnapshot) (Progress, error) {
	if !p.view.Unresolved {
		return p, errors.New("no dispatched effect to observe")
	}
	if err := current.Validate(); err != nil {
		return p, err
	}
	if observation.Causality != "" && observation.Causality != AfterDispatch {
		return p, errors.New("invalid observation causality")
	}
	if observation.Action != p.view.Action || observation.Attempt == 0 || observation.Attempt != p.view.Attempt || !observation.Snapshot.Matches(current) || !p.view.Snapshot.sameWorld(current) || observation.Tick < p.view.Tick || observation.Tick == p.view.Tick && observation.Causality != AfterDispatch {
		return p, errors.New("stale or differently scoped observation")
	}
	switch observation.Effect {
	case EffectUnknown, EffectPending, EffectCompleted, EffectAbsent, EffectUnsuccessful:
	default:
		return p, errors.New("invalid observed effect")
	}
	if observation.Effect == EffectUnsuccessful {
		if !observation.UnsuccessfulReason.valid() {
			return p, errors.New("invalid unsuccessful reason")
		}
	} else if observation.UnsuccessfulReason != "" {
		return p, errors.New("unsuccessful reason requires unsuccessful effect")
	}
	p.view.Tick, p.view.Effect = observation.Tick, Known(observation.Effect)
	if observation.Effect == EffectUnsuccessful {
		p.view.UnsuccessfulReason = Known(observation.UnsuccessfulReason)
	}
	if observation.Effect == EffectCompleted || observation.Effect == EffectAbsent || observation.Effect == EffectUnsuccessful {
		p.view.Unresolved = false
		if p.view.Stage != Cancelled {
			if observation.Effect == EffectCompleted {
				p.view.Stage = Completed
			} else if observation.Effect == EffectUnsuccessful {
				p.view.Stage = Unsuccessful
			} else if p.view.Snapshot.Matches(current) {
				p.view.Stage = Pending
			} else {
				p.view.Stage = Cancelled
			}
		}
	}
	return p, nil
}
