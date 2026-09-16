package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rangedRequest(t *testing.T) RangedDefenseRequest {
	t.Helper()
	draft, _ := domain.NewOwnedDraft("pawn")
	da, _ := domain.NewOwnedDraftAction("draft", draft)
	m, _ := domain.NewRangedAttack("pawn", "target", da.ID())
	a, _ := domain.NewRangedAttackAction("attack", m)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{da, a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
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
	return RangedDefenseRequest{Action: a, Progress: p, DraftProgress: d, Current: s, MinimumTick: 11, Facts: RangedDefenseFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Emergency: e, NativeCanTry: domain.Known(true), Pawn: RangedPawnFacts{Pawn: "pawn", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false), HealthFraction: domain.Known(1.0), FreeColonist: domain.Known(true), Drafted: domain.Known(true), ViolenceCapable: domain.Known(true), RangedWeaponEquipped: domain.Known(true), Owner: domain.Known(MeleeDraftOwner{Claim: "claim", Session: "session", Direction: 1})}, Target: RangedTargetFacts{Pawn: "target", SnapshotToken: "target-cas", Dead: domain.Known(false), Downed: domain.Known(false), Hostile: domain.Known(true)}}}
}

func TestRangedDefenseAdmission(t *testing.T) {
	r := rangedRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateRangedDefense(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestRangedDefenseRequiresRangedWeapon(t *testing.T) {
	r := rangedRequest(t)
	r.Facts.Pawn.RangedWeaponEquipped = domain.Known(false)
	d := EvaluateRangedDefense(r)
	if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != UnsuitableEquipment {
		t.Fatal(d)
	}
}

func TestRangedDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*RangedDefenseRequest)
	}{
		{"zero action", func(r *RangedDefenseRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *RangedDefenseRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *RangedDefenseRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *RangedDefenseRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *RangedDefenseRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *RangedDefenseRequest) { r.Facts.PreviewTick = 11 }},
		{"old emergency", func(r *RangedDefenseRequest) { r.Facts.Emergency.tick = 12 }},
		{"zero generation", func(r *RangedDefenseRequest) { r.Current.Native = 0 }},
		{"native", func(r *RangedDefenseRequest) { r.Current.Native++ }},
		{"direction", func(r *RangedDefenseRequest) { r.Current.Direction++ }},
		{"colony", func(r *RangedDefenseRequest) { r.Current.Colony = "other" }},
		{"load", func(r *RangedDefenseRequest) { r.Current.Load = "other" }},
		{"map", func(r *RangedDefenseRequest) { r.Current.Map++ }},
		{"plan", func(r *RangedDefenseRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *RangedDefenseRequest) { r.Current.Revision++ }},
		{"draft missing", func(r *RangedDefenseRequest) { r.DraftProgress = domain.Progress{} }},
		{"claim unknown", func(r *RangedDefenseRequest) { r.Facts.Pawn.Owner = domain.Unknown[MeleeDraftOwner]() }},
		{"pawn CAS", func(r *RangedDefenseRequest) { r.Facts.Pawn.SnapshotToken = "" }},
		{"target CAS", func(r *RangedDefenseRequest) { r.Facts.Target.SnapshotToken = "" }},
		{"wrong pawn", func(r *RangedDefenseRequest) { r.Facts.Pawn.Pawn = "other" }},
		{"wrong target", func(r *RangedDefenseRequest) { r.Facts.Target.Pawn = "other" }},
		{"bleeding", func(r *RangedDefenseRequest) { r.Facts.Pawn.Bleeding = domain.Known(true) }},
		{"health unknown", func(r *RangedDefenseRequest) { r.Facts.Pawn.HealthFraction = domain.Unknown[float64]() }},
		{"health threshold", func(r *RangedDefenseRequest) { r.Facts.Pawn.HealthFraction = domain.Known(float64(float32(0.5005))) }},
		{"health NaN", func(r *RangedDefenseRequest) { r.Facts.Pawn.HealthFraction = domain.Known(math.NaN()) }},
		{"violence incapable", func(r *RangedDefenseRequest) { r.Facts.Pawn.ViolenceCapable = domain.Known(false) }},
		{"ranged unknown", func(r *RangedDefenseRequest) { r.Facts.Pawn.RangedWeaponEquipped = domain.Unknown[bool]() }},
		{"not free", func(r *RangedDefenseRequest) { r.Facts.Pawn.FreeColonist = domain.Known(false) }},
		{"undrafted", func(r *RangedDefenseRequest) { r.Facts.Pawn.Drafted = domain.Known(false) }},
		{"target downed", func(r *RangedDefenseRequest) { r.Facts.Target.Downed = domain.Known(true) }},
		{"target neutral", func(r *RangedDefenseRequest) { r.Facts.Target.Hostile = domain.Known(false) }},
		{"preview refusal", func(r *RangedDefenseRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"census incomplete", func(r *RangedDefenseRequest) { r.Facts.Emergency.facts.ThreatsComplete = domain.Known(false) }},
		{"no opponent", func(r *RangedDefenseRequest) { r.Facts.Emergency.facts.Threats = nil }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := rangedRequest(t)
			c.change(&r)
			d := EvaluateRangedDefense(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}
