package policy

import (
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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
	// The native tend gates the Go predicate used to leave unmodelled, which
	// burned every CriticalMedical attempt on orders that could not succeed
	// (#657). ControlEligible is NativePawnControlState's own eligibility
	// (a drafter exists, spawned, alive, not downed, not mental,
	// player-controlled) -- the gate behind the refusal "Tend requires an
	// eligible doctor". TendCapacities is WorkGiver_Tend.MissingRequiredCapacity
	// coming back empty. ReachesPatient answers CanReach(ClosestTouch, Deadly)
	// for the one patient this pair names; multi-candidate selection uses
	// TendReachability instead.
	ControlEligible domain.Fact[bool]
	TendCapacities  domain.Fact[bool]
	ReachesPatient  domain.Fact[bool]
}

// TendReachability answers CanReach for the doctor/patient candidates observed
// together in one read. A doctor with no observed row reaches nothing known, so
// SelectTend never proposes it: an unreachable doctor is refused natively.
type TendReachability struct {
	reach map[domain.PawnID]map[domain.PawnID]bool
}

// ObserveTendReachability records, per doctor, the candidate patients native
// reported as reachable. A doctor absent from reachable has no fact at all.
func ObserveTendReachability(reachable map[domain.PawnID][]domain.PawnID) TendReachability {
	out := TendReachability{reach: make(map[domain.PawnID]map[domain.PawnID]bool, len(reachable))}
	for doctor, patients := range reachable {
		row := make(map[domain.PawnID]bool, len(patients))
		for _, patient := range patients {
			row[patient] = true
		}
		out.reach[doctor] = row
	}
	return out
}

// Reaches is unknown when the doctor was never observed, and a known false when
// it was observed and the patient is not in its reachable set.
func (r TendReachability) Reaches(doctor, patient domain.PawnID) domain.Fact[bool] {
	row, observed := r.reach[doctor]
	if !observed {
		return domain.Unknown[bool]()
	}
	return domain.Known(row[patient])
}

// TendPatientFacts describes one candidate patient. HoursUntilDeathFromBloodLoss
// is only meaningful when Bleeding is known true; callers must not treat an
// unknown/absent value as "not urgent" without checking Bleeding first.
type TendPatientFacts struct {
	Pawn          domain.PawnID
	SnapshotToken string
	Dead, Downed  domain.Fact[bool]
	// InBed with Downed decides whether the patient can be tended at all:
	// WorkGiver_Tend tends a humanlike only in a bed and the ground tend
	// needs a downed pawn, so an up patient out of bed is refused natively
	// however urgent (#618).
	InBed                        domain.Fact[bool]
	NeedsTend                    domain.Fact[bool]
	NoCare                       domain.Fact[bool]
	Bleeding, LifeThreatening    domain.Fact[bool]
	HoursUntilDeathFromBloodLoss domain.Fact[float64]
	ExistingJobDef               domain.Fact[string]
}

// SelectTend ports medical_triage.treatment_pairs: rank eligible doctors by
// interruption cost, then descending Medicine skill (tie-break by ID), and patients by ascending
// bleed-out urgency, then life-threatening, then downed, then ID, and pair the
// first available doctor with the first available patient. This is a proposal
// only; EvaluateTend re-validates the chosen pair against fresh facts.
func SelectTend(doctors []TendDoctorFacts, patients []TendPatientFacts, reach TendReachability) (domain.PawnID, domain.PawnID, bool) {
	// allowDrafted is only tried once no undrafted doctor is eligible at all --
	// giving up a drafted colonist's current order costs more than an idle
	// undrafted one, so it is strictly a fallback, never a first choice.
	eligibleDoctor := func(d TendDoctorFacts, allowDrafted bool) bool {
		dead, dk := d.Dead.Value()
		downed, wk := d.Downed.Value()
		drafted, tk := d.Drafted.Value()
		mental, mk := d.MentalState.Value()
		existing, ek := d.ExistingJobDef.Value()
		skill, sk := d.MedicineSkill.Value()
		skillDisabled, sdk := d.MedicineSkillDisabled.Value()
		enabled, wek := d.DoctorWorkEnabled.Value()
		overrideDisabled, odk := d.DoctorWorkOverrideDisabled.Value()
		controlEligible, cek := d.ControlEligible.Value()
		capacities, cak := d.TendCapacities.Value()
		if !dk || !wk || !tk || !mk || !ek || !sk || !sdk || !wek || !odk || !cek || !cak {
			return false
		}
		return !dead && !downed && (allowDrafted || !drafted) && !mental && existing != "TendPatient" && enabled && !overrideDisabled && !skillDisabled && skill >= 0 && controlEligible && capacities
	}
	eligiblePatient := func(p TendPatientFacts) bool {
		dead, dk := p.Dead.Value()
		needsTend, nk := p.NeedsTend.Value()
		noCare, ck := p.NoCare.Value()
		existing, ek := p.ExistingJobDef.Value()
		if !dk || !nk || !ck || !ek {
			return false
		}
		return !dead && needsTend && !noCare && existing != "TendPatient" && tendable(p)
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
	if len(patientPool) == 0 {
		return "", "", false
	}
	// The drafted fallback is reachability-aware too: an undrafted pool whose
	// every member is walled off from the patients is no better than an empty
	// one, so it falls through to the drafted candidates rather than reporting
	// no pair (#657).
	if !anyReachable(doctorPool, patientPool, reach) {
		doctorPool = nil
		for _, d := range doctors {
			if !needsTendItself[d.Pawn] && eligibleDoctor(d, true) {
				doctorPool = append(doctorPool, d)
			}
		}
	}
	if len(doctorPool) == 0 {
		return "", "", false
	}
	sort.SliceStable(doctorPool, func(i, j int) bool {
		a, b := orderedWorkCost(doctorPool[i].PlayerForced, doctorPool[i].QueuedJobs), orderedWorkCost(doctorPool[j].PlayerForced, doctorPool[j].QueuedJobs)
		if a != b {
			return a < b
		}
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
	// Reachability is the one pairwise gate, so the pair is the first patient
	// in urgency order some ranked doctor can actually reach. With every pair
	// reachable this is the head of each pool, the ranking above.
	for _, patient := range patientPool {
		for _, doctor := range doctorPool {
			if reaches, known := reach.Reaches(doctor.Pawn, patient.Pawn).Value(); known && reaches {
				return doctor.Pawn, patient.Pawn, true
			}
		}
	}
	return "", "", false
}

func anyReachable(doctors []TendDoctorFacts, patients []TendPatientFacts, reach TendReachability) bool {
	for _, d := range doctors {
		for _, p := range patients {
			if reaches, known := reach.Reaches(d.Pawn, p.Pawn).Value(); known && reaches {
				return true
			}
		}
	}
	return false
}

// tendable reports whether a doctor can tend the patient where they are: in
// a bed, or downed on the ground. Both facts must be known.
func tendable(p TendPatientFacts) bool {
	downed, dk := p.Downed.Value()
	inBed, bk := p.InBed.Value()
	return dk && bk && (downed || inBed)
}
