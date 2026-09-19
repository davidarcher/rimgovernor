package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

type DraftPawnFacts struct {
	Pawn                           domain.PawnID
	Drafted, Unowned, NativeCanTry domain.Fact[bool]
}
type DraftRequest struct {
	Action    domain.Action
	Progress  domain.Progress
	Current   domain.GenerationSnapshot
	Tick      domain.Tick
	Pawn      DraftPawnFacts
	Emergency EmergencySnapshot
}
type DraftDecision struct {
	Admitted  bool
	Refused   []Refusal
	Emergency EmergencyDecision
}

const (
	PlayerOrder      Reason = "player_order"
	DraftOwnership   Reason = "draft_ownership"
	NativeIneligible Reason = "native_ineligible"
)

// EvaluateOwnedDraft admits only this exact healthy pawn. It does not grant a
// lease, exempt other action families, or replace fresh native CAS validation.
func EvaluateOwnedDraft(request DraftRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: request.Action.ID(), Reason: reason}}}
	}
	// refuseEmergency marks a refusal as caused by the same genuine emergency
	// facts EvaluateEmergency itself would hold on, scoped to only the holds
	// relevant to this pawn's own drafting -- never a colony-wide UnsafeThreat
	// or another pawn's CriticalMedical, which draft deliberately ignores
	// (drafting an uninvolved, healthy pawn during a threat elsewhere is
	// exactly what a player wants to do, not something to block).
	refuseEmergency := func(reason Reason, holds ...EmergencyHold) DraftDecision {
		d := refuse(reason)
		d.Emergency = EmergencyDecision{Holds: holds}
		return d
	}
	draft, ok := request.Action.OwnedDraft()
	if !ok {
		return refuse(NotReady)
	}
	canonical, err := domain.NewOwnedDraftAction(request.Action.ID(), draft)
	if err != nil || canonical != request.Action {
		return refuse(NotReady)
	}
	view := request.Progress.View()
	if request.Current.Validate() != nil || request.Current.Native == 0 || request.Tick < 0 {
		return refuse(StaleFacts)
	}
	if request.Progress.Action() != request.Action || view.Action != request.Action.ID() || view.Plan != request.Current.Plan || view.Revision != request.Current.Revision || view.Unresolved || (view.Stage != domain.Pending && view.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if request.Tick < view.Tick || view.Stage == domain.Prepared && !sameWorld(view.Snapshot, request.Current) {
		return refuse(StaleFacts)
	}
	if view.Attempt > 0 && (view.Snapshot.Colony != request.Current.Colony || view.Snapshot.Map != request.Current.Map || view.Snapshot.Load != request.Current.Load) {
		return refuse(StaleFacts)
	}
	if cleanup, known := view.DraftCleanup.Value(); known && cleanup.Stage != domain.DraftNotAcquired && cleanup.Stage != domain.DraftReleased && cleanup.Stage != domain.DraftSuperseded {
		return refuse(NotReady)
	}
	if request.Pawn.Pawn != draft.Pawn() {
		return refuse(UnknownFacts)
	}
	clearance := EvaluateEmergency(request.Emergency, request.Current, request.Tick)
	for _, hold := range clearance.Holds {
		switch hold.Reason {
		case EmergencyStaleFacts:
			return refuseEmergency(StaleFacts, EmergencyHold{Reason: EmergencyStaleFacts})
		case EmergencyUnknownFacts:
			return refuseEmergency(UnknownFacts, EmergencyHold{Reason: EmergencyUnknownFacts})
		}
	}
	found := false
	for _, pawn := range request.Emergency.facts.Colonists {
		if domain.PawnID(pawn.ID) != draft.Pawn() {
			continue
		}
		found = true
		for _, fact := range []domain.Fact[bool]{pawn.Dead, pawn.Downed, pawn.Bleeding, pawn.NeedsTend} {
			bad, known := fact.Value()
			if !known {
				return refuseEmergency(UnknownFacts, EmergencyHold{Reason: EmergencyUnknownFacts, Pawn: pawn.ID})
			}
			if bad {
				return refuseEmergency(CriticalMedical, EmergencyHold{Reason: EmergencyCriticalMedical, Pawn: pawn.ID})
			}
		}
	}
	if !found {
		return refuseEmergency(UnknownFacts, EmergencyHold{Reason: EmergencyUnknownFacts, Pawn: PawnID(draft.Pawn())})
	}
	for _, fact := range []domain.Fact[bool]{request.Pawn.Drafted, request.Pawn.Unowned, request.Pawn.NativeCanTry} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	unowned, _ := request.Pawn.Unowned.Value()
	eligible, _ := request.Pawn.NativeCanTry.Value()
	// A draft another owned action still claims is refused; a standing draft
	// nobody claims (the player's, made under Manual) is admitted and native
	// adopts it under a fresh claim (#461). Drafted alone is no veto.
	if !unowned {
		return refuse(DraftOwnership)
	}
	if !eligible {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}
