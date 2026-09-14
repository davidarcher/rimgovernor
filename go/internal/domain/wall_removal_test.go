package domain

import "testing"

func TestWallRemovalRequiresOriginalXorBackupPrerequisite(t *testing.T) {
	if _, e := NewWallRemoval("", "", 1, 1, 1, 0, false, false, ""); e == nil {
		t.Fatal("accepted removal with neither original nor backup prerequisite")
	}
	if _, e := NewWallRemoval("original-wall", "backup", 1, 1, 1, 0, false, false, ""); e == nil {
		t.Fatal("accepted removal with both original and backup prerequisite")
	}
	if _, e := NewWallRemoval("original-wall", "", -1, 1, 1, 0, false, false, ""); e == nil {
		t.Fatal("accepted negative removal site")
	}
	if _, e := NewWallRemoval("original-wall", "", 1, 1, 1, 0, false, false, "not a valid material\x00"); e == nil {
		t.Fatal("accepted invalid material text")
	}
	if _, e := NewWallRemoval("original-wall", "", 1, 1, 1, 0, false, false, "BlocksGranite"); e != nil {
		t.Fatal(e)
	}
	if _, e := NewWallRemoval("", "backup", 1, 1, 1, 0, false, false, ""); e != nil {
		t.Fatal(e)
	}
}

func TestWallRemovalActionRejectsSelfPrerequisite(t *testing.T) {
	removal, e := NewWallRemoval("", "removal", 1, 1, 1, 0, false, false, "")
	if e != nil {
		t.Fatal(e)
	}
	if _, e = NewWallRemovalAction("removal", removal); e == nil {
		t.Fatal("accepted self-referential backup prerequisite")
	}
}

func backupWallBundle(t *testing.T, deps ...ActionDependency) (PlanSpec, []Action) {
	t.Helper()
	backup, e := NewBuilding("Wall", Cell{2, 1}, North, "BlocksGranite")
	if e != nil {
		t.Fatal(e)
	}
	backupAction, e := NewBuildingAction("backup", backup)
	if e != nil {
		t.Fatal(e)
	}
	demolition, e := NewWallRemoval("original-wall", "", 0, 1, 1, 0, false, false, "")
	if e != nil {
		t.Fatal(e)
	}
	demolitionAction, e := NewWallRemovalAction("demolition", demolition)
	if e != nil {
		t.Fatal(e)
	}
	permanent, e := NewBuilding("Wall", Cell{0, 1}, North, "BlocksGranite")
	if e != nil {
		t.Fatal(e)
	}
	permanentAction, e := NewBuildingAction("permanent", permanent)
	if e != nil {
		t.Fatal(e)
	}
	backupRemoval, e := NewWallRemoval("", "backup", 2, 1, 1, 0, false, false, "")
	if e != nil {
		t.Fatal(e)
	}
	backupRemovalAction, e := NewWallRemovalAction("backup-removal", backupRemoval)
	if e != nil {
		t.Fatal(e)
	}
	actions := []Action{backupAction, demolitionAction, permanentAction, backupRemovalAction}
	p, e := NewPlan("wall-upgrade", 1, actions, deps...)
	if e != nil {
		t.Fatal(e)
	}
	return p, actions
}

func TestWallRemovalBundleAdmitsFullStagedTopology(t *testing.T) {
	deps := []ActionDependency{{"permanent", "demolition"}}
	p, _ := backupWallBundle(t, deps...)
	if len(p.Actions()) != 4 {
		t.Fatal("bundle lost an action")
	}
}

func TestBackupWallRemovalRequiresPrecedingBackupWall(t *testing.T) {
	demolition, e := NewWallRemoval("", "missing-backup", 2, 1, 1, 0, false, false, "")
	if e != nil {
		t.Fatal(e)
	}
	action, e := NewWallRemovalAction("backup-removal", demolition)
	if e != nil {
		t.Fatal(e)
	}
	if _, e = NewPlan("wall-upgrade", 1, []Action{action}); e == nil {
		t.Fatal("accepted backup removal with no preceding backup wall")
	}
	nonWall, e := NewBuilding("Bed", Cell{2, 1}, North, "")
	if e != nil {
		t.Fatal(e)
	}
	nonWallAction, e := NewBuildingAction("backup", nonWall)
	if e != nil {
		t.Fatal(e)
	}
	if _, e = NewPlan("wall-upgrade", 1, []Action{nonWallAction, action}); e == nil {
		t.Fatal("accepted backup removal referencing a non-wall building")
	}
}

func TestWallRemovalBackupPrerequisiteParticipatesInCycleDetection(t *testing.T) {
	_, actions := backupWallBundle(t)
	// A dependency forcing the backup wall to wait on its own removal, combined
	// with the automatic backup-removal-requires-backup-wall edge, is a cycle.
	if _, e := NewPlan("wall-upgrade", 1, actions, ActionDependency{Action: "backup", Requires: "backup-removal"}); e == nil {
		t.Fatal("accepted cyclic backup wall dependency")
	}
}
