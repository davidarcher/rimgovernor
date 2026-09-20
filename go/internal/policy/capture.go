package policy

import (
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// SelectCapture pairs the warden (WardenFor over the roster, "" when none
// qualifies) when it is an available undrafted capturer, otherwise the
// first such capturer by ID, with the first downed, not-yet-captured
// patient (by ID for a stable, deterministic choice), mirroring
// SelectRescue. This is a proposal only; EvaluateCapture re-validates the
// chosen pair.
func SelectCapture(capturers []RescuerFacts, patients []CapturePatientFacts, warden domain.PawnID) (domain.PawnID, domain.PawnID, bool) {
	eligibleCapturer := func(r RescuerFacts) bool {
		dead, dk := r.Dead.Value()
		downed, wk := r.Downed.Value()
		drafted, tk := r.Drafted.Value()
		mental, mk := r.MentalState.Value()
		forced, fk := r.PlayerForced.Value()
		queued, qk := r.QueuedJobs.Value()
		existing, ek := r.ExistingJobDef.Value()
		if !dk || !wk || !tk || !mk || !fk || !qk || !ek {
			return false
		}
		return !dead && !downed && !drafted && !mental && !forced && queued == 0 && existing != "Capture"
	}
	eligiblePatient := func(p CapturePatientFacts) bool {
		dead, dk := p.Dead.Value()
		downed, wk := p.Downed.Value()
		prisoner, pk := p.Prisoner.Value()
		existing, ek := p.ExistingJobDef.Value()
		if !dk || !wk || !pk || !ek {
			return false
		}
		return !dead && downed && !prisoner && existing != "Capture"
	}
	var capturerPool []RescuerFacts
	for _, r := range capturers {
		if eligibleCapturer(r) {
			capturerPool = append(capturerPool, r)
		}
	}
	var patientPool []CapturePatientFacts
	for _, p := range patients {
		if eligiblePatient(p) {
			patientPool = append(patientPool, p)
		}
	}
	if len(capturerPool) == 0 || len(patientPool) == 0 {
		return "", "", false
	}
	sort.Slice(capturerPool, func(i, j int) bool {
		if a, b := capturerPool[i].Pawn == warden, capturerPool[j].Pawn == warden; a != b {
			return a
		}
		return capturerPool[i].Pawn < capturerPool[j].Pawn
	})
	sort.Slice(patientPool, func(i, j int) bool { return patientPool[i].Pawn < patientPool[j].Pawn })
	return capturerPool[0].Pawn, patientPool[0].Pawn, true
}

// CapturePatientFacts describes one candidate capture target. Completion is
// Prisoner true (the native Capture job carries the pawn into a prisoner
// bed and marks it a prisoner); a downed pawn who has simply stopped moving
// is not evidence of a completed capture. Capturer eligibility is identical
// to Rescue's RescuerFacts (an undrafted, capable performer), so it is
// reused rather than duplicated.
type CapturePatientFacts struct {
	Pawn                   domain.PawnID
	SnapshotToken          string
	Dead, Downed, Prisoner domain.Fact[bool]
	ExistingJobDef         domain.Fact[string]
}

type CaptureFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Capturer              RescuerFacts
	Patient               CapturePatientFacts
	NativeCanTry          domain.Fact[bool]
	Emergency             EmergencySnapshot
}

type CaptureRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       CaptureFacts
}

// EvaluateCapture re-validates one already-selected capturer/patient pair
// immediately before dispatch. Admission proves eligibility now; it does not
// prove the capture job will be issued, accepted or completed. Native
// hostility, bed availability, path and reservation eligibility are all
// re-checked by the native preview and surface only through NativeCanTry.
func EvaluateCapture(r CaptureRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	capture, ok := r.Action.Capture()
	canonical, err := domain.NewCaptureAction(r.Action.ID(), capture)
	if !ok || err != nil || canonical != r.Action {
		return refuse(NotReady)
	}
	v := r.Progress.View()
	f := r.Facts
	// The admission anchors on the preview tick, the inspection's one live
	// read; the pawn row may come from the step's fact cache up to the
	// planning tolerance behind it under a running window (#306, #323).
	if r.Current.Validate() != nil || r.Current.Native == 0 || !f.Snapshot.Matches(r.Current) || r.MinimumTick < 0 || f.PreviewTick < r.MinimumTick || !f.PreviewTick.FreshFor(f.PawnTick) {
		return refuse(StaleFacts)
	}
	if r.Progress.Action() != r.Action || v.Plan != r.Current.Plan || v.Revision != r.Current.Revision || v.Unresolved || (v.Stage != domain.Pending && v.Stage != domain.Prepared) {
		return refuse(NotReady)
	}
	if f.PreviewTick < v.Tick || (v.Stage == domain.Prepared && !sameWorld(v.Snapshot, r.Current)) || (v.Attempt > 0 && (v.Snapshot.Colony != r.Current.Colony || v.Snapshot.Map != r.Current.Map || v.Snapshot.Load != r.Current.Load)) {
		return refuse(StaleFacts)
	}
	validToken := func(s string) bool {
		return len(s) <= 256 && utf8.ValidString(s) && strings.TrimSpace(s) != "" && !strings.ContainsRune(s, 0)
	}
	if f.Capturer.Pawn != capture.Capturer() || f.Patient.Pawn != capture.Patient() || !validToken(f.Capturer.SnapshotToken) || !validToken(f.Patient.SnapshotToken) {
		return refuse(UnknownFacts)
	}
	// Combat safety (EmergencyUnsafeThreat) still gates dispatch, matching
	// EvaluateRescue; the capture patient is never a colonist, so it never
	// appears in EmergencyCriticalMedical evidence and needs no exclusion.
	for _, hold := range EvaluateEmergency(f.Emergency, r.Current, f.PreviewTick).Holds {
		switch hold.Reason {
		case EmergencyStaleFacts:
			return refuse(StaleFacts)
		case EmergencyUnknownFacts:
			return refuse(UnknownFacts)
		case EmergencyUnsafeThreat:
			return refuse(UnsupportedThreat)
		}
	}
	for _, fact := range []domain.Fact[bool]{f.Capturer.Dead, f.Capturer.Downed, f.Capturer.Drafted, f.Capturer.MentalState} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	existingCapturerJob, known := f.Capturer.ExistingJobDef.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	dead, _ := f.Capturer.Dead.Value()
	downed, _ := f.Capturer.Downed.Value()
	drafted, _ := f.Capturer.Drafted.Value()
	mental, _ := f.Capturer.MentalState.Value()
	if dead || downed {
		return refuse(CriticalMedical)
	}
	if drafted != capture.Arrest() || mental {
		return refuse(PlayerOrder)
	}
	if existingCapturerJob == "Capture" {
		return refuse(RescuerUnavailable)
	}
	for _, fact := range []domain.Fact[bool]{f.Patient.Dead, f.Patient.Downed, f.Patient.Prisoner} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	existingPatientJob, known := f.Patient.ExistingJobDef.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	patientDead, _ := f.Patient.Dead.Value()
	patientDowned, _ := f.Patient.Downed.Value()
	patientPrisoner, _ := f.Patient.Prisoner.Value()
	if patientDead {
		return refuse(PatientIneligible)
	}
	if patientDowned == capture.Arrest() || patientPrisoner {
		// Already captured or never needed capture; nothing left to admit.
		return refuse(PatientIneligible)
	}
	if existingPatientJob == "Capture" {
		return refuse(PatientIneligible)
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
