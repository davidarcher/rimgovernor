package policy

import (
	"math"
	"sort"
	"strings"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const (
	// DoctorUnavailable mirrors the Python patient_outcome failure of the same name.
	DoctorUnavailable Reason = "doctor_unavailable"
	PatientIneligible Reason = "patient_ineligible"
)

// TendDoctorFacts describes one undrafted candidate doctor. Self-tend (doctor
// equals patient) is out of scope here: native AI already self-tends a pawn
// with no eligible doctor, so the controller has nothing bounded left to order.
type TendDoctorFacts struct {
	Pawn                       domain.PawnID
	SnapshotToken              string
	Dead, Downed, Drafted      domain.Fact[bool]
	MentalState, PlayerForced  domain.Fact[bool]
	QueuedJobs                 domain.Fact[uint32]
	ExistingJobDef             domain.Fact[string]
	MedicineSkill              domain.Fact[int32]
	MedicineSkillDisabled      domain.Fact[bool]
	DoctorWorkEnabled          domain.Fact[bool]
	DoctorWorkOverrideDisabled domain.Fact[bool]
}

// TendPatientFacts describes one candidate patient. HoursUntilDeathFromBloodLoss
// is only meaningful when Bleeding is known true; callers must not treat an
// unknown/absent value as "not urgent" without checking Bleeding first.
type TendPatientFacts struct {
	Pawn                         domain.PawnID
	SnapshotToken                string
	Dead, Downed                 domain.Fact[bool]
	NeedsTend                    domain.Fact[bool]
	NoCare                       domain.Fact[bool]
	Bleeding, LifeThreatening    domain.Fact[bool]
	HoursUntilDeathFromBloodLoss domain.Fact[float64]
	ExistingJobDef               domain.Fact[string]
}

// SelectTend ports medical_triage.treatment_pairs: rank eligible doctors by
// descending Medicine skill (tie-break by ID), eligible patients by ascending
// bleed-out urgency, then life-threatening, then downed, then ID, and pair the
// first available doctor with the first available patient. This is a proposal
// only; EvaluateTend re-validates the chosen pair against fresh facts.
func SelectTend(doctors []TendDoctorFacts, patients []TendPatientFacts) (domain.PawnID, domain.PawnID, bool) {
	// allowDrafted is only tried once no undrafted doctor is eligible at all --
	// giving up a drafted colonist's current order costs more than an idle
	// undrafted one, so it is strictly a fallback, never a first choice.
	eligibleDoctor := func(d TendDoctorFacts, allowDrafted bool) bool {
		dead, dk := d.Dead.Value()
		downed, wk := d.Downed.Value()
		drafted, tk := d.Drafted.Value()
		mental, mk := d.MentalState.Value()
		forced, fk := d.PlayerForced.Value()
		queued, qk := d.QueuedJobs.Value()
		existing, ek := d.ExistingJobDef.Value()
		skill, sk := d.MedicineSkill.Value()
		skillDisabled, sdk := d.MedicineSkillDisabled.Value()
		enabled, wek := d.DoctorWorkEnabled.Value()
		overrideDisabled, odk := d.DoctorWorkOverrideDisabled.Value()
		if !dk || !wk || !tk || !mk || !fk || !qk || !ek || !sk || !sdk || !wek || !odk {
			return false
		}
		return !dead && !downed && (allowDrafted || !drafted) && !mental && !forced && queued == 0 && existing != "TendPatient" && enabled && !overrideDisabled && !skillDisabled && skill >= 0
	}
	eligiblePatient := func(p TendPatientFacts) bool {
		dead, dk := p.Dead.Value()
		needsTend, nk := p.NeedsTend.Value()
		noCare, ck := p.NoCare.Value()
		existing, ek := p.ExistingJobDef.Value()
		if !dk || !nk || !ck || !ek {
			return false
		}
		return !dead && needsTend && !noCare && existing != "TendPatient"
	}
	var patientPool []TendPatientFacts
	needsTendItself := map[domain.PawnID]bool{}
	for _, p := range patients {
		if eligiblePatient(p) {
			patientPool = append(patientPool, p)
			needsTendItself[p.Pawn] = true
		}
	}
	// A pawn that itself needs tend is never picked as someone else's doctor
	// here -- domain.NewTend requires distinct doctor/patient identities, and
	// TendDoctorFacts' own doc comment already puts self-tend out of scope
	// (native AI self-tends a pawn with no eligible doctor on its own). Without
	// this exclusion, a mildly ill but otherwise eligible pawn (not dead,
	// downed or mentally broken) can rank first in both pools -- e.g. three
	// colonists each with a mild Flu/withdrawal condition and equal Medicine
	// skill sort to the same lowest-ID pawn in both lists -- which
	// domain.NewTend then refuses, stalling the routine clock step on repeat.
	var doctorPool []TendDoctorFacts
	for _, d := range doctors {
		if !needsTendItself[d.Pawn] && eligibleDoctor(d, false) {
			doctorPool = append(doctorPool, d)
		}
	}
	if len(doctorPool) == 0 {
		for _, d := range doctors {
			if !needsTendItself[d.Pawn] && eligibleDoctor(d, true) {
				doctorPool = append(doctorPool, d)
			}
		}
	}
	if len(doctorPool) == 0 || len(patientPool) == 0 {
		return "", "", false
	}
	sort.SliceStable(doctorPool, func(i, j int) bool {
		si, _ := doctorPool[i].MedicineSkill.Value()
		sj, _ := doctorPool[j].MedicineSkill.Value()
		if si != sj {
			return si > sj
		}
		return doctorPool[i].Pawn < doctorPool[j].Pawn
	})
	sort.SliceStable(patientPool, func(i, j int) bool {
		a, b := patientPool[i], patientPool[j]
		ah, ableed := a.Bleeding.Value()
		bh, bbleed := b.Bleeding.Value()
		aHours, aHK := a.HoursUntilDeathFromBloodLoss.Value()
		bHours, bHK := b.HoursUntilDeathFromBloodLoss.Value()
		if !(ableed && ah && aHK) {
			aHours = math.Inf(1)
		}
		if !(bbleed && bh && bHK) {
			bHours = math.Inf(1)
		}
		if aHours != bHours {
			return aHours < bHours
		}
		aLife, _ := a.LifeThreatening.Value()
		bLife, _ := b.LifeThreatening.Value()
		if aLife != bLife {
			return aLife
		}
		aDown, _ := a.Downed.Value()
		bDown, _ := b.Downed.Value()
		if aDown != bDown {
			return aDown
		}
		return a.Pawn < b.Pawn
	})
	return doctorPool[0].Pawn, patientPool[0].Pawn, true
}

type TendFacts struct {
	Snapshot              domain.GenerationSnapshot
	PawnTick, PreviewTick domain.Tick
	Doctor                TendDoctorFacts
	Patient               TendPatientFacts
	NativeCanTry          domain.Fact[bool]
	Emergency             EmergencySnapshot
}

type TendRequest struct {
	Action      domain.Action
	Progress    domain.Progress
	Current     domain.GenerationSnapshot
	MinimumTick domain.Tick
	Facts       TendFacts
}

// EvaluateTend re-validates one already-selected doctor/patient pair immediately
// before dispatch. Admission proves eligibility now; it does not prove the tend
// job will be issued, accepted or completed.
func EvaluateTend(r TendRequest) DraftDecision {
	refuse := func(reason Reason) DraftDecision {
		return DraftDecision{Refused: []Refusal{{Action: r.Action.ID(), Reason: reason}}}
	}
	tend, ok := r.Action.Tend()
	canonical, err := domain.NewTendAction(r.Action.ID(), tend)
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
	if f.Doctor.Pawn != tend.Doctor() || f.Patient.Pawn != tend.Patient() || !validToken(f.Doctor.SnapshotToken) || !validToken(f.Patient.SnapshotToken) {
		return refuse(UnknownFacts)
	}
	drafted, known := f.Doctor.Drafted.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	// EmergencyCriticalMedical fires for any bleeding/downed/untended colonist,
	// including the patient this action exists to treat, so it is deliberately
	// excluded here. Combat safety (EmergencyUnsafeThreat) gates an undrafted
	// doctor's dispatch: ordinary triage only proceeds once threats_cleared,
	// matching the Python reference. A drafted doctor is exempted -- the
	// controller drafted them itself, so sending them to tend a dying colonist
	// is the same class of decision as drafting a healthy pawn during a threat
	// elsewhere (see EvaluateOwnedDraft): native's own job-acceptance check
	// (NativeCanTry below) remains the live safety authority for the order.
	for _, hold := range EvaluateEmergency(f.Emergency, r.Current, f.PreviewTick).Holds {
		switch hold.Reason {
		case EmergencyStaleFacts:
			return refuse(StaleFacts)
		case EmergencyUnknownFacts:
			return refuse(UnknownFacts)
		case EmergencyUnsafeThreat:
			if !drafted {
				return refuse(UnsupportedThreat)
			}
		}
	}
	for _, fact := range []domain.Fact[bool]{f.Doctor.Dead, f.Doctor.Downed, f.Doctor.MentalState, f.Doctor.PlayerForced} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	queued, known := f.Doctor.QueuedJobs.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	existingDoctorJob, known := f.Doctor.ExistingJobDef.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	skill, known := f.Doctor.MedicineSkill.Value()
	if !known || skill < 0 {
		return refuse(UnknownFacts)
	}
	skillDisabled, known := f.Doctor.MedicineSkillDisabled.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	enabled, known := f.Doctor.DoctorWorkEnabled.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	overrideDisabled, known := f.Doctor.DoctorWorkOverrideDisabled.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	dead, _ := f.Doctor.Dead.Value()
	downed, _ := f.Doctor.Downed.Value()
	mental, _ := f.Doctor.MentalState.Value()
	forced, _ := f.Doctor.PlayerForced.Value()
	if dead || downed {
		return refuse(CriticalMedical)
	}
	if mental || forced || queued != 0 {
		return refuse(PlayerOrder)
	}
	if existingDoctorJob == "TendPatient" {
		return refuse(DoctorUnavailable)
	}
	if !enabled || overrideDisabled || skillDisabled {
		return refuse(DoctorUnavailable)
	}
	for _, fact := range []domain.Fact[bool]{f.Patient.Dead, f.Patient.Downed, f.Patient.NeedsTend, f.Patient.NoCare} {
		if _, known := fact.Value(); !known {
			return refuse(UnknownFacts)
		}
	}
	existingPatientJob, known := f.Patient.ExistingJobDef.Value()
	if !known {
		return refuse(UnknownFacts)
	}
	patientDead, _ := f.Patient.Dead.Value()
	needsTend, _ := f.Patient.NeedsTend.Value()
	noCare, _ := f.Patient.NoCare.Value()
	if patientDead {
		return refuse(PatientIneligible)
	}
	if !needsTend {
		// The patient recovered or was already treated; nothing left to admit.
		return refuse(PatientIneligible)
	}
	if noCare || existingPatientJob == "TendPatient" {
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
