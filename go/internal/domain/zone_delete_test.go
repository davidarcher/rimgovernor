package domain

import "testing"

func TestZoneDeleteIdentity(t *testing.T) {
	if _, err := NewZoneDelete("", "token"); err == nil {
		t.Fatal("expected empty zone to be rejected")
	}
	if _, err := NewZoneDelete("Zone_7", ""); err == nil {
		t.Fatal("expected empty before token to be rejected")
	}
	c, err := NewZoneDelete("Zone_7", "token")
	if err != nil || c.Zone() != "Zone_7" || c.BeforeToken() != "token" {
		t.Fatal(c, err)
	}
	action, err := NewZoneDeleteAction("a", c)
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := action.ZoneDelete(); !ok || got != c || action.Kind() != ZoneDeleteAction {
		t.Fatal("zone delete action does not carry its value")
	}
	if _, err := NewZoneDeleteAction("a", ZoneDelete{zone: "Zone_7"}); err == nil {
		t.Fatal("expected a non-canonical value to be rejected")
	}
	plan, err := NewPlan("p", 1, []Action{action})
	if err != nil || plan.Actions()[0] != action {
		t.Fatal(plan, err)
	}
}
