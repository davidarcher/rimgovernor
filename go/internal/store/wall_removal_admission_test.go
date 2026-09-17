package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func wallRemovalStoreFixture(t *testing.T) (*Store, string, WallRemovalAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "wall_removal.db")
	s := open(t, path)
	removal, _ := domain.NewWallRemoval("original-wall", "", 0, 1, 1, 0, false, false, "")
	a, _ := domain.NewWallRemovalAction("demolition", removal)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := WallRemovalAdmission{Snapshot: snapshot, Tick: 12, Original: "original-wall", TargetIdentity: "original-wall", SiteEligible: true}
	return s, path, v
}

func TestWallRemovalAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := wallRemovalStoreFixture(t)
	if _, err := s.PrepareWallRemoval(ctx, "plan", "demolition", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.WallRemovalAdmissions) != 1 || state.WallRemovalAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "demolition", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestWallRemovalDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := wallRemovalStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "demolition", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "demolition", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a wall removal action")
	}
}

func TestWallRemovalAdmissionRejectsTargetMismatch(t *testing.T) {
	ctx := context.Background()
	s, _, v := wallRemovalStoreFixture(t)
	v.TargetIdentity = "different-wall"
	if _, err := s.PrepareWallRemoval(ctx, "plan", "demolition", v); err == nil {
		t.Fatal("admission accepted a target mismatched with the original identity")
	}
}

func backupWallRemovalStoreFixture(t *testing.T) (*Store, domain.GenerationSnapshot) {
	t.Helper()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	backup, _ := domain.NewBuilding("Wall", domain.Cell{X: 2, Z: 1}, domain.North, "BlocksGranite")
	backupAction, _ := domain.NewBuildingAction("backup", backup)
	removal, _ := domain.NewWallRemoval("", "backup", 2, 1, 1, 0, false, false, "")
	removalAction, _ := domain.NewWallRemovalAction("backup-removal", removal)
	deps := []domain.ActionDependency{{Action: "backup-removal", Requires: "backup"}}
	plan, err := domain.NewPlan("plan", 1, []domain.Action{backupAction, removalAction}, deps...)
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	scope := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	return s, scope
}

func completeBackupWall(t *testing.T, s *Store, scope domain.GenerationSnapshot, tick domain.Tick, identity string) {
	t.Helper()
	ctx := context.Background()
	if _, err := s.Prepare(ctx, "plan", "backup", scope, tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "backup", scope, tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "backup", Attempt: 1, Snapshot: scope, Tick: tick + 1, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch, Construction: &domain.ConstructionIdentity{Origin: "blueprint", Current: identity}}, scope); err != nil {
		t.Fatal(err)
	}
}

func TestBackupWallRemovalAdmissionRequiresCompletedBackup(t *testing.T) {
	ctx := context.Background()
	s, scope := backupWallRemovalStoreFixture(t)
	v := WallRemovalAdmission{Snapshot: scope, Tick: 12, BackupOf: "backup", TargetIdentity: "backup-thing", SiteEligible: true}
	if _, err := s.PrepareWallRemoval(ctx, "plan", "backup-removal", v); err == nil {
		t.Fatal("admission accepted before the backup wall completed")
	}
	completeBackupWall(t, s, scope, 12, "backup-thing")
	v.Tick = 14
	if _, err := s.PrepareWallRemoval(ctx, "plan", "backup-removal", v); err != nil {
		t.Fatal(err)
	}
}

func TestBackupWallRemovalAdmissionRejectsIdentityMismatch(t *testing.T) {
	ctx := context.Background()
	s, scope := backupWallRemovalStoreFixture(t)
	completeBackupWall(t, s, scope, 12, "backup-thing")
	v := WallRemovalAdmission{Snapshot: scope, Tick: 14, BackupOf: "backup", TargetIdentity: "a-different-thing", SiteEligible: true}
	if _, err := s.PrepareWallRemoval(ctx, "plan", "backup-removal", v); err == nil {
		t.Fatal("admission accepted a target identity that does not match the completed backup")
	}
}
