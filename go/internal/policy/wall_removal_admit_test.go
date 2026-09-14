package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func wallRemovalRequest(t *testing.T) WallRemovalRequest {
	t.Helper()
	removal, _ := domain.NewWallRemoval("original-wall", "", 0, 1, 1, 0, false, false, "")
	a, _ := domain.NewWallRemovalAction("demolition", removal)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	facts := WallRemovalFacts{
		Snapshot:        s,
		ObservationTick: 12,
		TargetIdentity:  domain.Known("original-wall"),
		SiteEligible:    domain.Known(true),
	}
	return WallRemovalRequest{Action: a, Progress: p, Current: s, Facts: facts}
}

func backupWallRemovalRequest(t *testing.T) WallRemovalRequest {
	t.Helper()
	removal, _ := domain.NewWallRemoval("", "backup", 2, 1, 1, 0, false, false, "")
	a, _ := domain.NewWallRemovalAction("backup-removal", removal)
	backup, _ := domain.NewBuilding("Wall", domain.Cell{X: 2, Z: 1}, domain.North, "BlocksGranite")
	backupAction, _ := domain.NewBuildingAction("backup", backup)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{backupAction, a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	facts := WallRemovalFacts{
		Snapshot:        s,
		ObservationTick: 12,
		TargetIdentity:  domain.Known("backup-wall-thing"),
		SiteEligible:    domain.Known(true),
	}
	return WallRemovalRequest{Action: a, Progress: p, Current: s, BackupIdentity: "backup-wall-thing", Facts: facts}
}

func TestWallRemovalAdmission(t *testing.T) {
	for _, request := range []func(*testing.T) WallRemovalRequest{wallRemovalRequest, backupWallRemovalRequest} {
		r := request(t)
		original := r.Progress
		for _, prepared := range []bool{false, true} {
			if prepared {
				var err error
				r.Progress, err = r.Progress.Prepare(r.Current, 11)
				if err != nil {
					t.Fatal(err)
				}
			}
			if d := EvaluateWallRemoval(r); !d.Admitted || len(d.Refused) != 0 {
				t.Fatal(d)
			}
		}
		if original.View().Stage != domain.Pending {
			t.Fatal("mutated progress")
		}
	}
}

func TestWallRemovalDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*WallRemovalRequest)
	}{
		{"zero action", func(r *WallRemovalRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *WallRemovalRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *WallRemovalRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"zero generation", func(r *WallRemovalRequest) { r.Current.Native = 0 }},
		{"native", func(r *WallRemovalRequest) { r.Current.Native++ }},
		{"direction", func(r *WallRemovalRequest) { r.Current.Direction++ }},
		{"colony", func(r *WallRemovalRequest) { r.Current.Colony = "other" }},
		{"load", func(r *WallRemovalRequest) { r.Current.Load = "other" }},
		{"map", func(r *WallRemovalRequest) { r.Current.Map++ }},
		{"plan", func(r *WallRemovalRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *WallRemovalRequest) { r.Current.Revision++ }},
		{"unknown target", func(r *WallRemovalRequest) { r.Facts.TargetIdentity = domain.Unknown[string]() }},
		{"target changed", func(r *WallRemovalRequest) { r.Facts.TargetIdentity = domain.Known("different-wall") }},
		{"unknown site", func(r *WallRemovalRequest) { r.Facts.SiteEligible = domain.Unknown[bool]() }},
		{"site ineligible", func(r *WallRemovalRequest) { r.Facts.SiteEligible = domain.Known(false) }},
		{"stale observation", func(r *WallRemovalRequest) { r.Facts.ObservationTick = -1 }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := wallRemovalRequest(t)
			c.change(&r)
			d := EvaluateWallRemoval(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

func TestBackupWallRemovalRequiresMatchingBackupIdentity(t *testing.T) {
	r := backupWallRemovalRequest(t)
	r.BackupIdentity = "a-different-backup"
	if d := EvaluateWallRemoval(r); d.Admitted {
		t.Fatal("admitted mismatched backup identity", d)
	}
	r = backupWallRemovalRequest(t)
	r.BackupIdentity = ""
	if d := EvaluateWallRemoval(r); d.Admitted {
		t.Fatal("admitted empty backup identity", d)
	}
}
