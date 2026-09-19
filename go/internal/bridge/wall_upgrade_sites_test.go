package bridge

import "testing"

func TestWallUpgradeSiteForeignDesignationIsObservedState(t *testing.T) {
	site := WallUpgradeSite{TargetID: "wall", TargetPresent: true, PlayerOwned: true}
	if !site.Eligible() {
		t.Fatal("foreign designation blocked explicit wall adoption")
	}
	site.Blocker = "roof support"
	if site.Eligible() {
		t.Fatal("foreign designation bypassed native safety")
	}
	site.Blocker, site.TargetPresent = "", false
	if site.Eligible() {
		t.Fatal("absent target eligible")
	}
}
