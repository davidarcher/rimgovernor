package policy

import (
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const RescuerUnavailable Reason = "rescuer_unavailable"

// RescuerFacts describes one undrafted candidate rescuer.
type RescuerFacts struct {
	Pawn                      domain.PawnID
	SnapshotToken             string
	Dead, Downed, Drafted     domain.Fact[bool]
	MentalState, PlayerForced domain.Fact[bool]
	QueuedJobs                domain.Fact[uint32]
	ExistingJobDef            domain.Fact[string]
}

// RescuePatientFacts describes one candidate patient. Completion is InBed with
// a known non-empty BedID; a downed pawn who has simply stopped moving is not
// evidence of a completed rescue.
type RescuePatientFacts struct {
	Pawn                domain.PawnID
	SnapshotToken       string
	Dead, Downed, InBed domain.Fact[bool]
	BedID               domain.Fact[string]
	ExistingJobDef      domain.Fact[string]
}

// SelectRescue pairs the first available undrafted rescuer with the first
// downed, not-yet-bedded patient (by ID for a stable, deterministic choice).
// This is a proposal only; EvaluateRescue re-validates the chosen pair.
func SelectRescue(rescuers []RescuerFacts, patients []RescuePatientFacts) (domain.PawnID, domain.PawnID, bool) {
	eligibleRescuer := func(r RescuerFacts) bool {
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
		return !dead && !downed && !drafted && !mental && !forced && queued == 0 && existing != "Rescue"
	}
	eligiblePatient := func(p RescuePatientFacts) bool {
		dead, dk := p.Dead.Value()
		downed, wk := p.Downed.Value()
		inBed, bk := p.InBed.Value()
		existing, ek := p.ExistingJobDef.Value()
		if !dk || !wk || !bk || !ek {
			return false
		}
		return !dead && downed && !inBed && existing != "Rescue"
	}
	var rescuerPool []RescuerFacts
	for _, r := range rescuers {
		if eligibleRescuer(r) {
			rescuerPool = append(rescuerPool, r)
		}
	}
	var patientPool []RescuePatientFacts
	for _, p := range patients {
		if eligiblePatient(p) {
			patientPool = append(patientPool, p)
		}
	}
	if len(rescuerPool) == 0 || len(patientPool) == 0 {
		return "", "", false
	}
	sort.Slice(rescuerPool, func(i, j int) bool { return rescuerPool[i].Pawn < rescuerPool[j].Pawn })
	sort.Slice(patientPool, func(i, j int) bool { return patientPool[i].Pawn < patientPool[j].Pawn })
	return rescuerPool[0].Pawn, patientPool[0].Pawn, true
}

type RescueFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Rescuer               RescuerFacts
	Patient               RescuePatientFacts
	NativeCanTry          domain.Fact[bool]
	Emergency             EmergencySnapshot
}

type RescueRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       RescueFacts
}

// EvaluateRescue re-validates one already-selected rescuer/patient pair
// immediately before dispatch. Admission proves eligibility now; it does not
// prove the rescue job will be issued, accepted or completed.
func EvaluateRescue(r RescueRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	rescue, ok := r.Action.Rescue()
	canonical, err := domain.NewRescueAction(r.Action.ID(), rescue)
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
	if f.Rescuer.Pawn != rescue.Rescuer() || f.Patient.Pawn != rescue.Patient() || !validToken(f.Rescuer.SnapshotToken) || !validToken(f.Patient.SnapshotToken) {
		return refuse(UnknownFacts)
	}
	// EmergencyCriticalMedical fires for the downed patient this action exists
	// to rescue, so it is deliberately excluded here, matching EvaluateTend.
	// Combat safety (EmergencyUnsafeThreat) still gates dispatch.
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
	for _, fact := range []domain.Fact[bool]{f.Rescuer.Dead, f.Rescuer.Downed, f.Rescuer.Drafted, f.Rescuer.MentalState, f.Rescuer.PlayerForced} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	queued, known := f.Rescuer.QueuedJobs.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	existingRescuerJob, known := f.Rescuer.ExistingJobDef.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	dead, _ := f.Rescuer.Dead.Value()
	downed, _ := f.Rescuer.Downed.Value()
	drafted, _ := f.Rescuer.Drafted.Value()
	mental, _ := f.Rescuer.MentalState.Value()
	forced, _ := f.Rescuer.PlayerForced.Value()
	if dead || downed {
		return refuse(CriticalMedical)
	}
	if drafted || mental || forced || queued != 0 {
		return refuse(PlayerOrder)
	}
	if existingRescuerJob == "Rescue" {
		return refuse(RescuerUnavailable)
	}
	for _, fact := range []domain.Fact[bool]{f.Patient.Dead, f.Patient.Downed, f.Patient.InBed} {
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
	patientInBed, _ := f.Patient.InBed.Value()
	if patientDead {
		return refuse(PatientIneligible)
	}
	if !patientDowned || patientInBed {
		// Already rescued or never needed rescue; nothing left to admit.
		return refuse(PatientIneligible)
	}
	if existingPatientJob == "Rescue" {
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
