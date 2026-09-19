package policy

import (
	"math"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func meleeRequest(t *testing.T) MeleeDefenseRequest {
	t.Helper()
	draft, _ := domain.NewOwnedDraft("pawn")
	da, _ := domain.NewOwnedDraftAction("draft", draft)
	m, _ := domain.NewMeleeAttack("pawn", "target", da.ID())
	a, _ := domain.NewMeleeAttackAction("attack", m)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{da, a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	d, _ := domain.NewProgress(plan, da.ID())
	d, err = d.Prepare(s, 10)
	if err != nil {
		t.Fatal(err)
	}
	d, err = d.MarkDispatched(s, 10)
	if err != nil {
		t.Fatal(err)
	}
	claim := domain.DraftClaim{Action: da.ID(), Attempt: 1, Pawn: "pawn", Claim: "claim", Session: "session", Origin: s}
	d, err = d.RecordDraftReceipt(1, domain.ReceiptAccepted, domain.Known(claim))
	if err != nil {
		t.Fatal(err)
	}
	d, err = d.ObserveDraft(domain.Observation{Action: da.ID(), Attempt: 1, Snapshot: s, Tick: 11, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, s, domain.Known(claim))
	if err != nil {
		t.Fatal(err)
	}
	p, _ := domain.NewProgress(plan, a.ID())
	e, err := NewEmergencySnapshot(s, 13, EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}, Threats: []EmergencyThreat{{ID: "target", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}})
	if err != nil {
		t.Fatal(err)
	}
	return MeleeDefenseRequest{Action: a, Progress: p, DraftProgress: d, Current: s, MinimumTick: 11, Facts: MeleeDefenseFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Emergency: e, NativeCanTry: domain.Known(true), Pawn: MeleePawnFacts{Pawn: "pawn", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false), HealthFraction: domain.Known(1.0), FreeColonist: domain.Known(true), Drafted: domain.Known(true), ViolenceCapable: domain.Known(true), EquipmentKnown: domain.Known(true), Owner: domain.Known(MeleeDraftOwner{Claim: "claim", Session: "session"})}, Target: MeleeTargetFacts{Pawn: "target", SnapshotToken: "target-cas", Dead: domain.Known(false), Downed: domain.Known(false), Hostile: domain.Known(true)}}}
}

