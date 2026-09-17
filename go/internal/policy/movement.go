package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// EvaluateMovement admits ordinary walk-to-cell dispatch under an existing
// exact draft claim. It proves nothing about pathability or completion.
func EvaluateMovement(r MovementRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	m, ok := r.Action.Movement()
	canonical, err := domain.NewMovementAction(r.Action.ID(), m)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v, d := r.Progress.View(), r.DraftProgress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PawnTick < r.MinimumTick || f.PreviewTick < f.PawnTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PawnTick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	draft, isDraft := r.DraftProgress.Action().OwnedDraft()
	if !isDraft || d.Action != m.DraftAction() || draft.Pawn() != m.Pawn() || d.Plan != r.Current.Plan || d.Revision != r.Current.Revision || d.Stage != domain.Completed || d.Unresolved {
		return refuse(DraftOwnership)
	}
	cleanup, known := d.DraftCleanup.Value()
	claim, claimed := cleanup.Claim.Value()
	owner, owned := f.Pawn.Owner.Value()
	if !known || cleanup.Stage != domain.DraftCleanupRequired || !claimed || !owned || claim.Action != d.Action || claim.Attempt != d.Attempt || claim.Pawn != m.Pawn() || !claim.Origin.Matches(r.Current) || owner.Claim != claim.Claim || owner.Session != claim.Session {
		return refuse(DraftOwnership)
	}
	if f.PawnTick < d.Tick {
		return refuse(StaleFacts)
	}
	if f.Pawn.Pawn != m.Pawn() {
		return refuse(UnknownFacts)
	}
	for _, hold := range EvaluateEmergency(f.Emergency, r.Current, f.PreviewTick).Holds {
		switch hold.Reason {
		case EmergencyStaleFacts:
			return refuse(StaleFacts)
		case EmergencyUnknownFacts:
			return refuse(UnknownFacts)
		case EmergencyCriticalMedical:
			return refuse(CriticalMedical)
		}
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.Dead, f.Pawn.Downed, f.Pawn.Bleeding, f.Pawn.NeedsTend, f.Pawn.FreeColonist, f.Pawn.Drafted, f.NativeCanTry} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.Dead, f.Pawn.Downed, f.Pawn.Bleeding, f.Pawn.NeedsTend} {
		bad, _ := fact.Value()
		if bad {
			return refuse(CriticalMedical)
		}
	}
	free, _ := f.Pawn.FreeColonist.Value()
	if !free {
		return refuse(NativeIneligible)
	}
	drafted, _ := f.Pawn.Drafted.Value()
	if !drafted {
		return refuse(DraftOwnership)
	}
	eligible, _ := f.NativeCanTry.Value()
	if !eligible {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}
