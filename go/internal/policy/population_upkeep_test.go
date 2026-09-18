package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func prisonerRow(id string, recruitable bool, current domain.PrisonerInteractionMode, resistance float64, heldDays float64) PrisonerFacts {
	return PrisonerFacts{
		Pawn: domain.PawnID(id), Dead: domain.Known(false), Prisoner: domain.Known(true),
		Recruitable: domain.Known(recruitable), CurrentInteraction: domain.Known(current),
		Resistance: domain.Known(resistance), HeldTicks: domain.Known(int64(heldDays * 60000)),
	}
}

func TestPrisonerRecruitOnlyWithoutReleasePolicy(t *testing.T) {
	rows := domain.Known([]PrisonerFacts{
		prisonerRow("p2", true, domain.PrisonerInteractionMaintain, 12, 30),
		prisonerRow("p1", false, domain.PrisonerInteractionMaintain, 12, 30),
	})
	food := domain.Known(1.0)
	if deficit, known := PrisonerRecruitDeficit(rows, food, PrisonerPolicy{}).Value(); !known || !deficit {
		t.Fatalf("expected recruit deficit, got %v %v", deficit, known)
	}
	choice := SelectPrisonerInteractionMethod(rows, food, PrisonerPolicy{})
	if choice.Reason != "" || choice.Pawn != "p2" || choice.Interaction != domain.PrisonerInteractionRecruit {
		t.Fatalf("unexpected choice %+v", choice)
	}
	// Facts the release path needs are irrelevant while it is off.
	bare := domain.Known([]PrisonerFacts{{Pawn: "p1", Dead: domain.Known(false), Recruitable: domain.Known(false), CurrentInteraction: domain.Known(domain.PrisonerInteractionMaintain)}})
	if deficit, known := PrisonerRecruitDeficit(bare, domain.Unknown[float64](), PrisonerPolicy{}).Value(); !known || deficit {
		t.Fatalf("expected settled, got %v %v", deficit, known)
	}
}

func TestPrisonerReleaseAfterDaysGates(t *testing.T) {
	p := PrisonerPolicy{ReleaseAfterDays: 10, FoodTargetDays: 7}
	cases := []struct {
		name string
		row  PrisonerFacts
		food domain.Fact[float64]
		want domain.PrisonerInteractionMode
	}{
		{"unbroken resistance past threshold, food short", prisonerRow("p", true, domain.PrisonerInteractionRecruit, 5, 12), domain.Known(3.0), domain.PrisonerInteractionRelease},
		{"never recruitable past threshold, food short", prisonerRow("p", false, domain.PrisonerInteractionMaintain, 0, 12), domain.Known(3.0), domain.PrisonerInteractionRelease},
		{"resistance broken keeps recruiting", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 0, 12), domain.Known(3.0), domain.PrisonerInteractionRecruit},
		{"held too briefly keeps recruiting", prisonerRow("p", true, domain.PrisonerInteractionMaintain, 5, 9.9), domain.Known(3.0), domain.PrisonerInteractionRecruit},
		{"food at target keeps feeding", prisonerRow("p", true, domain.PrisonerInteractionRecruit, 5, 12), domain.Known(7.0), ""},
		{"already released is settled", prisonerRow("p", true, domain.PrisonerInteractionRelease, 5, 12), domain.Known(3.0), ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			rows := domain.Known([]PrisonerFacts{c.row})
			deficit, known := PrisonerRecruitDeficit(rows, c.food, p).Value()
			if !known || deficit != (c.want != "") {
				t.Fatalf("deficit %v known %v, want %q", deficit, known, c.want)
			}
			choice := SelectPrisonerInteractionMethod(rows, c.food, p)
			if c.want == "" {
				if choice.Reason != PrisonerNoDeficit {
					t.Fatalf("unexpected choice %+v", choice)
				}
				return
			}
			if choice.Reason != "" || choice.Interaction != c.want {
				t.Fatalf("choice %+v, want %s", choice, c.want)
			}
		})
	}
}

func TestPrisonerReleaseUnknownFactsNeverAuthorize(t *testing.T) {
	p := PrisonerPolicy{ReleaseAfterDays: 10, FoodTargetDays: 7}
	base := prisonerRow("p", true, domain.PrisonerInteractionRecruit, 5, 12)
	noHeld := base
	noHeld.HeldTicks = domain.Unknown[int64]()
	noResistance := base
	noResistance.Resistance = domain.Unknown[float64]()
	for name, tc := range map[string]struct {
		row  PrisonerFacts
		food domain.Fact[float64]
	}{"held unknown": {noHeld, domain.Known(3.0)}, "resistance unknown": {noResistance, domain.Known(3.0)}, "food unknown": {base, domain.Unknown[float64]()}} {
		rows := domain.Known([]PrisonerFacts{tc.row})
		if _, known := PrisonerRecruitDeficit(rows, tc.food, p).Value(); known {
			t.Fatalf("%s: deficit should be unknown", name)
		}
		if choice := SelectPrisonerInteractionMethod(rows, tc.food, p); choice.Reason != PrisonerNoDeficit {
			t.Fatalf("%s: unexpected choice %+v", name, choice)
		}
	}
}

func TestPrisonerReleasePrecedesRecruitByPawnOrder(t *testing.T) {
	p := PrisonerPolicy{ReleaseAfterDays: 10, FoodTargetDays: 7}
	rows := domain.Known([]PrisonerFacts{
		prisonerRow("p2", true, domain.PrisonerInteractionMaintain, 0, 1),
		prisonerRow("p1", false, domain.PrisonerInteractionMaintain, 0, 20),
	})
	choice := SelectPrisonerInteractionMethod(rows, domain.Known(2.0), p)
	if choice.Pawn != "p1" || choice.Interaction != domain.PrisonerInteractionRelease {
		t.Fatalf("unexpected choice %+v", choice)
	}
}
