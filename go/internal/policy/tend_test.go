package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func tendRequest(t *testing.T) TendRequest {
	t.Helper()
	tend, _ := domain.NewTend("doctor", "patient")
	a, _ := domain.NewTendAction("tend", tend)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	e, err := NewEmergencySnapshot(s, 13, EmergencyFacts{
		ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true),
		Colonists: []EmergencyPawn{
			{ID: "doctor", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
			{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(true), NeedsTend: domain.Known(true)},
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	doctor := TendDoctorFacts{Pawn: "doctor", SnapshotToken: "doctor-cas", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known(""), MedicineSkill: domain.Known(int32(8)), MedicineSkillDisabled: domain.Known(false), DoctorWorkEnabled: domain.Known(true), DoctorWorkOverrideDisabled: domain.Known(false), ControlEligible: domain.Known(true), TendCapacities: domain.Known(true), ReachesPatient: domain.Known(true)}
	patient := TendPatientFacts{Pawn: "patient", SnapshotToken: "patient-cas", Dead: domain.Known(false), Downed: domain.Known(false), InBed: domain.Known(true), NeedsTend: domain.Known(true), NoCare: domain.Known(false), Bleeding: domain.Known(true), LifeThreatening: domain.Known(false), HoursUntilDeathFromBloodLoss: domain.Known(6.0), ExistingJobDef: domain.Known("")}
	return TendRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: TendFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Emergency: e, NativeCanTry: domain.Known(true), Doctor: doctor, Patient: patient}}
}

func TestTendAdmission(t *testing.T) {
	r := tendRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateTend(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestTendDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*TendRequest)
	}{
		{"zero action", func(r *TendRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *TendRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *TendRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *TendRequest) { r.MinimumTick = 14 }},
		{"negative minimum", func(r *TendRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *TendRequest) { r.Facts.PreviewTick = 11 }},
		{"pawn row outrun", func(r *TendRequest) { r.Facts.PreviewTick = 12 + domain.PlanningTickTolerance + 1 }},
		{"prepared past preview", func(r *TendRequest) { r.Progress, _ = r.Progress.Prepare(r.Current, 14) }},
		{"old emergency", func(r *TendRequest) { r.Facts.Emergency.tick = 12 }},
		{"zero generation", func(r *TendRequest) { r.Current.Native = 0 }},
		{"native", func(r *TendRequest) { r.Current.Native++ }},
		{"colony", func(r *TendRequest) { r.Current.Colony = "other" }},
		{"load", func(r *TendRequest) { r.Current.Load = "other" }},
		{"map", func(r *TendRequest) { r.Current.Map++ }},
		{"plan", func(r *TendRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *TendRequest) { r.Current.Revision++ }},
		{"doctor CAS", func(r *TendRequest) { r.Facts.Doctor.SnapshotToken = "" }},
		{"patient CAS", func(r *TendRequest) { r.Facts.Patient.SnapshotToken = "" }},
		{"wrong doctor", func(r *TendRequest) { r.Facts.Doctor.Pawn = "other" }},
		{"wrong patient", func(r *TendRequest) { r.Facts.Patient.Pawn = "other" }},
		{"doctor dead", func(r *TendRequest) { r.Facts.Doctor.Dead = domain.Known(true) }},
		{"doctor downed", func(r *TendRequest) { r.Facts.Doctor.Downed = domain.Known(true) }},
		{"doctor drafted unknown", func(r *TendRequest) { r.Facts.Doctor.Drafted = domain.Unknown[bool]() }},
		{"doctor mental state", func(r *TendRequest) { r.Facts.Doctor.MentalState = domain.Known(true) }},
		{"doctor already tending", func(r *TendRequest) { r.Facts.Doctor.ExistingJobDef = domain.Known("TendPatient") }},
		{"doctor unknown skill", func(r *TendRequest) { r.Facts.Doctor.MedicineSkill = domain.Unknown[int32]() }},
		{"doctor skill disabled", func(r *TendRequest) { r.Facts.Doctor.MedicineSkillDisabled = domain.Known(true) }},
		{"doctor work disabled", func(r *TendRequest) { r.Facts.Doctor.DoctorWorkEnabled = domain.Known(false) }},
		{"doctor work override zero", func(r *TendRequest) { r.Facts.Doctor.DoctorWorkOverrideDisabled = domain.Known(true) }},
		{"patient dead", func(r *TendRequest) { r.Facts.Patient.Dead = domain.Known(true) }},
		{"patient recovered", func(r *TendRequest) { r.Facts.Patient.NeedsTend = domain.Known(false) }},
		{"patient unknown needs tend", func(r *TendRequest) { r.Facts.Patient.NeedsTend = domain.Unknown[bool]() }},
		{"patient no care", func(r *TendRequest) { r.Facts.Patient.NoCare = domain.Known(true) }},
		{"patient already tended", func(r *TendRequest) { r.Facts.Patient.ExistingJobDef = domain.Known("TendPatient") }},
		{"preview refusal", func(r *TendRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"census incomplete", func(r *TendRequest) { r.Facts.Emergency.facts.ColonistsComplete = domain.Known(false) }},
		{"unsafe threat", func(r *TendRequest) {
			r.Facts.Emergency.facts.Threats = append(r.Facts.Emergency.facts.Threats, EmergencyThreat{ID: "raider", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)})
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := tendRequest(t)
			c.change(&r)
			d := EvaluateTend(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

// The patient's own untended/bleeding state must never refuse its own tend
// action; that is precisely the emergency this action resolves.
func TestTendIgnoresItsOwnCriticalMedicalHold(t *testing.T) {
	r := tendRequest(t)
	if d := EvaluateTend(r); !d.Admitted {
		t.Fatal("tend refused by the emergency it exists to clear", d)
	}
}

// A drafted doctor is the controller's own fallback for the deadlock the
// issue describes: every doctor drafted (e.g. for defense) while a hostile
// remains anywhere on the map. Native's own job-acceptance check (NativeCanTry)
// stays the live safety authority; RimGovernor's colony-wide hostile gate does
// not block this doctor the way it still blocks an undrafted one.
func TestTendAdmitsDraftedDoctorDespiteHostile(t *testing.T) {
	r := tendRequest(t)
	r.Facts.Doctor.Drafted = domain.Known(true)
	r.Facts.Emergency.facts.Threats = append(r.Facts.Emergency.facts.Threats, EmergencyThreat{ID: "raider", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)})
	if d := EvaluateTend(r); !d.Admitted {
		t.Fatal("drafted doctor refused despite controller-owned fallback", d)
	}
}

// An undrafted doctor still cannot tend while a hostile remains -- only the
// drafted fallback exempts the colony-wide hostile gate.
func TestTendStillBlocksUndraftedDoctorDuringHostile(t *testing.T) {
	r := tendRequest(t)
	r.Facts.Emergency.facts.Threats = append(r.Facts.Emergency.facts.Threats, EmergencyThreat{ID: "raider", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)})
	d := EvaluateTend(r)
	if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != UnsupportedThreat {
		t.Fatal(d)
	}
}

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

// TestTendAdmitsCachedPawnRowBehindPreparedTick is the #306 shape (#323):
// the executor's second inspection under a running window is served the
// pawn row the first read (tick 12) cached, while the first preview (tick
// 13) prepared the action and raised the minimum; the second preview (tick
// 15) anchors the admission, so the older row is fresh evidence.
func TestTendAdmitsCachedPawnRowBehindPreparedTick(t *testing.T) {
	r := tendRequest(t)
	var err error
	if r.Progress, err = r.Progress.Prepare(r.Current, 13); err != nil {
		t.Fatal(err)
	}
	r.MinimumTick, r.Facts.PawnTick, r.Facts.PreviewTick = 13, 12, 15
	r.Facts.Emergency.tick = r.Facts.PreviewTick
	if d := EvaluateTend(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
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
// and ground tending needs a downed patient", #618): selection skips them and
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
	r := tendRequest(t)
	r.Facts.Patient.InBed = domain.Known(false)
	if d := EvaluateTend(r); d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != PatientIneligible {
		t.Fatal(d)
	}
	r.Facts.Patient.Downed = domain.Known(true)
	if d := EvaluateTend(r); !d.Admitted {
		t.Fatal(d)
	}
	r.Facts.Patient.Downed, r.Facts.Patient.InBed = domain.Known(false), domain.Unknown[bool]()
	if d := EvaluateTend(r); d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != UnknownFacts {
		t.Fatal(d)
	}
}

// Each native doctor gate #657 added: pawn-control eligibility, WorkGiver_Tend's
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

func TestTendAdmissionRefusesNativeDoctorGates(t *testing.T) {
	for _, c := range []struct {
		name   string
		change func(*TendDoctorFacts)
		reason Reason
	}{
		{"control ineligible", func(d *TendDoctorFacts) { d.ControlEligible = domain.Known(false) }, DoctorUnavailable},
		{"control unknown", func(d *TendDoctorFacts) { d.ControlEligible = domain.Unknown[bool]() }, UnknownFacts},
		{"capacity missing", func(d *TendDoctorFacts) { d.TendCapacities = domain.Known(false) }, DoctorUnavailable},
		{"capacity unknown", func(d *TendDoctorFacts) { d.TendCapacities = domain.Unknown[bool]() }, UnknownFacts},
		{"unreachable", func(d *TendDoctorFacts) { d.ReachesPatient = domain.Known(false) }, DoctorUnavailable},
		{"reachability unknown", func(d *TendDoctorFacts) { d.ReachesPatient = domain.Unknown[bool]() }, UnknownFacts},
	} {
		t.Run(c.name, func(t *testing.T) {
			r := tendRequest(t)
			c.change(&r.Facts.Doctor)
			d := EvaluateTend(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != c.reason {
				t.Fatal(d)
			}
		})
	}
}
