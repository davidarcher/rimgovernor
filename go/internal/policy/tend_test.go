package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func tendDoctor(id domain.PawnID, skill int32) TendDoctorFacts {
	return TendDoctorFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known(""), MedicineSkill: domain.Known(skill), MedicineSkillDisabled: domain.Known(false), DoctorWorkEnabled: domain.Known(true), DoctorWorkOverrideDisabled: domain.Known(false), ControlEligible: domain.Known(true), TendCapacities: domain.Known(true)}
}
func tendPatient(id domain.PawnID, hours float64) TendPatientFacts {
	return TendPatientFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(false), InBed: domain.Known(true), NeedsTend: domain.Known(true), NoCare: domain.Known(false), Bleeding: domain.Known(true), LifeThreatening: domain.Known(false), HoursUntilDeathFromBloodLoss: domain.Known(hours), ExistingJobDef: domain.Known("")}
}

// selectTend runs the selection with every candidate pair reachable, so a test
// exercising ranking or eligibility is not also asserting reachability.
func selectTend(doctors []TendDoctorFacts, patients []TendPatientFacts) (domain.PawnID, domain.PawnID, bool) {
	reach := map[domain.PawnID][]domain.PawnID{}
	for _, d := range doctors {
		ids := make([]domain.PawnID, 0, len(patients))
		for _, p := range patients {
			ids = append(ids, p.Pawn)
		}
		reach[d.Pawn] = ids
	}
	return SelectTend(doctors, patients, ObserveTendReachability(reach))
}

func TestSelectTendRanksBySkillAndUrgency(t *testing.T) {
	doctors := []TendDoctorFacts{tendDoctor("junior", 4), tendDoctor("senior", 12)}
	patients := []TendPatientFacts{tendPatient("stable", 40), tendPatient("critical", 2)}
	doctor, patient, ok := selectTend(doctors, patients)
	if !ok || doctor != "senior" || patient != "critical" {
		t.Fatal(doctor, patient, ok)
	}
}
func TestSelectTendExcludesIneligibleCandidates(t *testing.T) {
	busy := tendDoctor("busy", 10)
	busy.ExistingJobDef = domain.Known("TendPatient")
	drafted := tendDoctor("drafted", 10)
	drafted.Drafted = domain.Known(true)
	fine := tendDoctor("fine", 6)
	recovered := tendPatient("recovered", 10)
	recovered.NeedsTend = domain.Known(false)
	noCare := tendPatient("nocare", 10)
	noCare.NoCare = domain.Known(true)
	sick := tendPatient("sick", 10)
	doctor, patient, ok := selectTend([]TendDoctorFacts{busy, drafted, fine}, []TendPatientFacts{recovered, noCare, sick})
	if !ok || doctor != "fine" || patient != "sick" {
		t.Fatal(doctor, patient, ok)
	}
	if _, _, ok := selectTend([]TendDoctorFacts{fine}, []TendPatientFacts{recovered, noCare}); ok {
		t.Fatal("selected an ineligible patient")
	}
}

// With no undrafted doctor eligible at all, SelectTend falls back to a
// drafted one rather than leaving the patient untreated.
func TestSelectTendFallsBackToDraftedDoctor(t *testing.T) {
	busy := tendDoctor("busy", 10)
	busy.ExistingJobDef = domain.Known("TendPatient")
	drafted := tendDoctor("drafted", 10)
	drafted.Drafted = domain.Known(true)
	sick := tendPatient("sick", 10)
	doctor, patient, ok := selectTend([]TendDoctorFacts{busy, drafted}, []TendPatientFacts{sick})
	if !ok || doctor != "drafted" || patient != "sick" {
		t.Fatal(doctor, patient, ok)
	}
	if _, _, ok := selectTend([]TendDoctorFacts{busy}, []TendPatientFacts{sick}); ok {
		t.Fatal("selected an ineligible doctor")
	}
}

// A mildly ill pawn (not dead, downed or mentally broken) is nominally an
// eligible doctor by TendDoctorFacts alone, but must never be selected as its
// own doctor: domain.NewTend refuses equal doctor/patient identities, and
// live acceptance evidence (three colonists all mildly ill with equal
// Medicine skill, so the same lowest-ID pawn topped both pools) showed this
// stalling the routine clock step on repeat rather than refusing cleanly.
func TestSelectTendNeverPicksAPatientAsItsOwnDoctor(t *testing.T) {
	self := tendDoctor("self", 10)
	other := tendDoctor("other", 10)
	selfAsPatient := tendPatient("self", 10)
	doctor, patient, ok := selectTend([]TendDoctorFacts{self, other}, []TendPatientFacts{selfAsPatient})
	if !ok || doctor != "other" || patient != "self" {
		t.Fatal(doctor, patient, ok)
	}
	if _, _, ok := selectTend([]TendDoctorFacts{self}, []TendPatientFacts{selfAsPatient}); ok {
		t.Fatal("selected a pawn as its own doctor")
	}
}

