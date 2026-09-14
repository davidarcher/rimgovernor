package policy

import (
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SurgeryPatientUnavailable mirrors BedAssignPawnUnavailable: the patient is
// dead or downed and cannot receive a fresh operation bill right now.
const SurgeryPatientUnavailable Reason = "surgery_patient_unavailable"

// SurgeryPatientFacts describes the one already-selected patient the player
// explicitly named. Unlike TendPatientFacts, there is no doctor role and no
// NeedsTend/NoCare facts: MedicalOperationsTool establishes recipe/body-part/
// ingredient/practitioner eligibility and current health/care CAS tokens at
// inspection, not here.
type SurgeryPatientFacts struct {
	Patient       domain.PawnID
	SnapshotToken string
	Dead, Downed  domain.Fact[bool]
}

type SurgeryFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Patient               SurgeryPatientFacts
	// HealthToken is the exact health-signature CAS token
	// NativeSurgeryOperations computed at inspection (hediffs/parts), distinct
	// from the patient's generic pawn snapshot token: it invalidates whenever
	// any hediff changes, not merely position/stack/forbid-style facts.
	HealthToken  string
	Care         domain.MedicalCare
	NativeCanTry domain.Fact[bool]
}

type SurgeryRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       SurgeryFacts
}

// EvaluateSurgery re-validates one already-selected patient/recipe/part triple
// immediately before dispatch, the same shape EvaluateBedAssign uses.
// Admission proves eligibility now; it does not prove the operation bill will
// be accepted, run to completion, or achieve its health postcondition (that is
// checked later, by observing hediffs).
func EvaluateSurgery(r SurgeryRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	surgery, ok := r.Action.Surgery()
	canonical, err := domain.NewSurgeryAction(r.Action.ID(), surgery)
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
	if f.Patient.Patient != surgery.Patient() || !validToken(f.Patient.SnapshotToken) || !validToken(f.HealthToken) || !domain.ValidMedicalCare(f.Care) {
		return refuse(UnknownFacts)
	}
	for _, fact := range []domain.Fact[bool]{f.Patient.Dead, f.Patient.Downed} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	dead, _ := f.Patient.Dead.Value()
	downed, _ := f.Patient.Downed.Value()
	if dead || downed {
		return refuse(SurgeryPatientUnavailable)
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
