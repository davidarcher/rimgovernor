package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func cleanRequest(t *testing.T) CleanRequest {
	t.Helper()
	clean, _ := domain.NewClean("cleaner", "filth", domain.Cell{X: 1, Z: 1})
	a, _ := domain.NewCleanAction("clean", clean)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	pawn := CleanPawnFacts{Pawn: "cleaner", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), ExistingJobDef: domain.Known("")}
	filth := CleanFilthFacts{Filth: "filth", SnapshotToken: "filth-cas", Exists: domain.Known(true)}
	return CleanRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: CleanFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Pawn: pawn, Filth: filth, NativeCanTry: domain.Known(true)}}
}

func TestCleanAdmission(t *testing.T) {
	r := cleanRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateClean(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

// TestCleanAdmitsCachedPawnRowBehindPreparedTick is the #306 shape: the
// executor's second inspection under a running window is served the pawn
// row the first read (tick 12) cached, while the first preview (tick 13)
// prepared the action and raised the minimum; the second preview (tick 15)
// anchors the admission, so the older pawn row is fresh evidence.
func TestCleanAdmitsCachedPawnRowBehindPreparedTick(t *testing.T) {
	r := cleanRequest(t)
	var err error
	if r.Progress, err = r.Progress.Prepare(r.Current, 13); err != nil {
		t.Fatal(err)
	}
	r.MinimumTick, r.Facts.PawnTick, r.Facts.PreviewTick = 13, 12, 15
	if d := EvaluateClean(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
	}
}

func TestCleanDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*CleanRequest)
	}{
		{"zero action", func(r *CleanRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *CleanRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *CleanRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *CleanRequest) { r.MinimumTick = 14 }},
		{"negative minimum", func(r *CleanRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *CleanRequest) { r.Facts.PreviewTick = 11 }},
		{"negative pawn tick", func(r *CleanRequest) { r.Facts.PawnTick = -1 }},
		{"prepared past preview", func(r *CleanRequest) { r.Progress, _ = r.Progress.Prepare(r.Current, 14) }},
		{"zero generation", func(r *CleanRequest) { r.Current.Native = 0 }},
		{"native", func(r *CleanRequest) { r.Current.Native++ }},
		{"colony", func(r *CleanRequest) { r.Current.Colony = "other" }},
		{"load", func(r *CleanRequest) { r.Current.Load = "other" }},
		{"map", func(r *CleanRequest) { r.Current.Map++ }},
		{"plan", func(r *CleanRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *CleanRequest) { r.Current.Revision++ }},
		{"pawn CAS", func(r *CleanRequest) { r.Facts.Pawn.SnapshotToken = "" }},
		{"filth CAS", func(r *CleanRequest) { r.Facts.Filth.SnapshotToken = "" }},
		{"wrong pawn", func(r *CleanRequest) { r.Facts.Pawn.Pawn = "other" }},
		{"wrong filth", func(r *CleanRequest) { r.Facts.Filth.Filth = "other" }},
		{"pawn dead", func(r *CleanRequest) { r.Facts.Pawn.Dead = domain.Known(true) }},
		{"pawn downed", func(r *CleanRequest) { r.Facts.Pawn.Downed = domain.Known(true) }},
		{"pawn drafted", func(r *CleanRequest) { r.Facts.Pawn.Drafted = domain.Known(true) }},
		{"pawn mental state", func(r *CleanRequest) { r.Facts.Pawn.MentalState = domain.Known(true) }},
		{"pawn already cleaning", func(r *CleanRequest) { r.Facts.Pawn.ExistingJobDef = domain.Known("Clean") }},
		{"filth unknown exists", func(r *CleanRequest) { r.Facts.Filth.Exists = domain.Unknown[bool]() }},
		{"filth vanished", func(r *CleanRequest) { r.Facts.Filth.Exists = domain.Known(false) }},
		{"preview refusal", func(r *CleanRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *CleanRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := cleanRequest(t)
			c.change(&r)
			d := EvaluateClean(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

func eligibleCleaner(pawn domain.PawnID) CleanCandidateFacts {
	return CleanCandidateFacts{Pawn: pawn, Dead: knownFalse(), Downed: knownFalse(), Drafted: knownFalse(),
		MentalState: knownFalse(), PlayerForced: knownFalse(), NeedsTend: knownFalse(), Bleeding: knownFalse(),
		CleaningEnabled: knownTrue()}
}

func TestSelectCleanPicksTopFilthAndLowestEligiblePawn(t *testing.T) {
	filth := []UpkeepFilth{{ID: "dirt"}, {ID: "mud"}}
	pawns := []CleanCandidateFacts{eligibleCleaner("z"), eligibleCleaner("a")}
	target, pawn, ok := SelectClean(filth, pawns)
	if !ok || target.ID != "dirt" || pawn != "a" {
		t.Fatal(target, pawn, ok)
	}
}

func TestSelectCleanExcludesIneligiblePawns(t *testing.T) {
	filth := []UpkeepFilth{{ID: "dirt"}}
	base := eligibleCleaner("a")
	for _, mutate := range []func(*CleanCandidateFacts){
		func(p *CleanCandidateFacts) { p.Dead = knownTrue() },
		func(p *CleanCandidateFacts) { p.Downed = knownTrue() },
		func(p *CleanCandidateFacts) { p.Drafted = knownTrue() },
		func(p *CleanCandidateFacts) { p.MentalState = knownTrue() },
		func(p *CleanCandidateFacts) { p.PlayerForced = knownTrue() },
		func(p *CleanCandidateFacts) { p.NeedsTend = knownTrue() },
		func(p *CleanCandidateFacts) { p.Bleeding = knownTrue() },
		func(p *CleanCandidateFacts) { p.CleaningEnabled = knownFalse() },
		func(p *CleanCandidateFacts) { p.CleaningEnabled = domain.Unknown[bool]() },
	} {
		p := base
		mutate(&p)
		if _, _, ok := SelectClean(filth, []CleanCandidateFacts{p}); ok {
			t.Fatal("ineligible cleaner selected", p)
		}
	}
}

func TestSelectCleanRequiresFilthAndPawns(t *testing.T) {
	if _, _, ok := SelectClean(nil, []CleanCandidateFacts{eligibleCleaner("a")}); ok {
		t.Fatal("selected with no filth")
	}
	if _, _, ok := SelectClean([]UpkeepFilth{{ID: "dirt"}}, nil); ok {
		t.Fatal("selected with no pawns")
	}
}