func TestSelectTendUsesForcedAndQueuedDoctorAsFallback(t *testing.T) {
	forced, idle := tendDoctor("a", 12), tendDoctor("z", 8)
	forced.PlayerForced, forced.QueuedJobs = domain.Known(true), domain.Known(uint32(2))
	patients := []TendPatientFacts{tendPatient("patient", 2)}
	if pawn, _, ok := selectTend([]TendDoctorFacts{forced, idle}, patients); !ok || pawn != idle.Pawn {
		t.Fatal(pawn, ok)
	}
	if pawn, _, ok := selectTend([]TendDoctorFacts{forced}, patients); !ok || pawn != forced.Pawn {
		t.Fatal(pawn, ok)
	}
	forced.Drafted = domain.Known(true)
	if pawn, _, ok := selectTend([]TendDoctorFacts{forced}, patients); !ok || pawn != forced.Pawn {
		t.Fatal("drafted forced doctor excluded", pawn, ok)
	}
}

// An up patient out of bed is refused natively ("WorkGiver_Tend makes no job
// and ground tending needs a downed patient"): selection skips them and
// admission refuses them, while a downed or bedded patient still qualifies.
func TestTendSkipsUpPatientOutOfBed(t *testing.T) {
	up := tendPatient("up", 2)
	up.InBed = domain.Known(false)
	downed := tendPatient("downed", 40)
	downed.InBed, downed.Downed = domain.Known(false), domain.Known(true)
	unknownBed := tendPatient("unknown", 1)
	unknownBed.InBed = domain.Unknown[bool]()
	doctor, patient, ok := selectTend([]TendDoctorFacts{tendDoctor("doc", 5)}, []TendPatientFacts{up, unknownBed, downed})
	if !ok || doctor != "doc" || patient != "downed" {
		t.Fatal(doctor, patient, ok)
	}
	if _, _, ok := selectTend([]TendDoctorFacts{tendDoctor("doc", 5)}, []TendPatientFacts{up}); ok {
		t.Fatal("selected an up patient out of bed")
	}
}

// Native doctor eligibility gates: pawn-control eligibility, WorkGiver_Tend's
// required capacities and reachability. Selection must skip a doctor failing any
// of them -- and skip one whose fact is simply unobserved -- and admission must
// refuse the same pair rather than spend an attempt on an order native refuses.
func TestSelectTendHonoursNativeDoctorGates(t *testing.T) {
	patients := []TendPatientFacts{tendPatient("patient", 2)}
	for _, c := range []struct {
		name   string
		change func(*TendDoctorFacts)
	}{
		{"control ineligible", func(d *TendDoctorFacts) { d.ControlEligible = domain.Known(false) }},
		{"control unknown", func(d *TendDoctorFacts) { d.ControlEligible = domain.Unknown[bool]() }},
		{"capacity missing", func(d *TendDoctorFacts) { d.TendCapacities = domain.Known(false) }},
		{"capacity unknown", func(d *TendDoctorFacts) { d.TendCapacities = domain.Unknown[bool]() }},
	} {
		t.Run(c.name, func(t *testing.T) {
			refused, fine := tendDoctor("refused", 20), tendDoctor("fine", 1)
			c.change(&refused)
			if pawn, _, ok := selectTend([]TendDoctorFacts{refused, fine}, patients); !ok || pawn != fine.Pawn {
				t.Fatal("picked the doctor native would refuse", pawn, ok)
			}
			if _, _, ok := selectTend([]TendDoctorFacts{refused}, patients); ok {
				t.Fatal("proposed a pair the native gate refuses")
			}
		})
	}
}

// Reachability is the one pairwise gate: the pair is the first patient in
// urgency order some ranked doctor can actually walk to.
func TestSelectTendRequiresReachability(t *testing.T) {
	walled, near := tendDoctor("walled", 20), tendDoctor("near", 1)
	critical, stable := tendPatient("critical", 2), tendPatient("stable", 40)
	doctors := []TendDoctorFacts{walled, near}
	patients := []TendPatientFacts{critical, stable}
	// The best doctor reaches only the less urgent patient; the worse doctor
	// reaches the critical one, so that pair wins on urgency.
	reach := ObserveTendReachability(map[domain.PawnID][]domain.PawnID{
		"walled": {"stable"},
		"near":   {"critical", "stable"},
	})
	if doctor, patient, ok := SelectTend(doctors, patients, reach); !ok || doctor != "near" || patient != "critical" {
		t.Fatal(doctor, patient, ok)
	}
	// Nothing reachable, and an unobserved doctor, are both no pair.
	none := ObserveTendReachability(map[domain.PawnID][]domain.PawnID{"walled": nil, "near": nil})
	if _, _, ok := SelectTend(doctors, patients, none); ok {
		t.Fatal("proposed an unreachable pair")
	}
	if _, _, ok := SelectTend(doctors, patients, ObserveTendReachability(nil)); ok {
		t.Fatal("proposed a pair with no reachability fact at all")
	}
}

// The drafted fallback is reachability-aware: an undrafted doctor walled off
// from every patient must not shadow a drafted one who can reach them.
func TestSelectTendFallsBackToDraftedWhenUndraftedCannotReach(t *testing.T) {
	walled := tendDoctor("walled", 20)
	drafted := tendDoctor("drafted", 1)
	drafted.Drafted = domain.Known(true)
	patients := []TendPatientFacts{tendPatient("patient", 2)}
	reach := ObserveTendReachability(map[domain.PawnID][]domain.PawnID{"walled": nil, "drafted": {"patient"}})
	doctor, patient, ok := SelectTend([]TendDoctorFacts{walled, drafted}, patients, reach)
	if !ok || doctor != "drafted" || patient != "patient" {
		t.Fatal(doctor, patient, ok)
	}
}
