package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func husbandryRequest(t *testing.T) HusbandryRequest {
	t.Helper()
	train, _ := domain.NewHusbandry("animal", domain.HusbandryTrain, "Trainability_Advanced")
	a, _ := domain.NewHusbandryAction("husbandry-1", train)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	animal := HusbandryAnimalFacts{Animal: "animal", SnapshotToken: "animal-cas", Dead: domain.Known(false), CanTrain: domain.Known(true), Learned: domain.Known(false), SafeToSlaughter: domain.Known(true)}
	return HusbandryRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: HusbandryFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Animal: animal, CensusToken: "census-cas", NativeCanTry: domain.Known(true)}}
}

func TestHusbandryAdmission(t *testing.T) {
	r := husbandryRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateHusbandry(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestHusbandryCancellationAdmission(t *testing.T) {
	for _, method := range []domain.HusbandryMethod{domain.HusbandryCancelRelease, domain.HusbandryCancelSlaughter} {
		r := husbandryRequest(t)
		h, err := domain.NewHusbandry("animal", method, "")
		if err != nil {
			t.Fatal(err)
		}
		r.Action, err = domain.NewHusbandryAction(r.Action.ID(), h)
		if err != nil {
			t.Fatal(err)
		}
		plan, err := domain.NewPlan(r.Current.Plan, r.Current.Revision, []domain.Action{r.Action})
		if err != nil {
			t.Fatal(err)
		}
		r.Progress, _ = domain.NewProgress(plan, r.Action.ID())
		r.Facts.Animal.SafeToSlaughter = domain.Known(false)
		r.Facts.Animal.SafeToRelease = domain.Known(false)
		if got := EvaluateHusbandry(r); !got.Admitted {
			t.Fatal(got)
		}
		r.Facts.NativeCanTry = domain.Known(false)
		if got := EvaluateHusbandry(r); got.Admitted {
			t.Fatal("ignored native refusal")
		}
		r.Facts.NativeCanTry = domain.Known(true)
		r.Facts.Snapshot.Load = "old-load"
		if got := EvaluateHusbandry(r); got.Admitted {
			t.Fatal("ignored save/load boundary")
		}
	}
}

func TestHusbandrySlaughterAdmission(t *testing.T) {
	slaughter, _ := domain.NewHusbandry("animal", domain.HusbandrySlaughter, "")
	a, _ := domain.NewHusbandryAction("husbandry-1", slaughter)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	animal := HusbandryAnimalFacts{Animal: "animal", SnapshotToken: "animal-cas", Dead: domain.Known(false), SafeToSlaughter: domain.Known(true)}
	r := HusbandryRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: HusbandryFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Animal: animal, CensusToken: "census-cas", NativeCanTry: domain.Known(true)}}
	if d := EvaluateHusbandry(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
	}
	r.Facts.Animal.SafeToSlaughter = domain.Known(false)
	if d := EvaluateHusbandry(r); d.Admitted || len(d.Refused) != 1 {
		t.Fatal(d)
	}
}

func TestHusbandryDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*HusbandryRequest)
	}{
		{"zero action", func(r *HusbandryRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *HusbandryRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *HusbandryRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *HusbandryRequest) { r.MinimumTick = 14 }},
		{"negative minimum", func(r *HusbandryRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *HusbandryRequest) { r.Facts.PreviewTick = 11 }},
		{"pawn row outrun", func(r *HusbandryRequest) { r.Facts.PreviewTick = 12 + domain.PlanningTickTolerance + 1 }},
		{"prepared past preview", func(r *HusbandryRequest) { r.Progress, _ = r.Progress.Prepare(r.Current, 14) }},
		{"zero generation", func(r *HusbandryRequest) { r.Current.Native = 0 }},
		{"native", func(r *HusbandryRequest) { r.Current.Native++ }},
		{"colony", func(r *HusbandryRequest) { r.Current.Colony = "other" }},
		{"load", func(r *HusbandryRequest) { r.Current.Load = "other" }},
		{"map", func(r *HusbandryRequest) { r.Current.Map++ }},
		{"plan", func(r *HusbandryRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *HusbandryRequest) { r.Current.Revision++ }},
		{"animal CAS", func(r *HusbandryRequest) { r.Facts.Animal.SnapshotToken = "" }},
		{"census CAS", func(r *HusbandryRequest) { r.Facts.CensusToken = "" }},
		{"wrong animal", func(r *HusbandryRequest) { r.Facts.Animal.Animal = "other" }},
		{"unknown dead", func(r *HusbandryRequest) { r.Facts.Animal.Dead = domain.Unknown[bool]() }},
		{"dead", func(r *HusbandryRequest) { r.Facts.Animal.Dead = domain.Known(true) }},
		{"unknown can train", func(r *HusbandryRequest) { r.Facts.Animal.CanTrain = domain.Unknown[bool]() }},
		{"cannot train", func(r *HusbandryRequest) { r.Facts.Animal.CanTrain = domain.Known(false) }},
		{"unknown learned", func(r *HusbandryRequest) { r.Facts.Animal.Learned = domain.Unknown[bool]() }},
		{"already learned", func(r *HusbandryRequest) { r.Facts.Animal.Learned = domain.Known(true) }},
		{"preview refusal", func(r *HusbandryRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *HusbandryRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := husbandryRequest(t)
			c.change(&r)
			d := EvaluateHusbandry(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

func TestHusbandryTameAndReleaseAdmission(t *testing.T) {
	cases := []struct {
		method domain.HusbandryMethod
		facts  func(bool) HusbandryAnimalFacts
	}{
		{domain.HusbandryTame, func(ok bool) HusbandryAnimalFacts {
			return HusbandryAnimalFacts{Animal: "animal", SnapshotToken: "animal-cas", Dead: domain.Known(false), Tameable: domain.Known(ok)}
		}},
		{domain.HusbandryRelease, func(ok bool) HusbandryAnimalFacts {
			return HusbandryAnimalFacts{Animal: "animal", SnapshotToken: "animal-cas", Dead: domain.Known(false), SafeToRelease: domain.Known(ok)}
		}},
		{domain.HusbandryAllowedArea, func(ok bool) HusbandryAnimalFacts {
			return HusbandryAnimalFacts{Animal: "animal", SnapshotToken: "animal-cas", Dead: domain.Known(false), SupportsAreas: domain.Known(ok)}
		}},
		{domain.HusbandryMaster, func(ok bool) HusbandryAnimalFacts {
			return HusbandryAnimalFacts{Animal: "animal", SnapshotToken: "animal-cas", Dead: domain.Known(false), Obedient: domain.Known(ok)}
		}},
		{domain.HusbandryFollowDrafted, func(ok bool) HusbandryAnimalFacts {
			return HusbandryAnimalFacts{Animal: "animal", SnapshotToken: "animal-cas", Dead: domain.Known(false), Obedient: domain.Known(ok)}
		}},
		{domain.HusbandryFollowFieldwork, func(ok bool) HusbandryAnimalFacts {
			return HusbandryAnimalFacts{Animal: "animal", SnapshotToken: "animal-cas", Dead: domain.Known(false), Obedient: domain.Known(ok)}
		}},
	}
	arguments := map[domain.HusbandryMethod]string{domain.HusbandryFollowDrafted: "true", domain.HusbandryFollowFieldwork: "false"}
	for _, c := range cases {
		t.Run(string(c.method), func(t *testing.T) {
			h, _ := domain.NewHusbandry("animal", c.method, arguments[c.method])
			a, _ := domain.NewHusbandryAction("husbandry-1", h)
			plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
			if err != nil {
				t.Fatal(err)
			}
			s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
			p, _ := domain.NewProgress(plan, a.ID())
			r := HusbandryRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: HusbandryFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Animal: c.facts(true), CensusToken: "census-cas", NativeCanTry: domain.Known(true)}}
			if d := EvaluateHusbandry(r); !d.Admitted || len(d.Refused) != 0 {
				t.Fatal(d)
			}
			r.Facts.Animal = c.facts(false)
			if d := EvaluateHusbandry(r); d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != NativeIneligible {
				t.Fatal(d)
			}
			// The other method's eligibility fact is never a substitute.
			r.Facts.Animal = HusbandryAnimalFacts{Animal: "animal", SnapshotToken: "animal-cas", Dead: domain.Known(false), SafeToSlaughter: domain.Known(true), CanTrain: domain.Known(true), Learned: domain.Known(false)}
			if d := EvaluateHusbandry(r); d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != UnknownFacts {
				t.Fatal(d)
			}
		})
	}
}

// TestHusbandryAdmitsCachedPawnRowBehindPreparedTick is the #306 shape (#323):
// the executor's second inspection under a running window is served the
// pawn row the first read (tick 12) cached, while the first preview (tick
// 13) prepared the action and raised the minimum; the second preview (tick
// 15) anchors the admission, so the older row is fresh evidence.
func TestHusbandryAdmitsCachedPawnRowBehindPreparedTick(t *testing.T) {
	r := husbandryRequest(t)
	var err error
	if r.Progress, err = r.Progress.Prepare(r.Current, 13); err != nil {
		t.Fatal(err)
	}
	r.MinimumTick, r.Facts.PawnTick, r.Facts.PreviewTick = 13, 12, 15
	if d := EvaluateHusbandry(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
	}
}
