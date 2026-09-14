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
	ReceiptUnknown Receipt = "unknown"
)

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

	HeldBedAssignPawnUnavailable        HeldReason = "bed_assign_pawn_unavailable"
	HeldCaravanCrewUnavailable          HeldReason = "caravan_crew_unavailable"
	HeldCaravanHomeFoodInsufficient     HeldReason = "caravan_home_food_insufficient"
	HeldCaravanHomeStaffingInsufficient HeldReason = "caravan_home_staffing_insufficient"
	HeldCaravanRouteUnavailable         HeldReason = "caravan_route_unavailable"
	HeldCleanerUnavailable              HeldReason = "cleaner_unavailable"
	HeldDoctorUnavailable               HeldReason = "doctor_unavailable"
	HeldDraftOwnership                  HeldReason = "draft_ownership"
	HeldEquipPawnUnavailable            HeldReason = "equip_pawn_unavailable"
	HeldFilthIneligible                 HeldReason = "filth_ineligible"
	HeldGearReplacePawnUnavailable      HeldReason = "gear_replace_pawn_unavailable"
	HeldHaulerUnavailable               HeldReason = "hauler_unavailable"
	HeldHomeCoverageExcluded            HeldReason = "home_coverage_excluded"
	HeldHomeCoverageGeometryChanged     HeldReason = "home_coverage_geometry_changed"
	HeldHusbandryAnimalUnavailable      HeldReason = "husbandry_animal_unavailable"
	HeldInsufficientReserve             HeldReason = "insufficient_reserve"
	HeldNativeIneligible                HeldReason = "native_ineligible"
	HeldPatientIneligible               HeldReason = "patient_ineligible"
	HeldPlayerOrder                     HeldReason = "player_order"
	HeldPrisonerUnavailable             HeldReason = "prisoner_unavailable"
	HeldProductionPolicySatisfied       HeldReason = "production_policy_satisfied"
	HeldQuestUnavailable                HeldReason = "quest_unavailable"
	HeldRecoveryServicePawnUnavailable  HeldReason = "recovery_service_pawn_unavailable"
	HeldRepairerUnavailable             HeldReason = "repairer_unavailable"
	HeldRescuerUnavailable              HeldReason = "rescuer_unavailable"
	HeldResearchProjectClaimed          HeldReason = "research_project_claimed"
	HeldSettlementUnavailable           HeldReason = "settlement_unavailable"
	HeldStructureIneligible             HeldReason = "structure_ineligible"
	HeldUnsuitableEquipment             HeldReason = "unsuitable_equipment"
	HeldUnsupportedThreat               HeldReason = "unsupported_threat"
	HeldWallRemovalGeometryChanged      HeldReason = "wall_removal_geometry_changed"
	HeldWallRemovalTargetChanged        HeldReason = "wall_removal_target_changed"
)

// orderedHeldReasons lists every reason in the fixed, deterministic order
// HoldEvidence.Reasons() decodes them in; bit position within this slice is
// the single source of truth for the packed encoding below.
var orderedHeldReasons = []HeldReason{
	HeldUnsafeThreat, HeldCriticalMedical, HeldStaleFacts, HeldUnknownFacts,
	HeldNotReady, HeldAlreadyReserved, HeldUnsafePlacement, HeldMaterialRequired,
	HeldDependencyBlocked, HeldGeometryBlocked, HeldSpendingBlocked, HeldInsufficientStock,
	HeldInvalidHeld, HeldArithmeticOverflow,
	HeldBedAssignPawnUnavailable, HeldCaravanCrewUnavailable, HeldCaravanHomeFoodInsufficient,
	HeldCaravanHomeStaffingInsufficient, HeldCaravanRouteUnavailable, HeldCleanerUnavailable,
	HeldDoctorUnavailable, HeldDraftOwnership, HeldEquipPawnUnavailable, HeldFilthIneligible,
	HeldGearReplacePawnUnavailable, HeldHaulerUnavailable, HeldHomeCoverageExcluded,
	HeldHomeCoverageGeometryChanged, HeldHusbandryAnimalUnavailable, HeldInsufficientReserve,
	HeldNativeIneligible, HeldPatientIneligible, HeldPlayerOrder, HeldPrisonerUnavailable,
	HeldProductionPolicySatisfied, HeldQuestUnavailable, HeldRecoveryServicePawnUnavailable,
	HeldRepairerUnavailable, HeldRescuerUnavailable, HeldResearchProjectClaimed,
	HeldSettlementUnavailable, HeldStructureIneligible, HeldUnsuitableEquipment,
	HeldUnsupportedThreat, HeldWallRemovalGeometryChanged, HeldWallRemovalTargetChanged,
}

func (r HeldReason) valid() bool {
	return r.bit() != 0
}