func TestMeleeDefenseAdmission(t *testing.T) {
	r := meleeRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateMeleeDefense(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
	// Cross-category copies describe one opponent, not two; no weapon-class gate.
	r.Facts.Emergency.facts.Threats = append(r.Facts.Emergency.facts.Threats, EmergencyThreat{ID: "target", Kind: NearbyPredator, Dead: domain.Known(false), Downed: domain.Known(false)})
	if !EvaluateMeleeDefense(r).Admitted {
		t.Fatal("duplicate opponent refused")
	}
	r.Facts.Pawn.HealthFraction = domain.Known(math.Nextafter(float64(float32(0.5005)), 1))
	if !EvaluateMeleeDefense(r).Admitted {
		t.Fatal("healthy boundary refused")
	}
}

// Under a running combat window the executor's second inspection may read
// the pawn from the step's fact cache, up to PlanningTickTolerance behind
// the preview that anchors the admission (#244, #246, #306): the cached
// read is fresh evidence for the preview and the attack goes on to
// dispatch; a pawn row the preview has outrun by more is stale, as is a
// preview behind the minimum tick.
func TestMeleeDefenseToleratesACachedPawnReadBehindTheAdmittedTick(t *testing.T) {
	r := meleeRequest(t)
	var err error
	r.Progress, err = r.Progress.Prepare(r.Current, 12+domain.PlanningTickTolerance)
	if err != nil {
		t.Fatal(err)
	}
	r.MinimumTick = 12 + domain.PlanningTickTolerance
	r.Facts.PreviewTick = 12 + domain.PlanningTickTolerance
	r.Facts.Emergency.tick = r.Facts.PreviewTick
	if d := EvaluateMeleeDefense(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatalf("cached pawn read within tolerance refused: %v", d)
	}
	r.MinimumTick++
	if d := EvaluateMeleeDefense(r); d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != StaleFacts {
		t.Fatalf("preview behind the minimum admitted: %v", d)
	}
	r.MinimumTick--
	r.Facts.PreviewTick++
	r.Facts.Emergency.tick = r.Facts.PreviewTick
	if d := EvaluateMeleeDefense(r); d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != StaleFacts {
		t.Fatalf("pawn read beyond tolerance admitted: %v", d)
	}
}

// A hostile building target (#246) is admitted on its census row alone:
// standing, never downed, hostile by faction; once the census drops it, the
// target is refused as an unsupported threat.
func TestMeleeDefenseAdmitsAHostileBuildingTarget(t *testing.T) {
	r := meleeRequest(t)
	r.Facts.Emergency.facts.Threats = []EmergencyThreat{{ID: "target", Kind: HostileBuilding, Dead: domain.Known(false), Downed: domain.Known(false), Animal: domain.Known(false), SnapshotToken: "hive-cas", Definition: "Hive", Cells: []domain.Cell{{X: 5, Z: 5}}}}
	r.Facts.Target.SnapshotToken = "hive-cas"
	if d := EvaluateMeleeDefense(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
	}
	r.Facts.Emergency.facts.Threats = nil
	r.Facts.Target.Dead, r.Facts.Target.Hostile = domain.Known(true), domain.Known(false)
	if d := EvaluateMeleeDefense(r); d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != UnsupportedThreat {
		t.Fatal(d)
	}
}

func TestMeleeDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*MeleeDefenseRequest)
	}{
		{"zero action", func(r *MeleeDefenseRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *MeleeDefenseRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *MeleeDefenseRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *MeleeDefenseRequest) { r.MinimumTick = 12 + domain.PlanningTickTolerance + 1 }},
		{"negative minimum", func(r *MeleeDefenseRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *MeleeDefenseRequest) { r.Facts.PreviewTick = 11 }},
		{"old emergency", func(r *MeleeDefenseRequest) { r.Facts.Emergency.tick = 12 }},
		{"zero generation", func(r *MeleeDefenseRequest) { r.Current.Native = 0 }},
		{"native", func(r *MeleeDefenseRequest) { r.Current.Native++ }},
		{"colony", func(r *MeleeDefenseRequest) { r.Current.Colony = "other" }},
		{"load", func(r *MeleeDefenseRequest) { r.Current.Load = "other" }},
		{"map", func(r *MeleeDefenseRequest) { r.Current.Map++ }},
		{"plan", func(r *MeleeDefenseRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *MeleeDefenseRequest) { r.Current.Revision++ }},
		{"draft missing", func(r *MeleeDefenseRequest) { r.DraftProgress = domain.Progress{} }},
		{"claim unknown", func(r *MeleeDefenseRequest) { r.Facts.Pawn.Owner = domain.Unknown[MeleeDraftOwner]() }},
		{"claim replaced", func(r *MeleeDefenseRequest) {
			r.Facts.Pawn.Owner = domain.Known(MeleeDraftOwner{Claim: "other", Session: "session"})
		}},
		{"foreign session", func(r *MeleeDefenseRequest) {
			r.Facts.Pawn.Owner = domain.Known(MeleeDraftOwner{Claim: "claim", Session: "other"})
		}},
		{"pawn CAS", func(r *MeleeDefenseRequest) { r.Facts.Pawn.SnapshotToken = "" }},
		{"target CAS", func(r *MeleeDefenseRequest) { r.Facts.Target.SnapshotToken = strings.Repeat("x", 257) }},
		{"wrong pawn", func(r *MeleeDefenseRequest) { r.Facts.Pawn.Pawn = "other" }},
		{"wrong target", func(r *MeleeDefenseRequest) { r.Facts.Target.Pawn = "other" }},
		{"bleeding", func(r *MeleeDefenseRequest) { r.Facts.Pawn.Bleeding = domain.Known(true) }},
		{"health unknown", func(r *MeleeDefenseRequest) { r.Facts.Pawn.HealthFraction = domain.Unknown[float64]() }},
		{"health threshold", func(r *MeleeDefenseRequest) { r.Facts.Pawn.HealthFraction = domain.Known(float64(float32(0.5005))) }},
		{"health NaN", func(r *MeleeDefenseRequest) { r.Facts.Pawn.HealthFraction = domain.Known(math.NaN()) }},
		{"health infinity", func(r *MeleeDefenseRequest) { r.Facts.Pawn.HealthFraction = domain.Known(math.Inf(1)) }},
		{"health overflow", func(r *MeleeDefenseRequest) { r.Facts.Pawn.HealthFraction = domain.Known(1.1) }},
		{"violence incapable", func(r *MeleeDefenseRequest) { r.Facts.Pawn.ViolenceCapable = domain.Known(false) }},
		{"equipment unknown", func(r *MeleeDefenseRequest) { r.Facts.Pawn.EquipmentKnown = domain.Unknown[bool]() }},
		{"equipment incomplete", func(r *MeleeDefenseRequest) { r.Facts.Pawn.EquipmentKnown = domain.Known(false) }},
		{"not free", func(r *MeleeDefenseRequest) { r.Facts.Pawn.FreeColonist = domain.Known(false) }},
		{"undrafted", func(r *MeleeDefenseRequest) { r.Facts.Pawn.Drafted = domain.Known(false) }},
		{"target downed", func(r *MeleeDefenseRequest) { r.Facts.Target.Downed = domain.Known(true) }},
		{"target neutral", func(r *MeleeDefenseRequest) { r.Facts.Target.Hostile = domain.Known(false) }},
		{"preview refusal", func(r *MeleeDefenseRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"census incomplete", func(r *MeleeDefenseRequest) { r.Facts.Emergency.facts.ThreatsComplete = domain.Known(false) }},
		{"missing attacker census", func(r *MeleeDefenseRequest) { r.Facts.Emergency.facts.Colonists = nil }},
		{"no opponent", func(r *MeleeDefenseRequest) { r.Facts.Emergency.facts.Threats = nil }},
		{"unsupported opponent", func(r *MeleeDefenseRequest) { r.Facts.Emergency.facts.Threats[0].Kind = HuntingPredator }},
		{"other medical", func(r *MeleeDefenseRequest) {
			r.Facts.Emergency.facts.Colonists = append(r.Facts.Emergency.facts.Colonists, EmergencyPawn{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(true), NeedsTend: domain.Known(false)})
		}},
		{"second opponent", func(r *MeleeDefenseRequest) {
			r.Facts.Emergency.facts.Threats = append(r.Facts.Emergency.facts.Threats, EmergencyThreat{ID: "other", Kind: NearbyPredator, Dead: domain.Known(false), Downed: domain.Known(false)})
		}},
		{"contradictory opponent", func(r *MeleeDefenseRequest) {
			r.Facts.Emergency.facts.Threats = append(r.Facts.Emergency.facts.Threats, EmergencyThreat{ID: "target", Kind: NearbyDowned, Dead: domain.Known(false), Downed: domain.Known(true)})
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := meleeRequest(t)
			c.change(&r)
			d := EvaluateMeleeDefense(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}
