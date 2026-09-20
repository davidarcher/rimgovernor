package bridge

import "testing"

func TestWallUpgradeSiteEligibility(t *testing.T) {
	site := WallUpgradeSite{TargetID: "wall", TargetPresent: true}
	if !site.Eligible() {
		t.Fatal("present unblocked site ineligible")
	}
	site.Blocker = "roof support"
	if site.Eligible() {
		t.Fatal("blocker bypassed native safety")
	}
	site.Blocker, site.TargetPresent = "", false
	if site.Eligible() {
		t.Fatal("absent target eligible")
	}
}
