package startuplabor

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// held builds a Pending progress view holding the given reasons, the way
// domain.Progress.Hold records them.
func held(t *testing.T, reasons ...domain.HeldReason) *domain.ProgressView {
	t.Helper()
	intent, err := domain.NewClean("pawn", "filth", domain.Cell{X: 1, Z: 1})
	if err != nil {
		t.Fatalf("intent: %v", err)
	}
	action, err := domain.NewCleanAction("act-1", intent)
	if err != nil {
		t.Fatalf("action: %v", err)
	}
	plan, err := domain.NewPlan("plan-1", 1, []domain.Action{action})
	if err != nil {
		t.Fatalf("plan: %v", err)
	}
	p, err := domain.NewProgress(plan, "act-1")
	if err != nil {
		t.Fatalf("progress: %v", err)
	}
	p, err = p.Hold(reasons, 10)
	if err != nil {
		t.Fatalf("hold: %v", err)
	}
	v := p.View()
	return &v
}

func pending() *domain.ProgressView {
	return &domain.ProgressView{Stage: domain.Pending, Tick: 10}
}

func TestDiagnoseDistinguishesBlockers(t *testing.T) {
	for _, tc := range []struct {
		name    string
		subject Subject
		want    Class
		blocker domain.HeldReason
		reason  policy.DevelopmentReason
	}{
		{
			name:    "slot refusal",
			subject: Subject{Slot: &Slot{Reason: policy.DevelopmentCapacity}},
			want:    ClassSlotRefusal, reason: policy.DevelopmentCapacity,
		},
		{
			name:    "worker refusal is not an ordinary slot refusal",
			subject: Subject{Slot: &Slot{Reason: policy.DevelopmentNoWorkers}},
			want:    ClassWorkerBlocker, reason: policy.DevelopmentNoWorkers,
		},
		{
			name:    "material blocker",
			subject: Subject{Slot: &Slot{Selected: true}, Progress: held(t, domain.HeldInsufficientStock)},
			want:    ClassMaterialBlocker, blocker: domain.HeldInsufficientStock,
		},
		{
			name:    "worker blocker",
			subject: Subject{Slot: &Slot{Selected: true}, Progress: held(t, domain.HeldHaulerUnavailable)},
			want:    ClassWorkerBlocker, blocker: domain.HeldHaulerUnavailable,
		},
		{
			name: "material outranks worker when both are held",
			subject: Subject{Slot: &Slot{Selected: true},
				Progress: held(t, domain.HeldHaulerUnavailable, domain.HeldMaterialRequired)},
			want: ClassMaterialBlocker, blocker: domain.HeldMaterialRequired,
		},
		{
			name: "unresolved action",
			subject: Subject{Slot: &Slot{Selected: true, Committed: true},
				Progress: &domain.ProgressView{Stage: domain.Dispatched, Unresolved: true}},
			want: ClassUnresolvedAction,
		},
		{
			name: "normal non-work activity",
			subject: Subject{Slot: &Slot{Selected: true}, Progress: pending(),
				Activity: []Activity{ActivitySleep, ActivityMeal}},
			want: ClassNonWorkActivity,
		},
		{
			name: "a working pawn is progress, not a blocker",
			subject: Subject{Slot: &Slot{Selected: true}, Progress: pending(),
				Activity: []Activity{ActivitySleep, ActivityWork}},
			want: ClassProgressing,
		},
		{
			name:    "a dispatched, resolved action is progressing",
			subject: Subject{Slot: &Slot{Selected: true, Committed: true}, Progress: &domain.ProgressView{Stage: domain.Dispatched}},
			want:    ClassProgressing,
		},
	} {
		t.Run(tc.name, func(t *testing.T) {
			d := Diagnose(tc.subject)
			if d.Class != tc.want {
				t.Fatalf("class = %q, want %q (missing %v)", d.Class, tc.want, d.Missing)
			}
			if d.Blocker != tc.blocker {
				t.Fatalf("blocker = %q, want %q", d.Blocker, tc.blocker)
			}
			if d.Reason != tc.reason {
				t.Fatalf("reason = %q, want %q", d.Reason, tc.reason)
			}
			if len(d.Missing) != 0 {
				t.Fatalf("missing = %v, want none", d.Missing)
			}
		})
	}
}

func TestDiagnoseNeverInfersMissingFacts(t *testing.T) {
	for _, tc := range []struct {
		name    string
		subject Subject
		missing []string
	}{
		{name: "no evidence at all", subject: Subject{}, missing: []string{"development_row", "progress"}},
		{
			name:    "refused with no reason recorded",
			subject: Subject{Slot: &Slot{}},
			missing: []string{"development_reason"},
		},
		{
			name:    "selected and unheld with no pawn sample",
			subject: Subject{Slot: &Slot{Selected: true}, Progress: pending()},
			missing: []string{"pawn_activity"},
		},
		{
			name: "a sample with no readable job",
			subject: Subject{Slot: &Slot{Selected: true}, Progress: pending(),
				Activity: []Activity{ActivityUnknown}},
			missing: []string{"pawn_activity"},
		},
		{
			name: "an idle pawn beside an unblocked goal names no blocker",
			subject: Subject{Slot: &Slot{Selected: true}, Progress: pending(),
				Activity: []Activity{ActivityIdle}},
			missing: []string{"blocker"},
		},
		{
			name:    "a hold in neither class is reported, not guessed",
			subject: Subject{Slot: &Slot{Selected: true}, Progress: held(t, domain.HeldUnsafeThreat)},
			missing: []string{"hold_reason_class"},
		},
	} {
		t.Run(tc.name, func(t *testing.T) {
			d := Diagnose(tc.subject)
			if d.Class != ClassUnknown {
				t.Fatalf("class = %q, want %q", d.Class, ClassUnknown)
			}
			if !reflect.DeepEqual(d.Missing, tc.missing) {
				t.Fatalf("missing = %v, want %v", d.Missing, tc.missing)
			}
		})
	}
}

func TestDiagnosisRowOmitsUnobservedFacts(t *testing.T) {
	d := Diagnose(Subject{
		World: World{Colony: "c", Load: "l", Map: 3}, ReviewTick: 42,
		Goal: "routine-shelter", Method: "shelter-beds", Action: "act-1",
		Slot: &Slot{Selected: true}, Progress: held(t, domain.HeldInsufficientStock),
		ShelterBeds: domain.Known(true),
	})
	row := d.Row()
	if row["class"] != ClassMaterialBlocker || row["blocker"] != domain.HeldInsufficientStock {
		t.Fatalf("row = %v", row)
	}
	if row["shelter_beds"] != true || row["review_tick"] != domain.Tick(42) {
		t.Fatalf("row = %v", row)
	}
	for _, key := range []string{"reason", "bottleneck", "missing", "activities", "unresolved"} {
		if _, present := row[key]; present {
			t.Fatalf("row carries unobserved %q: %v", key, row)
		}
	}
	// An unknown shelter-beds fact is absent from the row, not false.
	if _, present := Diagnose(Subject{Slot: &Slot{Reason: policy.DevelopmentCapacity}}).Row()["shelter_beds"]; present {
		t.Fatal("unknown shelter-beds fact rendered as a value")
	}
}