// heldReasonBits packs every hold reason into a comparable value so
// ProgressView (compared by == elsewhere) stays comparable; a slice field
// could not. 45 reasons currently exist, comfortably under the 64-bit cap;
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
	Construction         *ConstructionIdentity `json:",omitempty"`
	Action               ActionID
	Attempt              AttemptID
	Snapshot             GenerationSnapshot
	Tick                 Tick
	Effect               Effect
	Causality            ObservationCausality `json:",omitempty"`
	UnsuccessfulReason   UnsuccessfulReason   `json:",omitempty"`
	ConstructionObserved bool                 `json:",omitempty"`
}
type ProgressView struct {
	Construction       Fact[ConstructionIdentity]
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
	DraftCleanup       Fact[DraftCleanup]
	// Earliest complete correlated inspection in the current run of known pending
	// evidence. A later game tick can safely replace historic cost with net stock.
	ConstructionObserved Fact[Tick]
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
func (p Progress) Prepare(snapshot GenerationSnapshot, tick Tick) (Progress, error) {
	if p.view.Stage != Pending || p.view.Unresolved || p.draftCleanupOutstanding() {
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
	p.view.HeldReason = Known(HoldEvidence{reasons: bits, Plan: p.view.Plan, Revision: p.view.Revision, Tick: tick})
	return p, nil
}

// FreshHeldReason re-verifies HeldReason evidence against the view carrying
// it, rather than trusting the stored Fact alone. Any DTO projection must go
// through this instead of reading HeldReason directly.
func (v ProgressView) FreshHeldReason() ([]HeldReason, bool) {
	if v.Stage != Pending && v.Stage != Prepared || v.Unresolved {
		return nil, false
	}
	evidence, known := v.HeldReason.Value()
	if !known || evidence.Plan != v.Plan || evidence.Revision != v.Revision || evidence.Tick < v.Tick {
		return nil, false
	}
	return evidence.Reasons(), true
}
func (p Progress) MarkDispatched(current GenerationSnapshot, tick Tick) (Progress, error) {
	if p.view.Stage != Prepared || !p.view.Snapshot.Matches(current) || tick < p.view.Tick {
		return p, errors.New("dispatch requires current prepared authority")
	}
	if p.view.Attempt == ^AttemptID(0) {
		return p, errors.New("dispatch attempt identity exhausted")
	}
	if p.draftCleanupOutstanding() {
		return p, errors.New("draft cleanup remains outstanding")
	}
	if p.action.kind == OwnedDraftAction && (current.Native == 0 || current.Direction == 0) {
		return p, errors.New("draft dispatch requires native generation and player direction")
	}
	p.view.Attempt++
	p.view.Stage, p.view.Unresolved, p.view.Tick = Dispatched, true, tick
	p.view.Receipt, p.view.Effect = Unknown[Receipt](), Unknown[Effect]()
	p.view.UnsuccessfulReason = Unknown[UnsuccessfulReason]()
	p.view.HeldReason = Unknown[HoldEvidence]()
	p.view.Construction = Unknown[ConstructionIdentity]()
	p.view.ConstructionObserved = Unknown[Tick]()
	if p.action.kind == OwnedDraftAction {
		p.view.DraftCleanup = Known(DraftCleanup{Stage: DraftAwaitingClaim})
	}
	return p, nil
}
func (p Progress) RecordReceipt(attempt AttemptID, receipt Receipt) (Progress, error) {
	if p.action.kind == OwnedDraftAction {
		return p, errors.New("draft receipt requires typed claim transition")
	}
	return p.recordReceipt(attempt, receipt)
}
func (p Progress) recordReceipt(attempt AttemptID, receipt Receipt) (Progress, error) {
	if attempt == 0 || attempt != p.view.Attempt {
		return p, errors.New("receipt belongs to a different dispatch attempt")
	}
	if p.view.Stage != Dispatched && !(p.view.Stage == Cancelled && p.view.Unresolved) {
		return p, errors.New("receipt requires dispatched action")
	}
	switch receipt {
	case ReceiptAccepted, ReceiptRefused, ReceiptUnknown:
	default:
		return p, errors.New("invalid receipt")
	}
	if _, known := p.view.Receipt.Value(); known {
		return p, errors.New("receipt already recorded")
	}
	p.view.Receipt = Known(receipt)
	p.view.HeldReason = Unknown[HoldEvidence]()
	if receipt == ReceiptRefused {
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
func (p Progress) Cancel() (Progress, error) {
	if p.view.Stage == "" || p.view.Stage == Completed || p.view.Stage == Unsuccessful {
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
	if p.action.kind == OwnedDraftAction {
		return p, errors.New("draft observation requires typed claim transition")
	}
	return p.observe(observation, current)
}
func (p Progress) observe(observation Observation, current GenerationSnapshot) (Progress, error) {
	if observation.Construction != nil && (p.action.Kind() != BuildingAction || observation.Effect != EffectCompleted || observation.Causality != AfterDispatch || observation.Construction.Validate() != nil) {
		return p, errors.New("construction identity requires correlated completed building evidence")
	}
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
	if observation.ConstructionObserved && (p.action.Kind() != BuildingAction || observation.Effect != EffectPending || observation.Causality != AfterDispatch) {
		return p, errors.New("construction accounting requires correlated pending building evidence")
	}
	if observation.ConstructionObserved {
		if _, known := p.view.ConstructionObserved.Value(); !known {
			p.view.ConstructionObserved = Known(observation.Tick)
		}
	} else {
		p.view.ConstructionObserved = Unknown[Tick]()
	}
	if observation.Construction != nil {
		p.view.Construction = Known(*observation.Construction)
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
