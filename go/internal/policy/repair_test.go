package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func repairRequest(t *testing.T) RepairRequest {
	t.Helper()
	repair, _ := domain.NewRepair("repairer", "wall", domain.Cell{X: 1, Z: 1})
	a, _ := domain.NewRepairAction("repair", repair)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	pawn := RepairPawnFacts{Pawn: "repairer", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
	structure := RepairStructureFacts{Structure: "wall", SnapshotToken: "wall-cas", Exists: domain.Known(true), Damaged: domain.Known(true)}
	return RepairRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: RepairFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Pawn: pawn, Structure: structure, NativeCanTry: domain.Known(true)}}
}

func TestRepairAdmission(t *testing.T) {
	r := repairRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateRepair(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestRepairDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*RepairRequest)
	}{
		{"zero action", func(r *RepairRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *RepairRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *RepairRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *RepairRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *RepairRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *RepairRequest) { r.Facts.PreviewTick = 11 }},
		{"zero generation", func(r *RepairRequest) { r.Current.Native = 0 }},
		{"native", func(r *RepairRequest) { r.Current.Native++ }},
		{"direction", func(r *RepairRequest) { r.Current.Direction++ }},
		{"colony", func(r *RepairRequest) { r.Current.Colony = "other" }},
		{"load", func(r *RepairRequest) { r.Current.Load = "other" }},
		{"map", func(r *RepairRequest) { r.Current.Map++ }},
		{"plan", func(r *RepairRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *RepairRequest) { r.Current.Revision++ }},
		{"pawn CAS", func(r *RepairRequest) { r.Facts.Pawn.SnapshotToken = "" }},
		{"structure CAS", func(r *RepairRequest) { r.Facts.Structure.SnapshotToken = "" }},
		{"wrong pawn", func(r *RepairRequest) { r.Facts.Pawn.Pawn = "other" }},
		{"wrong structure", func(r *RepairRequest) { r.Facts.Structure.Structure = "other" }},
		{"pawn dead", func(r *RepairRequest) { r.Facts.Pawn.Dead = domain.Known(true) }},
		{"pawn downed", func(r *RepairRequest) { r.Facts.Pawn.Downed = domain.Known(true) }},
		{"pawn drafted", func(r *RepairRequest) { r.Facts.Pawn.Drafted = domain.Known(true) }},
		{"pawn mental state", func(r *RepairRequest) { r.Facts.Pawn.MentalState = domain.Known(true) }},
		{"pawn player forced", func(r *RepairRequest) { r.Facts.Pawn.PlayerForced = domain.Known(true) }},
		{"pawn queued", func(r *RepairRequest) { r.Facts.Pawn.QueuedJobs = domain.Known(uint32(1)) }},
		{"pawn unknown queue", func(r *RepairRequest) { r.Facts.Pawn.QueuedJobs = domain.Unknown[uint32]() }},
		{"pawn already repairing", func(r *RepairRequest) { r.Facts.Pawn.ExistingJobDef = domain.Known("Repair") }},
		{"structure unknown exists", func(r *RepairRequest) { r.Facts.Structure.Exists = domain.Unknown[bool]() }},
		{"structure vanished", func(r *RepairRequest) { r.Facts.Structure.Exists = domain.Known(false) }},
		{"structure unknown damaged", func(r *RepairRequest) { r.Facts.Structure.Damaged = domain.Unknown[bool]() }},
		{"structure already repaired", func(r *RepairRequest) { r.Facts.Structure.Damaged = domain.Known(false) }},
		{"preview refusal", func(r *RepairRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *RepairRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := repairRequest(t)
			c.change(&r)
			d := EvaluateRepair(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

func eligibleRepairer(pawn domain.PawnID) RepairCandidateFacts {
	return RepairCandidateFacts{Pawn: pawn, Dead: knownFalse(), Downed: knownFalse(), Drafted: knownFalse(),
		MentalState: knownFalse(), PlayerForced: knownFalse(), NeedsTend: knownFalse(), Bleeding: knownFalse(),
		ConstructionEnabled: knownTrue()}
}

func TestSelectRepairPicksTopStructureAndLowestEligiblePawn(t *testing.T) {
	structures := []UpkeepStructure{{ID: "wall"}, {ID: "door"}}
	pawns := []RepairCandidateFacts{eligibleRepairer("z"), eligibleRepairer("a")}
	structure, pawn, ok := SelectRepair(structures, pawns)
	if !ok || structure.ID != "wall" || pawn != "a" {
		t.Fatal(structure, pawn, ok)
	}
}

func TestSelectRepairExcludesIneligiblePawns(t *testing.T) {
	structures := []UpkeepStructure{{ID: "wall"}}
	base := eligibleRepairer("a")
	for _, mutate := range []func(*RepairCandidateFacts){
		func(p *RepairCandidateFacts) { p.Dead = knownTrue() },
		func(p *RepairCandidateFacts) { p.Downed = knownTrue() },
		func(p *RepairCandidateFacts) { p.Drafted = knownTrue() },
		func(p *RepairCandidateFacts) { p.MentalState = knownTrue() },
		func(p *RepairCandidateFacts) { p.PlayerForced = knownTrue() },
		func(p *RepairCandidateFacts) { p.NeedsTend = knownTrue() },
		func(p *RepairCandidateFacts) { p.Bleeding = knownTrue() },
		func(p *RepairCandidateFacts) { p.ConstructionEnabled = knownFalse() },
		func(p *RepairCandidateFacts) { p.ConstructionEnabled = domain.Unknown[bool]() },
	} {
		p := base
		mutate(&p)
		if _, _, ok := SelectRepair(structures, []RepairCandidateFacts{p}); ok {
			t.Fatal("ineligible repairer selected", p)
		}
	}
}

func TestSelectRepairRequiresStructuresAndPawns(t *testing.T) {
	if _, _, ok := SelectRepair(nil, []RepairCandidateFacts{eligibleRepairer("a")}); ok {
		t.Fatal("selected with no structures")
	}
	if _, _, ok := SelectRepair([]UpkeepStructure{{ID: "wall"}}, nil); ok {
		t.Fatal("selected with no pawns")
	}
}
