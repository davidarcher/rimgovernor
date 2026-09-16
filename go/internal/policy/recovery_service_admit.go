package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RecoveryServicePawnUnavailable mirrors GearReplacePawnUnavailable: the pawn
// is dead, downed or otherwise cannot be given a fresh order right now.
const RecoveryServicePawnUnavailable Reason = "recovery_service_pawn_unavailable"

// RecoveryServicePawnFacts describes the one already-selected undrafted pawn
// SelectRecoveryMethods chose for a repair, breakdown restoration or refuel job.
type RecoveryServicePawnFacts struct {
	Pawn                  domain.PawnID
	SnapshotToken         string
	Dead, Downed, Drafted domain.Fact[bool]
	MentalState           domain.Fact[bool]
	ExistingJobDef        domain.Fact[string]
}

type RecoveryServiceFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Pawn                  RecoveryServicePawnFacts
	ThingSnapshotToken    string
	NativeCanTry          domain.Fact[bool]
}

type RecoveryServiceRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       RecoveryServiceFacts
}

// EvaluateRecoveryService re-validates one already-selected pawn/building
// pair immediately before dispatch, the same shape EvaluateGearReplace uses.
// Admission proves eligibility now; it does not prove the service job will be
// issued, accepted or completed (that is observed later).
func EvaluateRecoveryService(r RecoveryServiceRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	service, ok := r.Action.RecoveryService()
	canonical, err := domain.NewRecoveryServiceAction(r.Action.ID(), service)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	if r.Current.Validate() != nil || r.Current.Native == 0 || r.Current.Direction == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PawnTick < r.MinimumTick || f.PreviewTick < f.PawnTick {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PawnTick < v.Tick || (v.Stage == domain.Prepared && !v.Snapshot.Matches(r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Pawn.Pawn != service.Pawn() || !validToken(f.Pawn.SnapshotToken) || !validToken(f.ThingSnapshotToken) {
		return refuse(UnknownFacts)
	}
	for _, fact := range []domain.Fact[bool]{f.Pawn.Dead, f.Pawn.Downed, f.Pawn.Drafted, f.Pawn.MentalState} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	if _, known := f.Pawn.ExistingJobDef.Value(); !known {
		return refuse(UnknownFacts)
	}
	dead, _ := f.Pawn.Dead.Value()
	downed, _ := f.Pawn.Downed.Value()
	drafted, _ := f.Pawn.Drafted.Value()
	mental, _ := f.Pawn.MentalState.Value()
	if dead || downed {
		return refuse(RecoveryServicePawnUnavailable)
	}
	if drafted || mental {
		return refuse(PlayerOrder)
	}
	eligible, known := f.NativeCanTry.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	if !eligible {
		return refuse(NativeIneligible)
	}
	return DraftDecision{Admitted: true}
}
